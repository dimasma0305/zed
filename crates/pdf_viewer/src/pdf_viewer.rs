use std::{path::PathBuf, sync::Arc, time::Duration};

use anyhow::{Context as _, Result, ensure};
use editor::Editor;
use file_icons::FileIcons;
use futures::future;
#[cfg(not(any(test, feature = "test-support")))]
use futures::io::{AsyncReadExt as _, AsyncWriteExt as _};
use gpui::{
    Bounds, CursorStyle, Entity, EventEmitter, FocusHandle, Focusable, MouseButton, MouseDownEvent,
    MouseMoveEvent, MouseUpEvent, PinchEvent, Point, Render, RenderImage, ScrollDelta,
    ScrollHandle, ScrollWheelEvent, Size, Task, WeakEntity, actions, canvas, img, point, size,
};
use pdf_renderer::{MAX_FILE_BYTES, RenderedPage};
#[cfg(not(any(test, feature = "test-support")))]
use pdf_renderer::{MAX_OUTPUT_BYTES, WORKER_ARGUMENT};
use project::{Project, ProjectEntryId, ProjectPath};
use settings::Settings as _;
use ui::{Tooltip, WithScrollbar, prelude::*};
use util::ResultExt as _;
#[cfg(not(any(test, feature = "test-support")))]
use util::command::{Stdio, new_command};
use workspace::{
    ItemId, ItemSettings, Pane, ToolbarItemEvent, ToolbarItemLocation, ToolbarItemView, Workspace,
    WorkspaceId, delete_unloaded_items,
    item::{Item, ItemBufferKind, ItemEvent, ItemHandle, ProjectItem, SerializableItem},
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
const ZOOM_STEP: f32 = 1.1;
const SCROLL_LINE_MULTIPLIER: f32 = 20.0;
const ZOOM_RENDER_DELAY: Duration = Duration::from_millis(100);
const PAGE_PADDING: f32 = 16.0;
const RENDER_TIMEOUT: Duration = Duration::from_secs(30);
const FILE_LOAD_TIMEOUT: Duration = Duration::from_secs(120);

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

#[cfg(any(test, feature = "test-support"))]
pub struct PdfViewTestState {
    pub page_index: u32,
    pub page_count: u32,
    pub zoom: f32,
    pub loading: bool,
    pub error: Option<String>,
    pub has_bytes: bool,
    pub has_image: bool,
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
    viewport_bounds: Option<Bounds<Pixels>>,
    image: Option<Arc<RenderImage>>,
    scroll_handle: ScrollHandle,
    loading: bool,
    error: Option<SharedString>,
    page_editor: Option<Entity<Editor>>,
    page_input_error: Option<SharedString>,
    zoom_editor: Option<Entity<Editor>>,
    zoom_input_error: Option<SharedString>,
    last_mouse_position: Option<Point<Pixels>>,
    generation: u64,
    render_task: Option<Task<()>>,
    load_task: Option<Task<()>>,
    zoom_task: Option<Task<()>>,
}

impl PdfView {
    #[cfg(any(test, feature = "test-support"))]
    pub fn test_state(&self) -> PdfViewTestState {
        PdfViewTestState {
            page_index: self.page_index,
            page_count: self.page_count,
            zoom: self.effective_zoom(),
            loading: self.loading,
            error: self.error.as_ref().map(ToString::to_string),
            has_bytes: self.bytes.is_some(),
            has_image: self.image.is_some(),
        }
    }
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
        cx.subscribe_in(&project, window, |this, _, event, window, cx| {
            if matches!(
                event,
                project::Event::DisconnectedFromRemote { .. }
                    | project::Event::DisconnectedFromHost
                    | project::Event::Closed
            ) {
                this.generation += 1;
                this.load_task = None;
                this.render_task = None;
                this.zoom_task = None;
                this.last_mouse_position = None;
                this.bytes = None;
                this.clear_image(window);
                this.loading = false;
                this.error =
                    Some("Remote project is disconnected. Reconnect and reload the PDF".into());
                cx.notify();
            }
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
            viewport_bounds: None,
            image: None,
            scroll_handle: ScrollHandle::new(),
            loading: false,
            error: None,
            page_editor: None,
            page_input_error: None,
            zoom_editor: None,
            zoom_input_error: None,
            last_mouse_position: None,
            generation: 0,
            render_task: None,
            load_task: None,
            zoom_task: None,
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
        let generation = self.generation;
        self.load_task = None;
        self.render_task = None;
        self.zoom_task = None;
        self.last_mouse_position = None;
        self.bytes = None;
        self.loading = true;
        self.error = None;
        self.clear_image(window);
        self.pending_page.get_or_insert(self.page_index);
        self.page_index = 0;
        self.page_count = 0;
        let path = self.document.read(cx).path.clone();
        let project = self.project.clone();
        let timeout = cx.background_executor().timer(FILE_LOAD_TIMEOUT);
        self.load_task = Some(cx.spawn_in(window, async move |this, cx| {
            let load = async {
                let (file_system, absolute_path, worktree) =
                    project.read_with(cx, |project, cx| {
                        Ok::<_, anyhow::Error>((
                            project.fs().clone(),
                            project
                                .absolute_path(&path, cx)
                                .context("PDF path is unavailable")?,
                            project
                                .worktree_for_id(path.worktree_id, cx)
                                .context("PDF worktree is unavailable")?,
                        ))
                    })?;
                if !worktree.read_with(cx, |worktree, _| worktree.is_local()) {
                    let bytes = project
                        .update(cx, |project, cx| {
                            project.read_file_bytes(path.clone(), MAX_FILE_BYTES as u64, cx)
                        })
                        .await?;
                    return Ok(bytes);
                }
                let metadata = file_system
                    .metadata(&absolute_path)
                    .await?
                    .context("PDF file was deleted")?;
                ensure!(
                    !metadata.is_dir && !metadata.is_fifo,
                    "The selected PDF path is not a regular file"
                );
                ensure!(
                    metadata.len <= MAX_FILE_BYTES as u64,
                    "PDF exceeds the 128 MiB file limit"
                );
                let reader = file_system.open_sync(&absolute_path).await?;
                cx.background_executor()
                    .spawn(async move { pdf_renderer::read_document(reader) })
                    .await
            };
            let result = match future::select(Box::pin(load), Box::pin(timeout)).await {
                future::Either::Left((result, _)) => result,
                future::Either::Right(_) => Err(anyhow::anyhow!(
                    "PDF loading exceeded 120 seconds. Check the connection and retry"
                )),
            };
            this.update_in(cx, |this, window, cx| {
                if this.generation != generation {
                    return;
                }
                match result {
                    Ok(bytes) => {
                        this.document.update(cx, |document, cx| {
                            document.entry_id = this
                                .project
                                .read(cx)
                                .entry_for_path(&document.path, cx)
                                .map(|entry| entry.id);
                        });
                        this.bytes = Some(Arc::new(bytes));
                        this.request_render(window, cx);
                        cx.emit(PdfViewEvent::TitleChanged);
                    }
                    Err(error) => {
                        this.loading = false;
                        this.error = Some(format!("{error:#}").into());
                    }
                }
                cx.notify();
            })
            .log_err();
        }));
        cx.notify();
    }

    fn effective_zoom(&self) -> f32 {
        if self.fit_to_page {
            self.page_size
                .map(|page| {
                    ((self.viewport_size.width - px(PAGE_PADDING * 2.0)) / page.width)
                        .min((self.viewport_size.height - px(PAGE_PADDING * 2.0)) / page.height)
                        .clamp(MIN_ZOOM, MAX_ZOOM)
                })
                .unwrap_or(1.0)
        } else {
            self.zoom
        }
    }

    fn request_render(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.zoom_task = None;
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
        self.last_mouse_position = None;
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
        self.zoom_editor = None;
        self.zoom_input_error = None;
        self.page_input_error = None;
        cx.notify();
    }

    fn confirm_page(&mut self, _: &menu::Confirm, window: &mut Window, cx: &mut Context<Self>) {
        if let Some(editor) = self.zoom_editor.as_ref() {
            let percentage = editor.read(cx).text(cx);
            match percentage.trim().trim_end_matches('%').parse::<f32>() {
                Ok(percentage)
                    if percentage.is_finite()
                        && (MIN_ZOOM * 100.0..=MAX_ZOOM * 100.0).contains(&percentage) =>
                {
                    self.zoom_editor = None;
                    self.zoom_input_error = None;
                    self.focus_handle.focus(window, cx);
                    self.set_zoom(percentage / 100.0, window, cx);
                }
                _ => self.zoom_input_error = Some("Enter a zoom from 10% to 800%".into()),
            }
            cx.notify();
            return;
        }
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
        if self.zoom_editor.take().is_some() {
            self.zoom_input_error = None;
            self.focus_handle.focus(window, cx);
            cx.notify();
            return;
        }
        if self.page_editor.take().is_none() {
            cx.propagate();
            return;
        }
        self.page_input_error = None;
        self.focus_handle.focus(window, cx);
        cx.notify();
    }

    fn set_zoom(&mut self, zoom: f32, window: &mut Window, cx: &mut Context<Self>) {
        if !self.update_zoom(zoom, None) {
            return;
        }
        self.request_render(window, cx);
    }

    fn page_origin(&self, zoom: f32) -> Point<Pixels> {
        let width = self.page_size.unwrap_or_default().width * zoom;
        point(
            ((width + px(PAGE_PADDING * 2.0)).max(self.viewport_size.width) - width) / 2.0,
            px(PAGE_PADDING),
        )
    }

    fn clamp_scroll_offset(&self, offset: Point<Pixels>) -> Point<Pixels> {
        let page = self.page_size.unwrap_or_default();
        let zoom = self.effective_zoom();
        let maximum = point(
            (page.width * zoom + px(PAGE_PADDING * 2.0) - self.viewport_size.width).max(px(0.0)),
            (page.height * zoom + px(PAGE_PADDING * 2.0) - self.viewport_size.height).max(px(0.0)),
        );
        point(
            offset.x.clamp(-maximum.x, px(0.0)),
            offset.y.clamp(-maximum.y, px(0.0)),
        )
    }

    fn update_zoom(&mut self, zoom: f32, center: Option<Point<Pixels>>) -> bool {
        if !zoom.is_finite() || zoom <= 0.0 {
            return false;
        }
        let previous_zoom = self.effective_zoom();
        let zoom = zoom.clamp(MIN_ZOOM, MAX_ZOOM);
        if zoom == previous_zoom && !self.fit_to_page {
            return false;
        }
        let center = center.or_else(|| self.viewport_bounds.map(|bounds| bounds.center()));
        let page_point = center.zip(self.viewport_bounds).map(|(center, bounds)| {
            let center = center - bounds.origin;
            let page_point =
                (center - self.scroll_handle.offset() - self.page_origin(previous_zoom))
                    / previous_zoom;
            (center, page_point)
        });
        self.zoom = zoom;
        self.fit_to_page = false;
        if let Some((center, page_point)) = page_point {
            let offset = center - self.page_origin(zoom) - page_point * zoom;
            self.scroll_handle
                .set_offset(self.clamp_scroll_offset(offset));
        }
        true
    }

    fn gesture_zoom(
        &mut self,
        zoom: f32,
        center: Point<Pixels>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.page_size.is_none()
            || self.bytes.is_none()
            || self.image.is_none()
            || !self.update_zoom(zoom, Some(center))
        {
            return;
        }
        // Avoid restarting a renderer process for every small trackpad delta.
        self.generation += 1;
        self.render_task = None;
        self.loading = false;
        self.zoom_task = Some(cx.spawn_in(window, async move |this, cx| {
            cx.background_executor().timer(ZOOM_RENDER_DELAY).await;
            this.update_in(cx, |this, window, cx| this.request_render(window, cx))
                .log_err();
        }));
        cx.notify();
    }

    fn handle_scroll_wheel(
        &mut self,
        event: &ScrollWheelEvent,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if event.modifiers.control || event.modifiers.platform {
            let delta: f32 = match event.delta {
                ScrollDelta::Pixels(pixels) => pixels.y.into(),
                ScrollDelta::Lines(lines) => lines.y * SCROLL_LINE_MULTIPLIER,
            };
            if delta.is_finite() && delta != 0.0 {
                let factor = if delta > 0.0 {
                    1.0 + delta * 0.01
                } else {
                    1.0 / (1.0 - delta * 0.01)
                };
                self.gesture_zoom(self.effective_zoom() * factor, event.position, window, cx);
            }
            cx.stop_propagation();
        }
    }

    fn handle_pinch(&mut self, event: &PinchEvent, window: &mut Window, cx: &mut Context<Self>) {
        if event.delta.is_finite() && event.delta != 0.0 && event.delta > -1.0 {
            self.gesture_zoom(
                self.effective_zoom() * (1.0 + event.delta),
                event.position,
                window,
                cx,
            );
        }
        cx.stop_propagation();
    }

    fn handle_mouse_down(
        &mut self,
        event: &MouseDownEvent,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self.image.is_some() {
            self.last_mouse_position = Some(event.position);
        }
        self.focus_handle.focus(window, cx);
        cx.notify();
    }

    fn handle_mouse_up(&mut self, _: &MouseUpEvent, _: &mut Window, cx: &mut Context<Self>) {
        self.last_mouse_position = None;
        cx.notify();
    }

    fn handle_mouse_move(
        &mut self,
        event: &MouseMoveEvent,
        _: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if !matches!(
            event.pressed_button,
            Some(MouseButton::Left | MouseButton::Middle)
        ) {
            if self.last_mouse_position.take().is_some() {
                cx.notify();
            }
        } else if let Some(previous) = self.last_mouse_position {
            self.last_mouse_position = Some(event.position);
            self.scroll_handle.set_offset(
                self.clamp_scroll_offset(self.scroll_handle.offset() + event.position - previous),
            );
            cx.notify();
        }
    }

    fn edit_zoom(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        let percentage = format!("{:.0}", self.effective_zoom() * 100.0);
        let editor = cx.new(|cx| {
            let mut editor = Editor::single_line(window, cx);
            editor.set_text(percentage, window, cx);
            editor.select_all(&editor::actions::SelectAll, window, cx);
            editor
        });
        editor.focus_handle(cx).focus(window, cx);
        self.page_editor = None;
        self.page_input_error = None;
        self.zoom_editor = Some(editor);
        self.zoom_input_error = None;
        cx.notify();
    }
    fn zoom_in(&mut self, _: &ZoomIn, window: &mut Window, cx: &mut Context<Self>) {
        self.set_zoom(self.effective_zoom() * ZOOM_STEP, window, cx);
    }
    fn zoom_out(&mut self, _: &ZoomOut, window: &mut Window, cx: &mut Context<Self>) {
        self.set_zoom(self.effective_zoom() / ZOOM_STEP, window, cx);
    }
    fn reset_zoom(&mut self, _: &ResetZoom, window: &mut Window, cx: &mut Context<Self>) {
        self.set_zoom(1.0, window, cx);
        self.scroll_handle.set_offset(point(px(0.0), px(0.0)));
        cx.notify();
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

#[cfg(not(any(test, feature = "test-support")))]
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

#[cfg(any(test, feature = "test-support"))]
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

impl PdfView {
    fn render_toolbar(&mut self, _window: &mut Window, cx: &mut Context<Self>) -> AnyElement {
        let page_label = if self.page_count == 0 {
            "Page — / —".to_string()
        } else {
            format!("Page {} / {}", self.page_index + 1, self.page_count)
        };
        let zoom_label = format!("{:.0}%", self.effective_zoom() * 100.0);
        h_flex()
            .key_context("PdfViewer")
            .gap_1()
            .on_action(cx.listener(Self::confirm_page))
            .on_action(cx.listener(Self::cancel_page))
            .child(
                IconButton::new("pdf-previous", IconName::ChevronLeft)
                    .icon_size(IconSize::Small)
                    .aria_label("Previous PDF page")
                    .disabled(self.page_index == 0 || self.page_count == 0)
                    .tooltip(|_, cx| Tooltip::for_action("Previous Page", &PreviousPage, cx))
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
                    .style(ButtonStyle::Subtle)
                    .disabled(self.page_count == 0)
                    .tooltip(|_, cx| Tooltip::for_action("Go to Page", &GoToPage, cx))
                    .on_click(
                        cx.listener(|this, _, window, cx| this.go_to_page(&GoToPage, window, cx)),
                    )
                    .into_any_element()
            })
            .child(
                IconButton::new("pdf-next", IconName::ChevronRight)
                    .icon_size(IconSize::Small)
                    .aria_label("Next PDF page")
                    .disabled(self.page_count == 0 || self.page_index + 1 >= self.page_count)
                    .tooltip(|_, cx| Tooltip::for_action("Next Page", &NextPage, cx))
                    .on_click(
                        cx.listener(|this, _, window, cx| this.next_page(&NextPage, window, cx)),
                    ),
            )
            .child(
                IconButton::new("pdf-zoom-out", IconName::Dash)
                    .icon_size(IconSize::Small)
                    .aria_label("Zoom out")
                    .tooltip(|_, cx| Tooltip::for_action("Zoom Out", &ZoomOut, cx))
                    .on_click(
                        cx.listener(|this, _, window, cx| this.zoom_out(&ZoomOut, window, cx)),
                    ),
            )
            .child(if let Some(editor) = self.zoom_editor.as_ref() {
                h_flex()
                    .w(px(64.0))
                    .child(editor.clone())
                    .into_any_element()
            } else {
                h_flex()
                    .id("pdf-zoom-percentage")
                    .px_1()
                    .cursor_pointer()
                    .child(Label::new(zoom_label).size(LabelSize::Small))
                    .tooltip(|_, cx| {
                        Tooltip::with_meta(
                            "Edit Zoom",
                            None,
                            "Ctrl-wheel or pinch to zoom. Right-click to reset to 100%.",
                            cx,
                        )
                    })
                    .on_click(cx.listener(|this, _, window, cx| this.edit_zoom(window, cx)))
                    .on_mouse_down(
                        MouseButton::Right,
                        cx.listener(|this, _, window, cx| this.reset_zoom(&ResetZoom, window, cx)),
                    )
                    .into_any_element()
            })
            .child(
                IconButton::new("pdf-zoom-in", IconName::Plus)
                    .icon_size(IconSize::Small)
                    .aria_label("Zoom in")
                    .tooltip(|_, cx| Tooltip::for_action("Zoom In", &ZoomIn, cx))
                    .on_click(cx.listener(|this, _, window, cx| this.zoom_in(&ZoomIn, window, cx))),
            )
            .child(
                IconButton::new("pdf-fit", IconName::Maximize)
                    .icon_size(IconSize::Small)
                    .tooltip(|_, cx| Tooltip::for_action("Fit Page", &FitToPage, cx))
                    .toggle_state(self.fit_to_page)
                    .on_click(cx.listener(|this, _, window, cx| this.fit(&FitToPage, window, cx))),
            )
            .child(
                IconButton::new("pdf-reload", IconName::RotateCw)
                    .icon_size(IconSize::Small)
                    .tooltip(|_, cx| Tooltip::for_action("Reload PDF", &Reload, cx))
                    .on_click(
                        cx.listener(|this, _, window, cx| this.reload_pdf(&Reload, window, cx)),
                    ),
            )
            .when(self.loading, |element| {
                element.child(Label::new("Loading PDF…").color(Color::Muted))
            })
            .into_any_element()
    }
}

impl Render for PdfView {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let this = cx.entity().downgrade();
        let zoom = self.effective_zoom();
        let page = self.page_size.unwrap_or_default();
        let content_width =
            (page.width * zoom + px(PAGE_PADDING * 2.0)).max(self.viewport_size.width);
        let content_height =
            (page.height * zoom + px(PAGE_PADDING * 2.0)).max(self.viewport_size.height);
        let content = div()
            .id("pdf-page-scroll")
            .absolute()
            .inset_0()
            .size_full()
            .overflow_scroll()
            .track_scroll(&self.scroll_handle)
            .child(
                div()
                    .id("pdf-page-content")
                    .p(px(PAGE_PADDING))
                    .w(content_width)
                    .h(content_height)
                    .flex()
                    .justify_center()
                    .items_start()
                    .cursor(if self.last_mouse_position.is_some() {
                        CursorStyle::ClosedHand
                    } else {
                        CursorStyle::OpenHand
                    })
                    .on_scroll_wheel(cx.listener(Self::handle_scroll_wheel))
                    .on_pinch(cx.listener(Self::handle_pinch))
                    .on_mouse_down(MouseButton::Left, cx.listener(Self::handle_mouse_down))
                    .on_mouse_down(MouseButton::Middle, cx.listener(Self::handle_mouse_down))
                    .on_mouse_up(MouseButton::Left, cx.listener(Self::handle_mouse_up))
                    .on_mouse_up(MouseButton::Middle, cx.listener(Self::handle_mouse_up))
                    .on_mouse_up_out(MouseButton::Left, cx.listener(Self::handle_mouse_up))
                    .on_mouse_up_out(MouseButton::Middle, cx.listener(Self::handle_mouse_up))
                    .on_mouse_move(cx.listener(Self::handle_mouse_move))
                    .when_some(self.image.clone(), |element, image| {
                        element.child(
                            img(image)
                                .w(page.width * zoom)
                                .h(page.height * zoom)
                                .flex_shrink_0(),
                        )
                    }),
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
            .when_some(self.page_input_error.clone(), |element, error| {
                element.child(Label::new(error).color(Color::Error))
            })
            .when_some(self.zoom_input_error.clone(), |element, error| {
                element.child(Label::new(error).color(Color::Error))
            })
            .child(
                div()
                    .id("pdf-viewport")
                    .relative()
                    .flex_1()
                    .min_w_0()
                    .min_h_0()
                    .overflow_hidden()
                    .child(
                        canvas(
                            move |bounds, window, cx| {
                                this.update(cx, |this, cx| {
                                    this.viewport_bounds = Some(bounds);
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
                    })
                    // Scrollbars belong to the viewport, outside the page's scroll transform.
                    .when(self.error.is_none(), |viewport| {
                        viewport.custom_scrollbars(
                            ui::Scrollbars::new(ui::ScrollAxes::Both)
                                .tracked_scroll_handle(&self.scroll_handle)
                                .tracked_entity(cx.entity_id()),
                            window,
                            cx,
                        )
                    }),
            )
    }
}

#[derive(Default)]
pub struct PdfViewToolbarControls {
    view: Option<WeakEntity<PdfView>>,
    subscription: Option<gpui::Subscription>,
}
impl EventEmitter<ToolbarItemEvent> for PdfViewToolbarControls {}
impl ToolbarItemView for PdfViewToolbarControls {
    fn set_active_pane_item(
        &mut self,
        item: Option<&dyn ItemHandle>,
        _: &mut Window,
        cx: &mut Context<Self>,
    ) -> ToolbarItemLocation {
        self.view = None;
        self.subscription = None;
        if let Some(view) = item.and_then(|item| item.downcast::<PdfView>()) {
            self.subscription = Some(cx.observe(&view, |_, _, cx| cx.notify()));
            self.view = Some(view.downgrade());
            cx.notify();
            ToolbarItemLocation::PrimaryRight
        } else {
            ToolbarItemLocation::Hidden
        }
    }
}
impl Render for PdfViewToolbarControls {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        self.view
            .as_ref()
            .and_then(WeakEntity::upgrade)
            .map(|view| view.update(cx, |view, cx| view.render_toolbar(window, cx)))
            .unwrap_or_else(|| div().into_any_element())
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
            callback(ItemEvent::UpdateBreadcrumbs);
        }
    }
    fn capability(&self, _: &App) -> language::Capability {
        language::Capability::ReadOnly
    }
    fn buffer_kind(&self, _: &App) -> ItemBufferKind {
        ItemBufferKind::Singleton
    }
    fn breadcrumb_location(&self, cx: &App) -> ToolbarItemLocation {
        if editor::EditorSettings::get_global(cx).toolbar.breadcrumbs {
            ToolbarItemLocation::PrimaryLeft
        } else {
            ToolbarItemLocation::Hidden
        }
    }
    fn breadcrumbs(
        &self,
        cx: &App,
    ) -> Option<(Vec<language::HighlightedText>, Option<gpui::Font>)> {
        let project = self.project.read(cx);
        let document = self.document.read(cx);
        let mut path = document.path.path.to_rel_path_buf();
        if project.visible_worktrees(cx).count() > 1
            && let Some(worktree) = project.worktree_for_id(document.path.worktree_id, cx)
        {
            path = worktree.read(cx).root_name().join(&path);
        }
        Some((
            vec![language::HighlightedText {
                text: path.display(project.path_style(cx)).to_string().into(),
                highlights: vec![],
            }],
            None,
        ))
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
    use fs::{FakeFs, Fs};
    use gpui::{Modifiers, TestAppContext, VisualTestContext};
    use settings::SettingsStore;
    use std::path::Path;

    const TWO_PAGES: &[u8] = include_bytes!("../../pdf_renderer/tests/fixtures/two-pages.pdf");

    fn draw_window(cx: &mut VisualTestContext) {
        cx.update(|window, cx| {
            window.refresh();
            window.draw(cx).clear(cx);
        });
        cx.run_until_parked();
    }

    struct TestViewer {
        view: Entity<PdfView>,
    }
    impl Render for TestViewer {
        fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
            let toolbar = self
                .view
                .update(cx, |view, cx| view.render_toolbar(window, cx));
            v_flex()
                .size_full()
                .child(toolbar)
                .child(div().flex_1().min_h_0().child(self.view.clone()))
        }
    }
    fn add_view(
        cx: &mut TestAppContext,
        build: impl FnOnce(&mut Window, &mut Context<PdfView>) -> PdfView,
    ) -> (Entity<PdfView>, &mut VisualTestContext) {
        let (host, cx) = cx.add_window_view(|window, cx| {
            let view = cx.new(|cx| build(window, cx));
            cx.observe(&view, |_, _, cx| cx.notify()).detach();
            TestViewer { view }
        });
        (host.read_with(cx, |host, _| host.view.clone()), cx)
    }

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
        fs.create_dir(Path::new("/root"))
            .await
            .expect("test root should be created");
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
    async fn draws_scrollable_pages_and_validates_page_input(cx: &mut TestAppContext) {
        let (project, document) = open_pdf(TWO_PAGES, cx).await;
        let (view, cx) = add_view(cx, |window, cx| {
            PdfView::new(document, project, 0, window, cx)
        });
        cx.simulate_resize(size(px(500.0), px(400.0)));
        draw_window(cx);
        view.update_in(cx, |view, window, cx| {
            view.focus_handle.focus(window, cx);
            view.set_zoom(4.0, window, cx);
        });
        cx.run_until_parked();
        draw_window(cx);
        view.read_with(cx, |view, _| {
            let maximum = view.scroll_handle.max_offset();
            assert!(
                maximum.x > px(0.0),
                "the enlarged page must scroll horizontally"
            );
            assert!(
                maximum.y > px(0.0),
                "the enlarged page must scroll vertically"
            );
        });
        cx.dispatch_action(GoToPage);
        draw_window(cx);
        view.update_in(cx, |view, window, cx| {
            view.page_editor
                .as_ref()
                .expect("page input")
                .update(cx, |editor, cx| editor.set_text("0", window, cx));
        });
        cx.dispatch_action(menu::Confirm);
        view.read_with(cx, |view, _| {
            assert!(view.page_input_error.is_some());
            assert_eq!(view.page_index, 0);
        });
        view.update_in(cx, |view, window, cx| {
            view.page_editor
                .as_ref()
                .expect("page input")
                .update(cx, |editor, cx| editor.set_text("2", window, cx));
        });
        cx.dispatch_action(menu::Confirm);
        cx.run_until_parked();
        draw_window(cx);
        view.read_with(cx, |view, _| {
            assert_eq!(view.page_index, 1);
            assert!(view.page_editor.is_none());
            assert!(view.error.is_none());
        });
    }

    #[gpui::test]
    async fn scrollbars_stay_at_viewport_edges_after_zoom_and_pan(cx: &mut TestAppContext) {
        let (project, document) = open_pdf(TWO_PAGES, cx).await;
        let (view, cx) = add_view(cx, |window, cx| {
            PdfView::new(document, project, 0, window, cx)
        });
        cx.simulate_resize(size(px(500.0), px(400.0)));
        draw_window(cx);
        view.update_in(cx, |view, window, cx| view.set_zoom(8.0, window, cx));
        cx.run_until_parked();
        draw_window(cx);
        for viewport in [size(px(500.0), px(400.0)), size(px(420.0), px(320.0))] {
            cx.simulate_resize(viewport);
            draw_window(cx);
            view.update(cx, |view, cx| {
                view.scroll_handle.set_offset(point(px(-200.0), px(-300.0)));
                cx.notify();
            });
            draw_window(cx);
            let bounds = view.read_with(cx, |view, _| view.viewport_bounds.expect("viewport"));
            let vertical_track = point(bounds.right() - px(8.0), bounds.bottom() - px(40.0));
            cx.simulate_mouse_move(vertical_track, None, Modifiers::default());
            draw_window(cx);
            cx.simulate_click(vertical_track, Modifiers::default());
            draw_window(cx);
            view.read_with(cx, |view, _| {
                assert!(
                    view.scroll_handle.offset().y < px(-300.0),
                    "vertical scrollbar must remain at the viewport's right edge after panning"
                );
                assert_eq!(view.scroll_handle.offset().x, px(-200.0));
                assert!(view.last_mouse_position.is_none());
            });
            let horizontal_track = point(bounds.right() - px(40.0), bounds.bottom() - px(8.0));
            cx.simulate_mouse_move(horizontal_track, None, Modifiers::default());
            draw_window(cx);
            cx.simulate_click(horizontal_track, Modifiers::default());
            draw_window(cx);
            view.read_with(cx, |view, _| {
                assert!(
                    view.scroll_handle.offset().x < px(-200.0),
                    "horizontal scrollbar must remain at the viewport's bottom edge after panning"
                );
                assert!(view.last_mouse_position.is_none());
            });
        }
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

    #[gpui::test(iterations = 5)]
    async fn wheel_zoom_is_anchored_and_ordinary_scroll_stays_on_the_page(cx: &mut TestAppContext) {
        let (project, document) = open_pdf(TWO_PAGES, cx).await;
        let (view, cx) = add_view(cx, |window, cx| {
            PdfView::new(document, project, 0, window, cx)
        });
        cx.simulate_resize(size(px(500.0), px(400.0)));
        draw_window(cx);
        view.update_in(cx, |view, window, cx| {
            view.set_zoom(3.0, window, cx);
            view.scroll_handle.set_offset(point(px(-200.0), px(-100.0)));
        });
        draw_window(cx);
        let (position, page_point, bitmap) = view.read_with(cx, |view, _| {
            let bounds = view.viewport_bounds.expect("viewport bounds");
            let position = bounds.origin + point(px(250.0), px(150.0));
            let page_point =
                (position - bounds.origin - view.scroll_handle.offset() - view.page_origin(3.0))
                    / 3.0;
            (
                position,
                page_point,
                view.image.clone().expect("page bitmap"),
            )
        });
        cx.simulate_event(ScrollWheelEvent {
            position,
            delta: ScrollDelta::Pixels(point(px(0.0), px(0.5))),
            modifiers: Modifiers {
                control: true,
                ..Default::default()
            },
            ..Default::default()
        });
        view.read_with(cx, |view, _| {
            assert!(
                (view.zoom - 3.015).abs() < 0.0001,
                "fractional pixel deltas must not be rounded"
            );
            let bounds = view.viewport_bounds.expect("viewport bounds");
            let anchored = bounds.origin
                + view.scroll_handle.offset()
                + view.page_origin(view.zoom)
                + page_point * view.zoom;
            assert!((anchored.x - position.x).abs() < px(0.01));
            assert!(
                (anchored.y - position.y).abs() < px(0.01),
                "Ctrl-wheel must not also scroll"
            );
            assert!(Arc::ptr_eq(
                view.image.as_ref().expect("scaled bitmap"),
                &bitmap
            ));
            assert!(view.zoom_task.is_some());
            assert!(!view.fit_to_page);
        });
        draw_window(cx);
        let offset = view.read_with(cx, |view, _| view.scroll_handle.offset());
        cx.simulate_event(ScrollWheelEvent {
            position,
            delta: ScrollDelta::Pixels(point(px(-12.0), px(-30.0))),
            ..Default::default()
        });
        draw_window(cx);
        view.read_with(cx, |view, _| {
            assert_eq!(view.page_index, 0);
            assert!((view.zoom - 3.015).abs() < 0.0001);
            assert!(view.scroll_handle.offset().y < offset.y);
        });
        cx.executor().advance_clock(ZOOM_RENDER_DELAY);
        cx.run_until_parked();
        view.read_with(cx, |view, _| {
            assert!(view.zoom_task.is_none());
            assert!(!view.loading);
            assert!(view.error.is_none());
            assert!(!Arc::ptr_eq(
                view.image.as_ref().expect("sharp bitmap"),
                &bitmap
            ));
        });
    }

    #[gpui::test]
    async fn line_wheel_and_native_pinch_leave_fit_and_respect_zoom_limits(
        cx: &mut TestAppContext,
    ) {
        let (project, document) = open_pdf(TWO_PAGES, cx).await;
        let (view, cx) = add_view(cx, |window, cx| {
            PdfView::new(document, project, 0, window, cx)
        });
        draw_window(cx);
        let position = view.read_with(cx, |view, _| {
            view.viewport_bounds.expect("viewport").center()
        });
        cx.simulate_event(ScrollWheelEvent {
            position,
            delta: ScrollDelta::Lines(point(1.0, 0.0)),
            modifiers: Modifiers {
                control: true,
                ..Default::default()
            },
            ..Default::default()
        });
        assert!(
            view.read_with(cx, |view, _| view.fit_to_page),
            "horizontal-only zoom input is ignored"
        );
        let fit_zoom = view.read_with(cx, |view, _| view.effective_zoom());
        cx.simulate_event(ScrollWheelEvent {
            position,
            delta: ScrollDelta::Lines(point(0.0, 1.0)),
            modifiers: Modifiers {
                platform: true,
                ..Default::default()
            },
            ..Default::default()
        });
        assert!((view.read_with(cx, |view, _| view.zoom) - fit_zoom * 1.2).abs() < 0.0001);
        draw_window(cx);
        cx.simulate_event(ScrollWheelEvent {
            position,
            delta: ScrollDelta::Lines(point(0.0, -1.0)),
            modifiers: Modifiers {
                control: true,
                ..Default::default()
            },
            ..Default::default()
        });
        assert!((view.read_with(cx, |view, _| view.zoom) - fit_zoom).abs() < 0.0001);
        draw_window(cx);
        cx.simulate_event(PinchEvent {
            position,
            delta: 0.1,
            ..Default::default()
        });
        assert!((view.read_with(cx, |view, _| view.zoom) - fit_zoom * 1.1).abs() < 0.0001);
        for delta in [f32::NAN, f32::INFINITY, -1.0, -2.0, 0.0] {
            cx.simulate_event(PinchEvent {
                position,
                delta,
                ..Default::default()
            });
        }
        assert!((view.read_with(cx, |view, _| view.zoom) - fit_zoom * 1.1).abs() < 0.0001);
        view.update_in(cx, |view, window, cx| {
            view.gesture_zoom(1000.0, position, window, cx);
            assert_eq!(view.zoom, MAX_ZOOM);
            view.gesture_zoom(0.0001, position, window, cx);
            assert_eq!(view.zoom, MIN_ZOOM);
            assert_eq!(view.scroll_handle.offset(), point(px(0.0), px(0.0)));
            view.fit(&FitToPage, window, cx);
            assert!(view.fit_to_page);
            assert!(
                view.zoom_task.is_none(),
                "Fit cancels a pending gesture render"
            );
        });
        cx.run_until_parked();
        view.update_in(cx, |view, window, cx| {
            view.reload_pdf(&Reload, window, cx);
            let generation = view.generation;
            view.handle_pinch(
                &PinchEvent {
                    position,
                    delta: 0.1,
                    ..Default::default()
                },
                window,
                cx,
            );
            assert_eq!(
                view.generation, generation,
                "a gesture cannot invalidate a pending file load"
            );
        });
        cx.run_until_parked();
        assert!(view.read_with(cx, |view, _| view.bytes.is_some()
            && view.image.is_some()
            && !view.loading));
    }

    #[gpui::test]
    async fn hand_drag_clamps_and_releasing_outside_ends_panning(cx: &mut TestAppContext) {
        let (project, document) = open_pdf(TWO_PAGES, cx).await;
        let (view, cx) = add_view(cx, |window, cx| {
            PdfView::new(document, project, 0, window, cx)
        });
        cx.simulate_resize(size(px(500.0), px(400.0)));
        draw_window(cx);
        view.update_in(cx, |view, window, cx| view.set_zoom(4.0, window, cx));
        draw_window(cx);
        let position = view.read_with(cx, |view, _| {
            view.viewport_bounds.expect("viewport").center()
        });
        cx.simulate_mouse_down(position, MouseButton::Left, Modifiers::none());
        let initial_offset = view.read_with(cx, |view, _| view.scroll_handle.offset());
        cx.simulate_mouse_move(
            position - point(px(30.0), px(20.0)),
            MouseButton::Left,
            Modifiers::none(),
        );
        view.read_with(cx, |view, _| {
            assert_eq!(view.scroll_handle.offset().y, initial_offset.y - px(20.0))
        });
        cx.simulate_mouse_up(
            point(px(-1.0), px(-1.0)),
            MouseButton::Left,
            Modifiers::none(),
        );
        assert!(view.read_with(cx, |view, _| view.last_mouse_position.is_none()));
        let offset = view.read_with(cx, |view, _| view.scroll_handle.offset());
        cx.simulate_mouse_move(position, None, Modifiers::none());
        assert_eq!(
            view.read_with(cx, |view, _| view.scroll_handle.offset()),
            offset
        );
        view.update(cx, |view, _| {
            view.scroll_handle.set_offset(point(px(-5.0), px(-5.0)))
        });
        draw_window(cx);
        cx.simulate_mouse_down(position, MouseButton::Middle, Modifiers::none());
        cx.simulate_mouse_move(
            position + point(px(30.0), px(20.0)),
            MouseButton::Middle,
            Modifiers::none(),
        );
        assert_eq!(
            view.read_with(cx, |view, _| view.scroll_handle.offset()),
            point(px(0.0), px(0.0))
        );
        cx.simulate_mouse_up(position, MouseButton::Middle, Modifiers::none());
    }

    #[gpui::test]
    async fn zoom_percentage_validates_confirms_and_cancels(cx: &mut TestAppContext) {
        let (project, document) = open_pdf(TWO_PAGES, cx).await;
        let (view, cx) = add_view(cx, |window, cx| {
            PdfView::new(document, project, 0, window, cx)
        });
        draw_window(cx);
        view.update_in(cx, |view, window, cx| view.edit_zoom(window, cx));
        draw_window(cx);
        for invalid in ["0", "801", "NaN", "infinity", "text"] {
            view.update_in(cx, |view, window, cx| {
                view.zoom_editor
                    .as_ref()
                    .expect("zoom editor")
                    .update(cx, |editor, cx| editor.set_text(invalid, window, cx));
            });
            cx.dispatch_action(menu::Confirm);
            assert!(view.read_with(cx, |view, _| view.zoom_input_error.is_some()));
        }
        view.update_in(cx, |view, window, cx| {
            view.zoom_editor
                .as_ref()
                .expect("zoom editor")
                .update(cx, |editor, cx| editor.set_text("125.5%", window, cx));
        });
        cx.dispatch_action(menu::Confirm);
        cx.run_until_parked();
        assert!((view.read_with(cx, |view, _| view.zoom) - 1.255).abs() < 0.0001);
        view.update_in(cx, |view, window, cx| view.edit_zoom(window, cx));
        draw_window(cx);
        cx.dispatch_action(menu::Cancel);
        assert!(view.read_with(cx, |view, _| view.zoom_editor.is_none()));
        view.update_in(cx, |view, window, cx| {
            view.reset_zoom(&ResetZoom, window, cx)
        });
        cx.run_until_parked();
        view.read_with(cx, |view, _| {
            assert_eq!(view.zoom, 1.0);
            assert_eq!(view.scroll_handle.offset(), point(px(0.0), px(0.0)));
        });
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
