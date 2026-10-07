use std::{path::PathBuf, sync::Arc, time::Duration};

use anyhow::{Context as _, Result, ensure};
use editor::Editor;
use file_icons::FileIcons;
use futures::future;
#[cfg(not(test))]
use futures::io::{AsyncReadExt as _, AsyncWriteExt as _};
use gpui::{
    Entity, EventEmitter, FocusHandle, Focusable, Render, RenderImage, ScrollHandle, Size, Task,
    WeakEntity, actions, canvas, img, point, size,
};
use pdf_renderer::{MAX_FILE_BYTES, RenderedPage};
#[cfg(not(test))]
use pdf_renderer::{MAX_OUTPUT_BYTES, WORKER_ARGUMENT};
use project::{Project, ProjectEntryId, ProjectPath};
use settings::Settings as _;
use ui::{Tooltip, WithScrollbar, prelude::*};
use util::ResultExt as _;
#[cfg(not(test))]
use util::command::{Stdio, new_command};
use workspace::{
    ItemId, ItemSettings, Pane, Workspace, WorkspaceId, delete_unloaded_items,
    item::{Item, ItemBufferKind, ItemEvent, ProjectItem, SerializableItem},
};

actions!(
    pdf_viewer,
    [
        /// Show the next PDF page.
        NextPage,
        /// Show the previous PDF page.
        PreviousPage,
        /// Show the first PDF page.
        FirstPage,
        /// Show the last PDF page.
        LastPage,
        /// Enter a PDF page number.
        GoToPage,
        /// Zoom in the PDF page.
        ZoomIn,
        /// Zoom out the PDF page.
        ZoomOut,
        /// Show the PDF page at 100%.
        ResetZoom,
        /// Fit the PDF page in the window.
        FitToPage,
        /// Reload the PDF from disk.
        Reload,
    ]
);

const MIN_ZOOM: f32 = 0.1;
const MAX_ZOOM: f32 = 8.0;
const RENDER_TIMEOUT: Duration = Duration::from_secs(30);

pub struct PdfDocument {
    path: ProjectPath,
    entry_id: Option<ProjectEntryId>,
    entry: Option<worktree::Entry>,
    deleted: bool,
}

impl EventEmitter<()> for PdfDocument {}

impl PdfDocument {
    fn new(project: &Entity<Project>, path: ProjectPath, cx: &mut Context<Self>) -> Self {
        let entry = project.read(cx).entry_for_path(&path, cx).cloned();
        if let Some(worktree) = project.read(cx).worktree_for_id(path.worktree_id, cx) {
            cx.subscribe(&worktree, |this, worktree, event, cx| {
                if !matches!(
                    event,
                    worktree::Event::UpdatedEntries(_)
                        | worktree::Event::DeletedEntry(_)
                        | worktree::Event::Deleted
                ) {
                    return;
                }
                let snapshot = worktree.read(cx).snapshot();
                let entry = this
                    .entry_id
                    .and_then(|id| snapshot.entry_for_id(id))
                    .or_else(|| snapshot.entry_for_path(&this.path.path))
                    .cloned();
                let changed = match (&this.entry, &entry) {
                    (Some(previous), Some(current)) => {
                        previous.path != current.path
                            || previous.mtime != current.mtime
                            || previous.size != current.size
                    }
                    (None, None) => false,
                    _ => true,
                };
                if changed {
                    if let Some(entry) = entry.as_ref() {
                        this.path.path = entry.path.clone();
                        this.entry_id = Some(entry.id);
                    }
                    this.deleted = entry.is_none();
                    this.entry = entry;
                    cx.emit(());
                }
            })
            .detach();
        }
        Self {
            path,
            entry_id: entry.as_ref().map(|entry| entry.id),
            entry,
            deleted: false,
        }
    }
}

impl project::ProjectItem for PdfDocument {
    fn try_open(
        project: &Entity<Project>,
        path: &ProjectPath,
        cx: &mut App,
    ) -> Option<Task<Result<Entity<Self>>>> {
        let absolute_path = project.read(cx).absolute_path(path, cx);
        let extension = path
            .path
            .extension()
            .or_else(|| absolute_path.as_ref()?.extension()?.to_str());
        if !extension.is_some_and(|extension| extension.eq_ignore_ascii_case("pdf")) {
            return None;
        }
        Some(Task::ready(Ok(
            cx.new(|cx| Self::new(project, path.clone(), cx))
        )))
    }

    fn entry_id(&self, _: &App) -> Option<ProjectEntryId> {
        self.entry_id
    }
    fn project_path(&self, _: &App) -> Option<ProjectPath> {
        Some(self.path.clone())
    }
    fn is_dirty(&self) -> bool {
        false
    }
}

pub enum PdfViewEvent {
    TitleChanged,
    Navigated,
}

pub struct PdfView {
    document: Entity<PdfDocument>,
    project: Entity<Project>,
    focus_handle: FocusHandle,
    bytes: Option<Arc<Vec<u8>>>,
    page_index: u32,
    page_count: u32,
    pending_page: Option<u32>,
    zoom: f32,
    fit_to_page: bool,
    page_size: Option<Size<Pixels>>,
    viewport_size: Size<Pixels>,
    image: Option<Arc<RenderImage>>,
    scroll_handle: ScrollHandle,
    loading: bool,
    error: Option<SharedString>,
    page_editor: Option<Entity<Editor>>,
    page_input_error: Option<SharedString>,
    generation: u64,
    render_task: Option<Task<()>>,
    load_task: Option<Task<()>>,
}

impl PdfView {
    fn new(
        document: Entity<PdfDocument>,
        project: Entity<Project>,
        page_index: u32,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        cx.subscribe_in(&document, window, |this, _, _, window, cx| {
            this.load_document(window, cx);
            cx.emit(PdfViewEvent::TitleChanged);
        })
        .detach();
        cx.on_release_in(window, |this, window, _| {
            if let Some(image) = this.image.take() {
                window.drop_image(image).log_err();
            }
        })
        .detach();
        let mut view = Self {
            document,
            project,
            focus_handle: cx.focus_handle(),
            bytes: None,
            page_index: 0,
            page_count: 0,
            pending_page: Some(page_index),
            zoom: 1.0,
            fit_to_page: true,
            page_size: None,
            viewport_size: size(px(800.0), px(600.0)),
            image: None,
            scroll_handle: ScrollHandle::new(),
            loading: false,
            error: None,
            page_editor: None,
            page_input_error: None,
            generation: 0,
            render_task: None,
            load_task: None,
        };
        view.load_document(window, cx);
        view
    }

    fn clear_image(&mut self, window: &mut Window) {
        if let Some(image) = self.image.take() {
            window.drop_image(image).log_err();
        }
    }

    fn load_document(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.generation += 1;
        self.render_task = None;
        self.bytes = None;
        self.loading = true;
        self.error = None;
        self.clear_image(window);
        self.pending_page.get_or_insert(self.page_index);
        self.page_index = 0;
        self.page_count = 0;
        let path = self.document.read(cx).path.clone();
        let project = self.project.clone();
        self.load_task = Some(cx.spawn_in(window, async move |this, cx| {
            let result = async {
                let (file_system, absolute_path, worktree) = project.read_with(cx, |project, cx| {
                    Ok::<_, anyhow::Error>((
                        project.fs().clone(),
                        project.absolute_path(&path, cx).context("PDF path is unavailable")?,
                        project.worktree_for_id(path.worktree_id, cx).context("PDF worktree is unavailable")?,
                    ))
                })?;
                ensure!(worktree.read_with(cx, |worktree, _| worktree.is_local()),
                    "PDF viewing currently supports local files. Download this PDF and open the local copy");
                let metadata = file_system.metadata(&absolute_path).await?.context("PDF file was deleted")?;
                ensure!(!metadata.is_dir && !metadata.is_fifo, "The selected PDF path is not a regular file");
                ensure!(metadata.len <= MAX_FILE_BYTES as u64, "PDF exceeds the 128 MiB file limit");
                let reader = file_system.open_sync(&absolute_path).await?;
                cx.background_executor().spawn(async move { pdf_renderer::read_document(reader) }).await
            }.await;
            this.update_in(cx, |this, window, cx| {
                match result {
                    Ok(bytes) => {
                        this.document.update(cx, |document, cx| {
                            document.entry_id = this.project.read(cx).entry_for_path(&document.path, cx).map(|entry| entry.id);
                        });
                        this.bytes = Some(Arc::new(bytes));
                        this.request_render(window, cx);
                        cx.emit(PdfViewEvent::TitleChanged);
                    }
                    Err(error) => { this.loading = false; this.error = Some(format!("{error:#}").into()); }
                }
                cx.notify();
            }).log_err();
        }));
        cx.notify();
    }

    fn effective_zoom(&self) -> f32 {
        if self.fit_to_page {
            self.page_size
                .map(|page| {
                    ((self.viewport_size.width - px(32.0)) / page.width)
                        .min((self.viewport_size.height - px(32.0)) / page.height)
                        .clamp(MIN_ZOOM, MAX_ZOOM)
                })
                .unwrap_or(1.0)
        } else {
            self.zoom
        }
    }

    fn request_render(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let Some(bytes) = self.bytes.clone() else {
            return;
        };
        self.generation += 1;
        let generation = self.generation;
        let page_index = self.page_index;
        let scale = (self.effective_zoom() * window.scale_factor()).clamp(MIN_ZOOM, 16.0);
        let timeout = cx.background_executor().timer(RENDER_TIMEOUT);
        self.loading = true;
        self.error = None;
        // Cancel the old child before starting a new one, including after rapid navigation.
        self.render_task = None;
        let render = cx.background_spawn(async move {
            match future::select(
                Box::pin(render_in_worker(bytes, page_index, scale)),
                Box::pin(timeout),
            )
            .await
            {
                future::Either::Left((result, _)) => result,
                future::Either::Right(_) => Err(anyhow::anyhow!(
                    "PDF rendering exceeded 30 seconds. Try a smaller document"
                )),
            }
        });
        self.render_task = Some(cx.spawn_in(window, async move |this, cx| {
            let result = render.await;
            this.update_in(cx, |this, window, cx| {
                if this.generation != generation {
                    return;
                }
                this.loading = false;
                match result {
                    Ok(mut page) => {
                        let previous_zoom = this.effective_zoom();
                        this.page_count = page.page_count;
                        this.page_size = Some(size(px(page.page_width), px(page.page_height)));
                        if let Some(pending_page) = this.pending_page.take() {
                            let pending_page = pending_page.min(this.page_count.saturating_sub(1));
                            if pending_page != this.page_index {
                                this.page_index = pending_page;
                                this.request_render(window, cx);
                                cx.emit(PdfViewEvent::Navigated);
                                return;
                            }
                        }
                        // GPUI's image atlas expects BGRA pixels.
                        for pixel in page.rgba.chunks_exact_mut(4) {
                            pixel.swap(0, 2);
                        }
                        match image::RgbaImage::from_raw(page.width, page.height, page.rgba) {
                            Some(buffer) => {
                                this.clear_image(window);
                                this.image =
                                    Some(Arc::new(RenderImage::new(vec![image::Frame::new(
                                        buffer,
                                    )])));
                            }
                            None => {
                                this.error =
                                    Some("The PDF renderer returned an invalid image".into())
                            }
                        }
                        if this.fit_to_page && (this.effective_zoom() - previous_zoom).abs() > 0.01
                        {
                            this.request_render(window, cx);
                        }
                    }
                    Err(error) => {
                        this.clear_image(window);
                        this.error = Some(format!("{error:#}").into());
                    }
                }
                cx.notify();
            })
            .log_err();
        }));
        cx.notify();
    }

    fn navigate_to(&mut self, page_index: u32, window: &mut Window, cx: &mut Context<Self>) {
        if self.page_count == 0 {
            return;
        }
        let page_index = page_index.min(self.page_count - 1);
        if page_index == self.page_index {
            return;
        }
        self.page_index = page_index;
        self.page_size = None;
        self.scroll_handle.set_offset(point(px(0.0), px(0.0)));
        self.clear_image(window);
        self.request_render(window, cx);
        cx.emit(PdfViewEvent::Navigated);
    }

    fn next_page(&mut self, _: &NextPage, window: &mut Window, cx: &mut Context<Self>) {
        self.navigate_to(self.page_index.saturating_add(1), window, cx);
    }
    fn previous_page(&mut self, _: &PreviousPage, window: &mut Window, cx: &mut Context<Self>) {
        self.navigate_to(self.page_index.saturating_sub(1), window, cx);
    }
    fn first_page(&mut self, _: &FirstPage, window: &mut Window, cx: &mut Context<Self>) {
        self.navigate_to(0, window, cx);
    }
    fn last_page(&mut self, _: &LastPage, window: &mut Window, cx: &mut Context<Self>) {
        self.navigate_to(self.page_count.saturating_sub(1), window, cx);
    }
    fn go_to_page(&mut self, _: &GoToPage, window: &mut Window, cx: &mut Context<Self>) {
        if self.page_count == 0 {
            return;
        }
        let editor = cx.new(|cx| {
            let mut editor = Editor::single_line(window, cx);
            editor.set_text((self.page_index + 1).to_string(), window, cx);
            editor.select_all(&editor::actions::SelectAll, window, cx);
            editor
        });
        editor.focus_handle(cx).focus(window, cx);
        self.page_editor = Some(editor);
        self.page_input_error = None;
        cx.notify();
    }

    fn confirm_page(&mut self, _: &menu::Confirm, window: &mut Window, cx: &mut Context<Self>) {
        let Some(editor) = self.page_editor.as_ref() else {
            cx.propagate();
            return;
        };
        match editor.read(cx).text(cx).trim().parse::<u32>() {
            Ok(page) if page > 0 && page <= self.page_count => {
                self.page_editor = None;
                self.page_input_error = None;
                self.focus_handle.focus(window, cx);
                self.navigate_to(page - 1, window, cx);
            }
            _ => {
                self.page_input_error =
                    Some(format!("Enter a page from 1 to {}", self.page_count).into())
            }
        }
        cx.notify();
    }

    fn cancel_page(&mut self, _: &menu::Cancel, window: &mut Window, cx: &mut Context<Self>) {
        if self.page_editor.take().is_none() {
            cx.propagate();
            return;
        }
        self.page_input_error = None;
        self.focus_handle.focus(window, cx);
        cx.notify();
    }

    fn set_zoom(&mut self, zoom: f32, window: &mut Window, cx: &mut Context<Self>) {
        self.zoom = zoom.clamp(MIN_ZOOM, MAX_ZOOM);
        self.fit_to_page = false;
        self.request_render(window, cx);
    }
    fn zoom_in(&mut self, _: &ZoomIn, window: &mut Window, cx: &mut Context<Self>) {
        self.set_zoom(self.effective_zoom() * 1.25, window, cx);
    }
    fn zoom_out(&mut self, _: &ZoomOut, window: &mut Window, cx: &mut Context<Self>) {
        self.set_zoom(self.effective_zoom() / 1.25, window, cx);
    }
    fn reset_zoom(&mut self, _: &ResetZoom, window: &mut Window, cx: &mut Context<Self>) {
        self.set_zoom(1.0, window, cx);
    }
    fn fit(&mut self, _: &FitToPage, window: &mut Window, cx: &mut Context<Self>) {
        self.fit_to_page = true;
        self.scroll_handle.set_offset(point(px(0.0), px(0.0)));
        self.request_render(window, cx);
    }
    fn reload_pdf(&mut self, _: &Reload, window: &mut Window, cx: &mut Context<Self>) {
        self.load_document(window, cx);
    }

    fn absolute_path(&self, cx: &App) -> Option<PathBuf> {
        self.project
            .read(cx)
            .absolute_path(&self.document.read(cx).path, cx)
    }
}

#[cfg(not(test))]
async fn render_in_worker(
    bytes: Arc<Vec<u8>>,
    page_index: u32,
    scale: f32,
) -> Result<RenderedPage> {
    let header = pdf_renderer::request_header(page_index, scale, bytes.len())?;
    let mut child = new_command(std::env::current_exe()?)
        .arg(WORKER_ARGUMENT)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .kill_on_drop(true)
        .spawn()
        .context("Unable to start the PDF renderer")?;
    let mut input = child
        .stdin
        .take()
        .context("PDF renderer input is unavailable")?;
    let output = child
        .stdout
        .take()
        .context("PDF renderer output is unavailable")?;
    let errors = child
        .stderr
        .take()
        .context("PDF renderer errors are unavailable")?;
    let write = async {
        input.write_all(&header).await?;
        input.write_all(&bytes).await?;
        input.close().await
    };
    let read = async {
        let mut bytes = Vec::new();
        output
            .take((MAX_OUTPUT_BYTES + 1) as u64)
            .read_to_end(&mut bytes)
            .await?;
        ensure!(
            bytes.len() <= MAX_OUTPUT_BYTES,
            "PDF renderer output exceeds the bitmap limit"
        );
        Ok::<_, anyhow::Error>(bytes)
    };
    let read_errors = async {
        let mut bytes = Vec::new();
        errors.take(4096).read_to_end(&mut bytes).await?;
        Ok::<_, anyhow::Error>(bytes)
    };
    let (write_result, output, errors) = future::join3(write, read, read_errors).await;
    let output = output?;
    let errors = errors?;
    let status = child.status().await?;
    ensure!(
        status.success(),
        "{}",
        if errors.is_empty() {
            "The PDF renderer stopped unexpectedly".to_string()
        } else {
            String::from_utf8_lossy(&errors).trim().to_owned()
        }
    );
    write_result?;
    pdf_renderer::read_response(&output)
}

#[cfg(test)]
async fn render_in_worker(
    bytes: Arc<Vec<u8>>,
    page_index: u32,
    scale: f32,
) -> Result<RenderedPage> {
    pdf_renderer::render_page(bytes.as_ref().clone(), page_index, scale)
}

impl EventEmitter<PdfViewEvent> for PdfView {}
impl Focusable for PdfView {
    fn focus_handle(&self, _: &App) -> FocusHandle {
        self.focus_handle.clone()
    }
}

impl Render for PdfView {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let page_label = if self.page_count == 0 {
            "Page — / —".to_string()
        } else {
            format!("Page {} / {}", self.page_index + 1, self.page_count)
        };
        let zoom_label = format!("{:.0}%", self.effective_zoom() * 100.0);
        let this = cx.entity().downgrade();
        let zoom = self.effective_zoom();
        let page = self.page_size.unwrap_or_default();
        let content_width = (page.width * zoom + px(32.0)).max(self.viewport_size.width);
        let content_height = (page.height * zoom + px(32.0)).max(self.viewport_size.height);
        let content = div()
            .id("pdf-page-scroll")
            .size_full()
            .overflow_scroll()
            .track_scroll(&self.scroll_handle)
            .child(
                div()
                    .p_4()
                    .w(content_width)
                    .h(content_height)
                    .flex()
                    .justify_center()
                    .items_start()
                    .when_some(self.image.clone(), |element, image| {
                        element.child(
                            img(image)
                                .w(page.width * zoom)
                                .h(page.height * zoom)
                                .flex_shrink_0(),
                        )
                    }),
            )
            .custom_scrollbars(
                ui::Scrollbars::new(ui::ScrollAxes::Both)
                    .tracked_scroll_handle(&self.scroll_handle)
                    .tracked_entity(cx.entity_id()),
                window,
                cx,
            );
        v_flex()
            .key_context("PdfViewer")
            .track_focus(&self.focus_handle)
            .size_full()
            .bg(cx.theme().colors().editor_background)
            .on_action(cx.listener(Self::next_page))
            .on_action(cx.listener(Self::previous_page))
            .on_action(cx.listener(Self::first_page))
            .on_action(cx.listener(Self::last_page))
            .on_action(cx.listener(Self::go_to_page))
            .on_action(cx.listener(Self::confirm_page))
            .on_action(cx.listener(Self::cancel_page))
            .on_action(cx.listener(Self::zoom_in))
            .on_action(cx.listener(Self::zoom_out))
            .on_action(cx.listener(Self::reset_zoom))
            .on_action(cx.listener(Self::fit))
            .on_action(cx.listener(Self::reload_pdf))
            .child(
                h_flex()
                    .flex_wrap()
                    .gap_1()
                    .p_2()
                    .border_b_1()
                    .border_color(cx.theme().colors().border)
                    .child(
                        IconButton::new("pdf-previous", IconName::ChevronLeft)
                            .aria_label("Previous PDF page")
                            .disabled(self.page_index == 0 || self.page_count == 0)
                            .tooltip(|_, cx| {
                                Tooltip::for_action("Previous Page", &PreviousPage, cx)
                            })
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.previous_page(&PreviousPage, window, cx)
                            })),
                    )
                    .child(if let Some(editor) = self.page_editor.as_ref() {
                        h_flex()
                            .w(px(80.0))
                            .child(editor.clone())
                            .into_any_element()
                    } else {
                        Button::new("pdf-page-number", page_label)
                            .disabled(self.page_count == 0)
                            .tooltip(|_, cx| Tooltip::for_action("Go to Page", &GoToPage, cx))
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.go_to_page(&GoToPage, window, cx)
                            }))
                            .into_any_element()
                    })
                    .child(
                        IconButton::new("pdf-next", IconName::ChevronRight)
                            .aria_label("Next PDF page")
                            .disabled(
                                self.page_count == 0 || self.page_index + 1 >= self.page_count,
                            )
                            .tooltip(|_, cx| Tooltip::for_action("Next Page", &NextPage, cx))
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.next_page(&NextPage, window, cx)
                            })),
                    )
                    .child(
                        IconButton::new("pdf-zoom-out", IconName::Dash)
                            .aria_label("Zoom out")
                            .tooltip(|_, cx| Tooltip::for_action("Zoom Out", &ZoomOut, cx))
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.zoom_out(&ZoomOut, window, cx)
                            })),
                    )
                    .child(
                        Button::new("pdf-zoom-reset", zoom_label)
                            .tooltip(|_, cx| Tooltip::for_action("Reset Zoom", &ResetZoom, cx))
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.reset_zoom(&ResetZoom, window, cx)
                            })),
                    )
                    .child(
                        IconButton::new("pdf-zoom-in", IconName::Plus)
                            .aria_label("Zoom in")
                            .tooltip(|_, cx| Tooltip::for_action("Zoom In", &ZoomIn, cx))
                            .on_click(
                                cx.listener(|this, _, window, cx| {
                                    this.zoom_in(&ZoomIn, window, cx)
                                }),
                            ),
                    )
                    .child(
                        Button::new("pdf-fit", "Fit Page")
                            .toggle_state(self.fit_to_page)
                            .on_click(
                                cx.listener(|this, _, window, cx| this.fit(&FitToPage, window, cx)),
                            ),
                    )
                    .child(Button::new("pdf-reload", "Reload").on_click(
                        cx.listener(|this, _, window, cx| this.reload_pdf(&Reload, window, cx)),
                    ))
                    .when(self.loading, |element| {
                        element.child(Label::new("Loading PDF…").color(Color::Muted))
                    }),
            )
            .when_some(self.page_input_error.clone(), |element, error| {
                element.child(Label::new(error).color(Color::Error))
            })
            .child(
                div()
                    .relative()
                    .flex_1()
                    .min_h_0()
                    .overflow_hidden()
                    .child(
                        canvas(
                            move |bounds, window, cx| {
                                this.update(cx, |this, cx| {
                                    if this.viewport_size != bounds.size {
                                        this.viewport_size = bounds.size;
                                        if this.fit_to_page && this.page_size.is_some() {
                                            this.request_render(window, cx);
                                        }
                                    }
                                })
                                .log_err();
                            },
                            |_, _, _, _| {},
                        )
                        .absolute()
                        .size_full(),
                    )
                    .child(if let Some(error) = self.error.clone() {
                        v_flex()
                            .size_full()
                            .items_center()
                            .justify_center()
                            .p_4()
                            .gap_2()
                            .child(Label::new("Unable to view PDF").color(Color::Error))
                            .child(div().max_w(px(640.0)).child(error))
                            .into_any_element()
                    } else {
                        content.into_any_element()
                    }),
            )
    }
}

impl Item for PdfView {
    type Event = PdfViewEvent;
    fn tab_content_text(&self, _: usize, cx: &App) -> SharedString {
        self.absolute_path(cx)
            .and_then(|path| {
                path.file_name()
                    .map(|name| name.to_string_lossy().into_owned())
            })
            .unwrap_or_else(|| "PDF".to_string())
            .into()
    }
    fn tab_tooltip_text(&self, cx: &App) -> Option<SharedString> {
        Some(
            self.absolute_path(cx)?
                .to_string_lossy()
                .into_owned()
                .into(),
        )
    }
    fn tab_icon(&self, _: &Window, cx: &App) -> Option<Icon> {
        ItemSettings::get_global(cx)
            .file_icons
            .then(|| FileIcons::get_icon(&self.absolute_path(cx)?, cx))
            .flatten()
            .map(Icon::from_path)
    }
    fn for_each_project_item(
        &self,
        cx: &App,
        callback: &mut dyn FnMut(gpui::EntityId, &dyn project::ProjectItem),
    ) {
        callback(self.document.entity_id(), self.document.read(cx));
    }
    fn to_item_events(event: &Self::Event, callback: &mut dyn FnMut(ItemEvent)) {
        if matches!(event, PdfViewEvent::TitleChanged) {
            callback(ItemEvent::UpdateTab);
        }
    }
    fn capability(&self, _: &App) -> language::Capability {
        language::Capability::ReadOnly
    }
    fn buffer_kind(&self, _: &App) -> ItemBufferKind {
        ItemBufferKind::Singleton
    }
    fn can_split(&self) -> bool {
        true
    }
    fn has_deleted_file(&self, cx: &App) -> bool {
        self.document.read(cx).deleted
    }
    fn clone_on_split(
        &self,
        _: Option<WorkspaceId>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Task<Option<Entity<Self>>> {
        Task::ready(Some(cx.new(|cx| {
            Self::new(
                self.document.clone(),
                self.project.clone(),
                self.page_index,
                window,
                cx,
            )
        })))
    }
}

impl ProjectItem for PdfView {
    type Item = PdfDocument;
    fn for_project_item(
        project: Entity<Project>,
        _: Option<&Pane>,
        item: Entity<PdfDocument>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        Self::new(item, project, 0, window, cx)
    }
}

impl SerializableItem for PdfView {
    fn serialized_item_kind() -> &'static str {
        "PdfView"
    }
    fn deserialize(
        project: Entity<Project>,
        _: WeakEntity<Workspace>,
        workspace_id: WorkspaceId,
        item_id: ItemId,
        window: &mut Window,
        cx: &mut App,
    ) -> Task<Result<Entity<Self>>> {
        let database = persistence::PdfViewerDb::global(cx);
        window.spawn(cx, async move |cx| {
            let (path, page_index) = database
                .get_pdf_state(item_id, workspace_id)?
                .context("PDF state was not saved")?;
            let (worktree, relative_path) = project
                .update(cx, |project, cx| {
                    project.find_or_create_worktree(path, false, cx)
                })
                .await?;
            let path = ProjectPath {
                worktree_id: worktree.read_with(cx, |worktree, _| worktree.id()),
                path: relative_path,
            };
            cx.update(|window, cx| {
                let document = cx.new(|cx| PdfDocument::new(&project, path, cx));
                cx.new(|cx| Self::new(document, project, page_index.max(0) as u32, window, cx))
            })
        })
    }
    fn serialize(
        &mut self,
        workspace: &mut Workspace,
        item_id: ItemId,
        _: bool,
        cx: &mut Context<Self>,
    ) -> Option<Task<Result<()>>> {
        let workspace_id = workspace.database_id()?;
        let path = self.absolute_path(cx)?;
        let page_index = self.pending_page.unwrap_or(self.page_index) as i64;
        let database = persistence::PdfViewerDb::global(cx);
        Some(cx.background_spawn(async move {
            database
                .save_pdf_state(item_id, workspace_id, path, page_index)
                .await
        }))
    }
    fn cleanup(
        workspace_id: WorkspaceId,
        alive_items: Vec<ItemId>,
        _: &mut Window,
        cx: &mut App,
    ) -> Task<Result<()>> {
        let database = persistence::PdfViewerDb::global(cx);
        delete_unloaded_items(alive_items, workspace_id, "pdf_viewers", &database, cx)
    }
    fn should_serialize(&self, _: &PdfViewEvent) -> bool {
        true
    }
}

pub fn init(cx: &mut App) {
    workspace::register_project_item::<PdfView>(cx);
    workspace::register_serializable_item::<PdfView>(cx);
}

#[cfg(test)]
mod tests {
    use super::*;
    use fs::FakeFs;
    use gpui::TestAppContext;
    use settings::SettingsStore;
    use std::path::Path;

    const TWO_PAGES: &[u8] = include_bytes!("../../pdf_renderer/tests/fixtures/two-pages.pdf");

    async fn open_pdf(
        bytes: &[u8],
        cx: &mut TestAppContext,
    ) -> (Entity<Project>, Entity<PdfDocument>) {
        cx.update(|cx| {
            let settings = SettingsStore::test(cx);
            cx.set_global(settings);
            theme_settings::init(theme::LoadThemes::JustBase, cx);
        });
        let fs = FakeFs::new(cx.executor());
        fs.insert_file("/root/report.PDF", bytes.to_vec()).await;
        let project = Project::test(fs, [Path::new("/root")], cx).await;
        let path = cx.update(|cx| {
            let worktree = project.read(cx).worktrees(cx).next().expect("worktree");
            ProjectPath {
                worktree_id: worktree.read(cx).id(),
                path: util::rel_path::rel_path("report.PDF").into(),
            }
        });
        let document = cx
            .update(|cx| <PdfDocument as project::ProjectItem>::try_open(&project, &path, cx))
            .expect("PDF opener")
            .await
            .expect("PDF document");
        (project, document)
    }

    #[gpui::test]
    async fn opens_pdf_and_navigates_without_overrunning_pages(cx: &mut TestAppContext) {
        let (project, document) = open_pdf(TWO_PAGES, cx).await;
        let cx = cx.add_empty_window();
        let view =
            cx.update(|window, cx| cx.new(|cx| PdfView::new(document, project, 0, window, cx)));
        cx.run_until_parked();
        view.read_with(cx, |view, _| {
            assert_eq!(view.page_count, 2);
            assert!(view.image.is_some());
            assert!(view.error.is_none());
        });
        view.update_in(cx, |view, window, cx| {
            view.previous_page(&PreviousPage, window, cx);
            assert_eq!(view.page_index, 0);
            view.next_page(&NextPage, window, cx);
            view.next_page(&NextPage, window, cx);
            assert_eq!(view.page_index, 1);
        });
        cx.run_until_parked();
        view.read_with(cx, |view, _| {
            assert!(view.image.is_some());
            assert_eq!(view.page_size, Some(size(px(300.0), px(200.0))));
        });
    }

    #[gpui::test]
    async fn malformed_pdf_keeps_a_read_only_tab_with_an_error(cx: &mut TestAppContext) {
        let (project, document) = open_pdf(b"%PDF-1.7\ntruncated", cx).await;
        let cx = cx.add_empty_window();
        let view =
            cx.update(|window, cx| cx.new(|cx| PdfView::new(document, project, 0, window, cx)));
        cx.run_until_parked();
        view.read_with(cx, |view, cx| {
            assert!(view.error.is_some());
            assert!(!view.loading);
            assert!(!view.can_save(cx));
            assert_eq!(view.capability(cx), language::Capability::ReadOnly);
            assert_eq!(view.tab_content_text(0, cx), "report.PDF");
        });
    }

    #[gpui::test]
    async fn restored_page_is_clamped_and_split_navigation_is_independent(cx: &mut TestAppContext) {
        let (project, document) = open_pdf(TWO_PAGES, cx).await;
        let cx = cx.add_empty_window();
        let view =
            cx.update(|window, cx| cx.new(|cx| PdfView::new(document, project, 99, window, cx)));
        cx.run_until_parked();
        assert_eq!(view.read_with(cx, |view, _| view.page_index), 1);
        let split = view
            .update_in(cx, |view, window, cx| view.clone_on_split(None, window, cx))
            .await
            .expect("split");
        cx.run_until_parked();
        split.update_in(cx, |view, window, cx| {
            view.first_page(&FirstPage, window, cx)
        });
        cx.run_until_parked();
        assert_eq!(view.read_with(cx, |view, _| view.page_index), 1);
        assert_eq!(split.read_with(cx, |view, _| view.page_index), 0);
        split.update_in(cx, |view, window, cx| {
            view.set_zoom(100.0, window, cx);
            assert_eq!(view.zoom, MAX_ZOOM);
            view.set_zoom(0.001, window, cx);
            assert_eq!(view.zoom, MIN_ZOOM);
        });
        cx.run_until_parked();
    }
}

mod persistence {
    use db::{
        query,
        sqlez::{domain::Domain, thread_safe_connection::ThreadSafeConnection},
        sqlez_macros::sql,
    };
    use std::path::PathBuf;
    use workspace::{ItemId, WorkspaceDb, WorkspaceId};

    pub struct PdfViewerDb(ThreadSafeConnection);
    impl Domain for PdfViewerDb {
        const NAME: &str = stringify!(PdfViewerDb);
        const MIGRATIONS: &[&str] = &[sql!(
            CREATE TABLE pdf_viewers (
                workspace_id INTEGER,
                item_id INTEGER UNIQUE,
                pdf_path BLOB,
                page_index INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(workspace_id, item_id),
                FOREIGN KEY(workspace_id) REFERENCES workspaces(workspace_id) ON DELETE CASCADE
            ) STRICT;
        )];
    }
    db::static_connection!(PdfViewerDb, [WorkspaceDb]);
    impl PdfViewerDb {
        query! {
            pub async fn save_pdf_state(item_id: ItemId, workspace_id: WorkspaceId, pdf_path: PathBuf, page_index: i64) -> Result<()> {
                INSERT OR REPLACE INTO pdf_viewers(item_id, workspace_id, pdf_path, page_index) VALUES (?, ?, ?, ?)
            }
        }
        query! {
            pub fn get_pdf_state(item_id: ItemId, workspace_id: WorkspaceId) -> Result<Option<(PathBuf, i64)>> {
                SELECT pdf_path, page_index FROM pdf_viewers WHERE item_id = ? AND workspace_id = ?
            }
        }
    }
}
