use std::{fmt::Write as _, path::PathBuf, time::Duration};

use anyhow::{Context as _, Result};
use editor::Editor;
use file_icons::FileIcons;
use futures::future;
use gpui::{Entity, EventEmitter, FocusHandle, Focusable, Render, Task, WeakEntity, actions};
use language::{Buffer, Capability, language_settings::SoftWrap};
use project::{Project, ProjectEntryId, ProjectPath};
use settings::Settings as _;
use ui::{ContextMenu, DropdownMenu, prelude::*};
use util::ResultExt as _;
use workspace::{
    ItemId, ItemSettings, Pane, Workspace, WorkspaceId, delete_unloaded_items,
    item::{Item, ItemBufferKind, ItemEvent, ProjectItem, SerializableItem},
};

actions!(
    binary_viewer,
    [
        /// Inspect the active file's saved bytes in a read-only binary tab.
        OpenInBinaryViewer,
        /// Read the next 64 KiB of the file.
        NextPage,
        /// Read the previous 64 KiB of the file.
        PreviousPage,
        /// Inspect the beginning of the file.
        FirstPage,
        /// Inspect the last page of the file.
        LastPage,
        /// Enter an absolute byte offset, in decimal or hexadecimal.
        GoToOffset,
        /// Reload the file's saved bytes.
        Reload,
    ]
);

pub const PAGE_BYTES: u64 = 64 * 1024;
const LOAD_TIMEOUT: Duration = Duration::from_secs(120);

#[derive(Clone, Copy, Default, PartialEq, Eq)]
enum ViewMode {
    #[default]
    Hex,
    Text,
}

#[derive(Clone, Copy, Default, PartialEq, Eq)]
enum TextEncoding {
    #[default]
    Utf8,
    Utf16LittleEndian,
    Utf16BigEndian,
    Windows1252,
    Latin1,
}

impl TextEncoding {
    const ALL: [Self; 5] = [
        Self::Utf8,
        Self::Utf16LittleEndian,
        Self::Utf16BigEndian,
        Self::Windows1252,
        Self::Latin1,
    ];

    fn label(self) -> &'static str {
        match self {
            Self::Utf8 => "UTF-8",
            Self::Utf16LittleEndian => "UTF-16 LE",
            Self::Utf16BigEndian => "UTF-16 BE",
            Self::Windows1252 => "Windows-1252",
            Self::Latin1 => "Latin-1",
        }
    }

    fn from_label(label: &str) -> Self {
        Self::ALL
            .into_iter()
            .find(|encoding| encoding.label() == label)
            .unwrap_or_default()
    }

    fn decode(self, bytes: &[u8], offset: u64) -> String {
        let (decoded, _) = match self {
            Self::Latin1 => return escape_controls(bytes.iter().map(|byte| char::from(*byte))),
            Self::Utf8 => encoding_rs::UTF_8,
            Self::Utf16LittleEndian => encoding_rs::UTF_16LE,
            Self::Utf16BigEndian => encoding_rs::UTF_16BE,
            Self::Windows1252 => encoding_rs::WINDOWS_1252,
        }
        .decode_without_bom_handling(bytes);
        let decoded = if offset == 0 {
            decoded.trim_start_matches('\u{feff}')
        } else {
            decoded.as_ref()
        };
        escape_controls(decoded.chars())
    }
}

fn escape_controls(characters: impl Iterator<Item = char>) -> String {
    let mut text = String::new();
    for character in characters {
        if character == '\n' || character == '\t' {
            text.push(character);
        } else if character.is_control()
            || matches!(character, '\u{200b}'..='\u{200f}' | '\u{202a}'..='\u{202e}' | '\u{2060}' | '\u{2066}'..='\u{2069}' | '\u{feff}')
        {
            text.push_str(&format!("\\u{{{:04x}}}", character as u32));
        } else {
            text.push(character);
        }
    }
    text
}

fn hex_text(bytes: &[u8], offset: u64) -> Result<String> {
    let mut text = String::with_capacity(bytes.len().saturating_mul(5));
    for (row, chunk) in bytes.chunks(16).enumerate() {
        let absolute_offset = offset
            .checked_add(row as u64 * 16)
            .context("Byte offset overflows")?;
        write!(&mut text, "{absolute_offset:016X}  ")?;
        for column in 0..16 {
            if let Some(byte) = chunk.get(column) {
                write!(&mut text, "{byte:02X} ")?;
            } else {
                text.push_str("   ");
            }
            if column == 7 {
                text.push(' ');
            }
        }
        text.push_str(" | ");
        for byte in chunk {
            text.push(if (0x20..=0x7e).contains(byte) {
                char::from(*byte)
            } else {
                '.'
            });
        }
        text.push('\n');
    }
    Ok(text)
}

fn parse_offset(text: &str) -> Result<u64> {
    let text = text.trim();
    if let Some(hexadecimal) = text.strip_prefix("0x").or_else(|| text.strip_prefix("0X")) {
        Ok(u64::from_str_radix(hexadecimal, 16)
            .context("Enter a byte offset in decimal or 0x hexadecimal")?)
    } else {
        Ok(text
            .parse()
            .context("Enter a byte offset in decimal or 0x hexadecimal")?)
    }
}

#[derive(Clone, Default)]
struct ViewState {
    offset: u64,
    mode: ViewMode,
    encoding: TextEncoding,
    separate_view: bool,
}

pub struct BinaryDocument {
    path: ProjectPath,
    entry_id: Option<ProjectEntryId>,
    entry: Option<worktree::Entry>,
    deleted: bool,
}

impl EventEmitter<()> for BinaryDocument {}

impl BinaryDocument {
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

impl project::ProjectItem for BinaryDocument {
    fn try_open(
        project: &Entity<Project>,
        path: &ProjectPath,
        cx: &mut App,
    ) -> Option<Task<Result<Entity<Self>>>> {
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

pub enum BinaryViewEvent {
    TitleChanged,
    Navigated,
}

pub struct BinaryView {
    document: Entity<BinaryDocument>,
    project: Entity<Project>,
    focus_handle: FocusHandle,
    state: ViewState,
    bytes: Vec<u8>,
    file_size: Option<u64>,
    editor: Option<Entity<Editor>>,
    offset_editor: Option<Entity<Editor>>,
    input_error: Option<SharedString>,
    error: Option<SharedString>,
    loading: bool,
    generation: u64,
    load_task: Option<Task<()>>,
}

#[cfg(any(test, feature = "test-support"))]
pub struct BinaryViewTestState {
    pub offset: u64,
    pub file_size: Option<u64>,
    pub byte_count: usize,
    pub loading: bool,
    pub error: Option<String>,
    pub text: Option<String>,
}

impl BinaryView {
    #[cfg(any(test, feature = "test-support"))]
    pub fn test_state(&self, cx: &App) -> BinaryViewTestState {
        BinaryViewTestState {
            offset: self.state.offset,
            file_size: self.file_size,
            byte_count: self.bytes.len(),
            loading: self.loading,
            error: self.error.as_ref().map(ToString::to_string),
            text: self.editor.as_ref().map(|editor| editor.read(cx).text(cx)),
        }
    }
    fn new(
        document: Entity<BinaryDocument>,
        project: Entity<Project>,
        state: ViewState,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        cx.subscribe_in(&document, window, |this, _, _, window, cx| {
            this.load(window, cx);
            cx.emit(BinaryViewEvent::TitleChanged);
        })
        .detach();
        cx.subscribe_in(&project, window, |this, _, event, window, cx| {
            if matches!(
                event,
                project::Event::DisconnectedFromRemote { .. }
                    | project::Event::DisconnectedFromHost
                    | project::Event::Closed
            ) {
                this.clear_content(
                    "Remote project is disconnected. Reconnect and reload the file",
                    window,
                    cx,
                );
            }
        })
        .detach();
        if let Some(remote) = project.read(cx).remote_client() {
            cx.subscribe_in(&remote, window, |this, _, event, window, cx| {
                if matches!(event, remote::RemoteClientEvent::Reconnected) {
                    this.clear_content(
                        "Remote project reconnected. Reload the file to read its current bytes",
                        window,
                        cx,
                    );
                }
            })
            .detach();
            cx.observe_in(&remote, window, |this, remote, window, cx| {
                if remote.read(cx).connection_state() != remote::ConnectionState::Connected {
                    this.clear_content("Remote project is disconnected or reconnecting. Reconnect and reload the file", window, cx);
                }
            }).detach();
        }
        let mut view = Self {
            document,
            project,
            focus_handle: cx.focus_handle(),
            state,
            bytes: Vec::new(),
            file_size: None,
            editor: None,
            offset_editor: None,
            input_error: None,
            error: None,
            loading: false,
            generation: 0,
            load_task: None,
        };
        view.load(window, cx);
        view
    }

    fn clear_content(
        &mut self,
        message: &'static str,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        if self
            .editor
            .as_ref()
            .is_some_and(|editor| editor.focus_handle(cx).contains_focused(window, cx))
        {
            self.focus_handle.focus(window, cx);
        }
        self.generation += 1;
        self.load_task = None;
        self.bytes.clear();
        self.editor = None;
        self.loading = false;
        self.error = Some(message.into());
        cx.notify();
    }

    fn load(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        if self
            .editor
            .as_ref()
            .is_some_and(|editor| editor.focus_handle(cx).contains_focused(window, cx))
        {
            self.focus_handle.focus(window, cx);
        }
        self.generation += 1;
        let generation = self.generation;
        self.load_task = None;
        self.editor = None;
        self.bytes.clear();
        self.error = None;
        self.loading = true;
        let path = self.document.read(cx).path.clone();
        let read = self.project.update(cx, |project, cx| {
            project.read_file_range(path, self.state.offset, PAGE_BYTES, cx)
        });
        let timeout = cx.background_executor().timer(LOAD_TIMEOUT);
        self.load_task = Some(cx.spawn_in(window, async move |this, cx| {
            let result = match future::select(Box::pin(read), Box::pin(timeout)).await {
                future::Either::Left((result, _)) => result,
                future::Either::Right(_) => Err(anyhow::anyhow!(
                    "File loading exceeded 120 seconds. Check the connection and retry"
                )),
            };
            this.update_in(cx, |this, window, cx| {
                if this.generation != generation {
                    return;
                }
                this.loading = false;
                match result {
                    Ok(range) => {
                        this.file_size = Some(range.file_size);
                        this.bytes = range.data;
                        this.update_text(window, cx);
                    }
                    Err(error) => this.error = Some(format!("{error:#}").into()),
                }
                cx.notify();
            })
            .log_err();
        }));
        cx.notify();
    }

    fn update_text(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        cx.emit(BinaryViewEvent::Navigated);
        cx.notify();
        if self.loading || self.error.is_some() {
            return;
        }
        let text = match self.state.mode {
            ViewMode::Hex => match hex_text(&self.bytes, self.state.offset) {
                Ok(text) => text,
                Err(error) => {
                    self.error = Some(error.to_string().into());
                    cx.notify();
                    return;
                }
            },
            ViewMode::Text => self.state.encoding.decode(&self.bytes, self.state.offset),
        };
        let focused = self.focus_handle.is_focused(window)
            || self
                .editor
                .as_ref()
                .is_some_and(|editor| editor.focus_handle(cx).contains_focused(window, cx));
        let buffer = cx.new(|cx| {
            let mut buffer = Buffer::local(text, cx);
            buffer.set_capability(Capability::ReadOnly, cx);
            buffer
        });
        let editor = cx.new(|cx| {
            let mut editor = Editor::for_buffer(buffer, None, window, cx);
            editor.set_read_only(true);
            editor.set_soft_wrap_mode(SoftWrap::None, cx);
            editor.set_show_gutter(false, cx);
            editor.set_show_indent_guides(false, cx);
            editor.set_show_wrap_guides(false, cx);
            editor
        });
        if focused {
            editor.focus_handle(cx).focus(window, cx);
        }
        self.editor = Some(editor);
        cx.emit(BinaryViewEvent::Navigated);
        cx.notify();
    }

    fn navigate(&mut self, offset: u64, window: &mut Window, cx: &mut Context<Self>) {
        self.state.offset = offset;
        self.offset_editor = None;
        self.input_error = None;
        self.load(window, cx);
        cx.emit(BinaryViewEvent::Navigated);
    }

    fn next_page(&mut self, _: &NextPage, window: &mut Window, cx: &mut Context<Self>) {
        if self.loading {
            return;
        }
        if let Some(next) = self.state.offset.checked_add(PAGE_BYTES)
            && self.file_size.is_some_and(|size| next < size)
        {
            self.navigate(next, window, cx);
        }
    }

    fn previous_page(&mut self, _: &PreviousPage, window: &mut Window, cx: &mut Context<Self>) {
        if !self.loading {
            self.navigate(self.state.offset.saturating_sub(PAGE_BYTES), window, cx);
        }
    }

    fn first_page(&mut self, _: &FirstPage, window: &mut Window, cx: &mut Context<Self>) {
        self.navigate(0, window, cx);
    }

    fn last_page(&mut self, _: &LastPage, window: &mut Window, cx: &mut Context<Self>) {
        if let Some(size) = self.file_size {
            self.navigate(size.saturating_sub(1) / PAGE_BYTES * PAGE_BYTES, window, cx);
        }
    }

    fn reload(&mut self, _: &Reload, window: &mut Window, cx: &mut Context<Self>) {
        self.load(window, cx);
    }

    fn go_to_offset(&mut self, _: &GoToOffset, window: &mut Window, cx: &mut Context<Self>) {
        let editor = cx.new(|cx| {
            let mut editor = Editor::single_line(window, cx);
            editor.set_text(format!("0x{:X}", self.state.offset), window, cx);
            editor.select_all(&editor::actions::SelectAll, window, cx);
            editor
        });
        editor.focus_handle(cx).focus(window, cx);
        self.offset_editor = Some(editor);
        self.input_error = None;
        cx.notify();
    }

    fn confirm_offset(&mut self, _: &menu::Confirm, window: &mut Window, cx: &mut Context<Self>) {
        let Some(editor) = self.offset_editor.as_ref() else {
            cx.propagate();
            return;
        };
        match parse_offset(&editor.read(cx).text(cx)) {
            Ok(offset)
                if self.file_size.is_none_or(|size| offset <= size)
                    && offset <= u64::MAX - PAGE_BYTES =>
            {
                self.focus_handle.focus(window, cx);
                self.navigate(offset, window, cx);
            }
            Ok(_) => self.input_error = Some("Enter an offset within the file".into()),
            Err(error) => self.input_error = Some(error.to_string().into()),
        }
        cx.notify();
    }

    fn cancel_offset(&mut self, _: &menu::Cancel, window: &mut Window, cx: &mut Context<Self>) {
        if self.offset_editor.take().is_none() {
            cx.propagate();
            return;
        }
        self.input_error = None;
        self.focus_handle.focus(window, cx);
        cx.notify();
    }

    fn absolute_path(&self, cx: &App) -> Option<PathBuf> {
        self.project
            .read(cx)
            .absolute_path(&self.document.read(cx).path, cx)
    }
}

impl EventEmitter<BinaryViewEvent> for BinaryView {}

impl Focusable for BinaryView {
    fn focus_handle(&self, cx: &App) -> FocusHandle {
        self.editor
            .as_ref()
            .map(|editor| editor.focus_handle(cx))
            .unwrap_or_else(|| self.focus_handle.clone())
    }
}

impl Render for BinaryView {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let this = cx.entity();
        let selected_encoding = self.state.encoding;
        let encoding_menu = ContextMenu::build(window, cx, move |mut menu, _, _| {
            for encoding in TextEncoding::ALL {
                let this = this.clone();
                menu = menu.toggleable_entry(
                    encoding.label(),
                    encoding == selected_encoding,
                    IconPosition::End,
                    None,
                    move |window, cx| {
                        this.update(cx, |this, cx| {
                            this.state.encoding = encoding;
                            this.update_text(window, cx);
                        });
                    },
                );
            }
            menu
        });
        let range_label = match self.file_size {
            Some(size) => format!(
                "0x{:X} · {} bytes shown / {size} bytes",
                self.state.offset,
                self.bytes.len()
            ),
            None => format!("Offset 0x{:X}", self.state.offset),
        };
        v_flex()
            .key_context("BinaryViewer")
            .track_focus(&self.focus_handle)
            .size_full()
            .bg(cx.theme().colors().editor_background)
            .on_action(cx.listener(Self::next_page))
            .on_action(cx.listener(Self::previous_page))
            .on_action(cx.listener(Self::first_page))
            .on_action(cx.listener(Self::last_page))
            .on_action(cx.listener(Self::reload))
            .on_action(cx.listener(Self::go_to_offset))
            .on_action(cx.listener(Self::confirm_offset))
            .on_action(cx.listener(Self::cancel_offset))
            .child(
                h_flex()
                    .flex_wrap()
                    .gap_2()
                    .p_2()
                    .border_b_1()
                    .border_color(cx.theme().colors().border)
                    .child(Label::new("Read-only").color(Color::Muted))
                    .child(Button::new("binary-first", "First").on_click(
                        cx.listener(|this, _, window, cx| this.first_page(&FirstPage, window, cx)),
                    ))
                    .child(
                        Button::new("binary-previous", "Previous")
                            .disabled(self.loading || self.state.offset == 0)
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.previous_page(&PreviousPage, window, cx)
                            })),
                    )
                    .child(
                        Button::new("binary-next", "Next")
                            .disabled(
                                self.loading
                                    || !self.file_size.is_some_and(|size| {
                                        self.state.offset.saturating_add(PAGE_BYTES) < size
                                    }),
                            )
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.next_page(&NextPage, window, cx)
                            })),
                    )
                    .child(
                        Button::new("binary-last", "Last")
                            .disabled(self.file_size.is_none())
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.last_page(&LastPage, window, cx)
                            })),
                    )
                    .child(
                        Button::new("binary-offset", "Go to Offset").on_click(cx.listener(
                            |this, _, window, cx| this.go_to_offset(&GoToOffset, window, cx),
                        )),
                    )
                    .child(
                        Button::new("binary-hex", "Hex + ASCII")
                            .toggle_state(self.state.mode == ViewMode::Hex)
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.state.mode = ViewMode::Hex;
                                this.update_text(window, cx);
                            })),
                    )
                    .child(
                        Button::new("binary-text", "Text")
                            .toggle_state(self.state.mode == ViewMode::Text)
                            .on_click(cx.listener(|this, _, window, cx| {
                                this.state.mode = ViewMode::Text;
                                this.update_text(window, cx);
                            })),
                    )
                    .child(
                        DropdownMenu::new(
                            "binary-encoding",
                            self.state.encoding.label(),
                            encoding_menu,
                        )
                        .disabled(self.state.mode != ViewMode::Text),
                    )
                    .child(Button::new("binary-reload", "Reload").on_click(
                        cx.listener(|this, _, window, cx| this.reload(&Reload, window, cx)),
                    )),
            )
            .child(
                h_flex()
                    .px_2()
                    .py_1()
                    .gap_2()
                    .child(Label::new(range_label).size(LabelSize::Small))
                    .when(self.state.mode == ViewMode::Text, |element| {
                        element.child(
                            Label::new("Text decodes this page; controls are escaped")
                                .size(LabelSize::Small)
                                .color(Color::Muted),
                        )
                    }),
            )
            .when_some(self.offset_editor.clone(), |element, editor| {
                element.child(
                    h_flex()
                        .key_context("BinaryOffsetInput")
                        .p_2()
                        .gap_2()
                        .child(Label::new("Byte offset:"))
                        .child(div().w(px(240.0)).child(editor)),
                )
            })
            .when_some(self.input_error.clone(), |element, error| {
                element.child(Label::new(error).color(Color::Error))
            })
            .child(div().flex_1().min_h_0().size_full().child(
                if let Some(error) = self.error.clone() {
                    v_flex()
                        .p_4()
                        .gap_2()
                        .child(Label::new("Unable to view file").color(Color::Error))
                        .child(div().max_w(px(640.0)).child(error))
                        .into_any_element()
                } else if self.loading {
                    div()
                        .p_4()
                        .child(Label::new("Loading file page…"))
                        .into_any_element()
                } else if let Some(editor) = self.editor.clone() {
                    div().size_full().child(editor).into_any_element()
                } else {
                    div().into_any_element()
                },
            ))
    }
}

impl Item for BinaryView {
    type Event = BinaryViewEvent;
    fn tab_content_text(&self, _: usize, cx: &App) -> SharedString {
        self.absolute_path(cx)
            .and_then(|path| {
                path.file_name()
                    .map(|name| name.to_string_lossy().into_owned())
            })
            .unwrap_or_else(|| "Binary".to_string())
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
        if matches!(event, BinaryViewEvent::TitleChanged) {
            callback(ItemEvent::UpdateTab);
        }
    }
    fn capability(&self, _: &App) -> language::Capability {
        language::Capability::ReadOnly
    }
    fn buffer_kind(&self, _: &App) -> ItemBufferKind {
        // A saved-byte inspection must not replace an editor with unsaved changes.
        if self.state.separate_view {
            ItemBufferKind::None
        } else {
            ItemBufferKind::Singleton
        }
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
                self.state.clone(),
                window,
                cx,
            )
        })))
    }
}

impl ProjectItem for BinaryView {
    type Item = BinaryDocument;
    fn for_project_item(
        project: Entity<Project>,
        _: Option<&Pane>,
        item: Entity<BinaryDocument>,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) -> Self {
        Self::new(item, project, ViewState::default(), window, cx)
    }
}

impl SerializableItem for BinaryView {
    fn serialized_item_kind() -> &'static str {
        "BinaryView"
    }

    fn deserialize(
        project: Entity<Project>,
        _: WeakEntity<Workspace>,
        workspace_id: WorkspaceId,
        item_id: ItemId,
        window: &mut Window,
        cx: &mut App,
    ) -> Task<Result<Entity<Self>>> {
        let database = persistence::BinaryViewerDb::global(cx);
        window.spawn(cx, async move |cx| {
            let (path, offset, mode, encoding, separate_view) = database
                .get_binary_state(item_id, workspace_id)?
                .context("Binary viewer state was not saved")?;
            let state = ViewState {
                offset: offset.parse().context("Saved byte offset is invalid")?,
                mode: if mode == "Text" {
                    ViewMode::Text
                } else {
                    ViewMode::Hex
                },
                encoding: TextEncoding::from_label(&encoding),
                separate_view: separate_view != 0,
            };
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
                let document = cx.new(|cx| BinaryDocument::new(&project, path, cx));
                cx.new(|cx| Self::new(document, project, state, window, cx))
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
        let offset = self.state.offset.to_string();
        let mode = if self.state.mode == ViewMode::Text {
            "Text"
        } else {
            "Hex"
        }
        .to_string();
        let encoding = self.state.encoding.label().to_string();
        let separate_view = i64::from(self.state.separate_view);
        let database = persistence::BinaryViewerDb::global(cx);
        Some(cx.background_spawn(async move {
            database
                .save_binary_state(
                    item_id,
                    workspace_id,
                    path,
                    offset,
                    mode,
                    encoding,
                    separate_view,
                )
                .await
        }))
    }

    fn cleanup(
        workspace_id: WorkspaceId,
        alive_items: Vec<ItemId>,
        _: &mut Window,
        cx: &mut App,
    ) -> Task<Result<()>> {
        let database = persistence::BinaryViewerDb::global(cx);
        delete_unloaded_items(alive_items, workspace_id, "binary_viewers", &database, cx)
    }

    fn should_serialize(&self, _: &BinaryViewEvent) -> bool {
        true
    }
}

pub fn init(cx: &mut App) {
    workspace::register_binary_project_item::<BinaryView>(cx);
    workspace::register_serializable_item::<BinaryView>(cx);
    cx.observe_new(|workspace: &mut Workspace, _, cx| {
        workspace.register_action(|workspace, _: &OpenInBinaryViewer, window, cx| {
            if workspace
                .active_item(cx)
                .is_some_and(|item| item.downcast::<BinaryView>().is_some())
            {
                return;
            }
            let Some(path) = workspace
                .active_item(cx)
                .and_then(|item| item.project_path(cx))
            else {
                return;
            };
            let project = workspace.project().clone();
            let document = cx.new(|cx| BinaryDocument::new(&project, path, cx));
            let view = cx.new(|cx| {
                BinaryView::new(
                    document,
                    project,
                    ViewState {
                        separate_view: true,
                        ..ViewState::default()
                    },
                    window,
                    cx,
                )
            });
            workspace.add_item_to_active_pane(Box::new(view), None, true, window, cx);
        });
        cx.notify();
    })
    .detach();
}

#[cfg(test)]
mod tests {
    use super::*;
    use fs::{FakeFs, Fs};
    use gpui::{TestAppContext, VisualTestContext};
    use settings::SettingsStore;
    use std::path::Path;

    async fn open_file(
        bytes: &[u8],
        cx: &mut TestAppContext,
    ) -> (
        Entity<Project>,
        Entity<BinaryDocument>,
        std::sync::Arc<FakeFs>,
    ) {
        cx.update(|cx| {
            let settings = SettingsStore::test(cx);
            cx.set_global(settings);
            cx.set_global(db::AppDatabase::test_new());
            theme_settings::init(theme::LoadThemes::JustBase, cx);
            editor::init(cx);
            init(cx);
        });
        let fs = FakeFs::new(cx.executor());
        fs.create_dir(Path::new("/root")).await.unwrap();
        fs.insert_file("/root/sample.bin", bytes.to_vec()).await;
        let project = Project::test(fs.clone(), [Path::new("/root")], cx).await;
        let document = cx.update(|cx| {
            let worktree = project.read(cx).worktrees(cx).next().unwrap();
            let path = ProjectPath {
                worktree_id: worktree.read(cx).id(),
                path: util::rel_path::rel_path("sample.bin").into(),
            };
            cx.new(|cx| BinaryDocument::new(&project, path, cx))
        });
        (project, document, fs)
    }

    fn draw(cx: &mut VisualTestContext) {
        cx.update(|window, cx| {
            window.refresh();
            window.draw(cx).clear(cx);
        });
        cx.run_until_parked();
    }

    #[test]
    fn formats_absolute_offsets_and_ascii_without_interpreting_controls() {
        let text = hex_text(&[0, 0x1b, b'A', 0x7e, 0xff], 0x1_0000_0000).unwrap();
        assert!(text.starts_with("0000000100000000  00 1B 41 7E FF"));
        assert!(text.ends_with(" | ..A~.\n"));
        assert_eq!(hex_text(&[], 0).unwrap(), "");
        assert_eq!(parse_offset(" 0x100000000 ").unwrap(), 4_294_967_296);
        assert_eq!(parse_offset("18446744073709551615").unwrap(), u64::MAX);
        for invalid in ["-1", "0x", "18446744073709551616", "garbage"] {
            assert!(parse_offset(invalid).is_err());
        }
    }

    #[test]
    fn decodes_explicit_encodings_and_escapes_terminal_and_bidi_controls() {
        assert_eq!(
            TextEncoding::Utf8.decode(b"\x1b[31m\0\r\nA\t", 0),
            "\\u{001b}[31m\\u{0000}\\u{000d}\nA\t"
        );
        assert_eq!(
            TextEncoding::Utf8.decode("a\u{202e}b".as_bytes(), 0),
            "a\\u{202e}b"
        );
        assert_eq!(
            TextEncoding::Utf16LittleEndian.decode(&[0xff, 0xfe, b'A', 0], 0),
            "A"
        );
        assert_eq!(TextEncoding::Utf16BigEndian.decode(&[0, b'A'], 2), "A");
        assert_eq!(TextEncoding::Windows1252.decode(&[0x80], 0), "€");
        assert_eq!(TextEncoding::Latin1.decode(&[0x80, 0xe9], 0), "\\u{0080}é");
        assert!(TextEncoding::Utf8.decode(&[0xff], 0).contains('\u{fffd}'));
    }

    #[gpui::test(iterations = 5)]
    async fn reads_pages_and_keeps_buffers_read_only(cx: &mut TestAppContext) {
        let bytes = (0..PAGE_BYTES * 2 + 19)
            .map(|index| (index % 251) as u8)
            .collect::<Vec<_>>();
        let (project, document, _) = open_file(&bytes, cx).await;
        let (view, cx) = cx.add_window_view(|window, cx| {
            BinaryView::new(
                document,
                project,
                ViewState {
                    separate_view: true,
                    ..ViewState::default()
                },
                window,
                cx,
            )
        });
        draw(cx);
        view.update_in(cx, |view, window, cx| {
            assert_eq!(view.bytes, bytes[..PAGE_BYTES as usize]);
            assert!(!view.can_save(cx) && !view.can_save_as(cx));
            assert_eq!(view.capability(cx), Capability::ReadOnly);
            let editor = view.editor.as_ref().unwrap();
            assert!(editor.read(cx).read_only(cx));
            editor.focus_handle(cx).focus(window, cx);
        });
        draw(cx);
        let before = view.read_with(cx, |view, cx| {
            view.editor.as_ref().unwrap().read(cx).text(cx)
        });
        cx.simulate_input("overwrite");
        assert!(view.read_with(
            cx,
            |view, cx| view.editor.as_ref().unwrap().read(cx).text(cx) == before
        ));
        view.update_in(cx, |view, window, cx| {
            view.next_page(&NextPage, window, cx);
        });
        draw(cx);
        assert_eq!(view.read_with(cx, |view, _| view.state.offset), PAGE_BYTES);
        view.update_in(cx, |view, window, cx| view.last_page(&LastPage, window, cx));
        draw(cx);
        view.read_with(cx, |view, _| {
            assert_eq!(view.bytes.len(), 19);
            assert_eq!(view.state.offset, PAGE_BYTES * 2);
            assert!(view.error.is_none());
        });
        view.update_in(cx, |view, window, cx| view.next_page(&NextPage, window, cx));
        assert_eq!(
            view.read_with(cx, |view, _| view.state.offset),
            PAGE_BYTES * 2
        );
    }

    #[gpui::test]
    async fn shows_empty_files_errors_and_independent_splits(cx: &mut TestAppContext) {
        let (project, document, fs) = open_file(&[], cx).await;
        let cx = cx.add_empty_window();
        let view = cx.update(|window, cx| {
            cx.new(|cx| BinaryView::new(document, project, ViewState::default(), window, cx))
        });
        cx.run_until_parked();
        view.read_with(cx, |view, _| {
            assert_eq!(view.file_size, Some(0));
            assert!(view.error.is_none());
        });
        fs.insert_file("/root/sample.bin", vec![b'A'; PAGE_BYTES as usize + 5])
            .await;
        view.update_in(cx, |view, window, cx| view.reload(&Reload, window, cx));
        cx.run_until_parked();
        let split = view
            .update_in(cx, |view, window, cx| view.clone_on_split(None, window, cx))
            .await
            .unwrap();
        cx.run_until_parked();
        split.update_in(cx, |view, window, cx| view.last_page(&LastPage, window, cx));
        cx.run_until_parked();
        assert_eq!(view.read_with(cx, |view, _| view.state.offset), 0);
        assert_eq!(split.read_with(cx, |view, _| view.state.offset), PAGE_BYTES);
        split.update_in(cx, |view, window, cx| {
            view.navigate(PAGE_BYTES * 3, window, cx)
        });
        cx.run_until_parked();
        split.read_with(cx, |view, _| {
            assert!(view.error.is_some());
            assert!(view.editor.is_none());
            assert!(view.bytes.is_empty());
        });
    }

    #[gpui::test(iterations = 5)]
    async fn offset_input_and_rapid_navigation_keep_the_latest_page(cx: &mut TestAppContext) {
        let bytes = (0..PAGE_BYTES * 3 + 7)
            .map(|index| (index % 251) as u8)
            .collect::<Vec<_>>();
        let (project, document, _) = open_file(&bytes, cx).await;
        let (view, cx) = cx.add_window_view(|window, cx| {
            BinaryView::new(document, project, ViewState::default(), window, cx)
        });
        draw(cx);
        view.update_in(cx, |view, window, cx| {
            view.go_to_offset(&GoToOffset, window, cx)
        });
        draw(cx);
        for invalid in ["-1", "0xQQ", "9999999999999999999999", "9999999"] {
            view.update_in(cx, |view, window, cx| {
                view.offset_editor
                    .as_ref()
                    .unwrap()
                    .update(cx, |editor, cx| editor.set_text(invalid, window, cx))
            });
            cx.dispatch_action(menu::Confirm);
            assert!(view.read_with(cx, |view, _| view.input_error.is_some()));
        }
        view.update_in(cx, |view, window, cx| {
            view.offset_editor
                .as_ref()
                .unwrap()
                .update(cx, |editor, cx| editor.set_text("0x10000", window, cx))
        });
        cx.dispatch_action(menu::Confirm);
        draw(cx);
        assert_eq!(view.read_with(cx, |view, _| view.state.offset), PAGE_BYTES);
        view.update_in(cx, |view, window, cx| {
            view.state.mode = ViewMode::Text;
            view.state.encoding = TextEncoding::Latin1;
            view.update_text(window, cx);
            assert_eq!(
                view.bytes,
                bytes[PAGE_BYTES as usize..PAGE_BYTES as usize * 2]
            );
            view.go_to_offset(&GoToOffset, window, cx);
        });
        draw(cx);
        cx.dispatch_action(menu::Cancel);
        assert!(view.read_with(cx, |view, _| view.offset_editor.is_none()));
        view.update_in(cx, |view, window, cx| {
            for offset in [0, PAGE_BYTES * 2, PAGE_BYTES, PAGE_BYTES * 3] {
                view.navigate(offset, window, cx);
            }
        });
        draw(cx);
        view.read_with(cx, |view, _| {
            assert_eq!(view.state.offset, PAGE_BYTES * 3);
            assert_eq!(view.bytes.len(), 7);
            assert!(view.error.is_none() && !view.loading);
        });
    }

    #[gpui::test]
    async fn persisted_offsets_and_manual_view_state_round_trip(cx: &mut TestAppContext) {
        let (project, _, _) = open_file(&vec![65; PAGE_BYTES as usize + 5], cx).await;
        let (multi_workspace, cx) = cx.add_window_view(|window, cx| {
            workspace::MultiWorkspace::test_new(project.clone(), window, cx)
        });
        let workspace = multi_workspace.read_with(cx, |workspace, _| workspace.workspace().clone());
        let (database, workspace_database) = cx.update(|_, cx| {
            (
                persistence::BinaryViewerDb::global(cx),
                workspace::WorkspaceDb::global(cx),
            )
        });
        let workspace_id = workspace_database.next_id().await.unwrap();
        database
            .save_binary_state(
                7,
                workspace_id,
                PathBuf::from("/root/sample.bin"),
                u64::MAX.to_string(),
                "Text".into(),
                "Windows-1252".into(),
                1,
            )
            .await
            .unwrap();
        let saved = database.get_binary_state(7, workspace_id).unwrap().unwrap();
        assert_eq!(saved.1, u64::MAX.to_string());
        assert_eq!(saved.4, 1);
        database
            .save_binary_state(
                7,
                workspace_id,
                saved.0,
                PAGE_BYTES.to_string(),
                saved.2,
                saved.3,
                saved.4,
            )
            .await
            .unwrap();
        let view = cx
            .update(|window, cx| {
                BinaryView::deserialize(project, workspace.downgrade(), workspace_id, 7, window, cx)
            })
            .await
            .unwrap();
        cx.run_until_parked();
        view.read_with(cx, |view, cx| {
            assert_eq!(view.state.offset, PAGE_BYTES);
            assert_eq!(view.bytes.len(), 5);
            assert!(view.state.separate_view && view.state.mode == ViewMode::Text);
            assert_eq!(view.editor.as_ref().unwrap().read(cx).text(cx), "AAAAA");
            assert_eq!(view.buffer_kind(cx), ItemBufferKind::None);
        });
    }

    #[gpui::test]
    async fn automatic_fallback_preserves_text_editor_and_manual_open_preserves_unsaved_edits(
        cx: &mut TestAppContext,
    ) {
        let (project, document, fs) = open_file(b"\xD4\xC3\xB2\xA1\x02\x00\x04\x00", cx).await;
        fs.insert_file("/root/plain.txt", b"saved text".to_vec())
            .await;
        let path = document.read_with(cx, |document, _| document.path.clone());
        let (multi_workspace, cx) = cx.add_window_view(|window, cx| {
            workspace::MultiWorkspace::test_new(project.clone(), window, cx)
        });
        let workspace =
            multi_workspace.read_with(cx, |multi_workspace, _| multi_workspace.workspace().clone());
        let binary = workspace
            .update_in(cx, |workspace, window, cx| {
                workspace.open_path(path.clone(), None, true, window, cx)
            })
            .await
            .unwrap();
        assert!(binary.downcast::<BinaryView>().is_some());
        let text_path = ProjectPath {
            path: util::rel_path::rel_path("plain.txt").into(),
            ..path
        };
        let text = workspace
            .update_in(cx, |workspace, window, cx| {
                workspace.open_path(text_path, None, true, window, cx)
            })
            .await
            .unwrap()
            .downcast::<Editor>()
            .unwrap();
        text.update_in(cx, |editor, window, cx| {
            editor.set_text("unsaved text", window, cx);
            editor.focus_handle(cx).focus(window, cx);
        });
        draw(cx);
        assert!(
            workspace.read_with(cx, |workspace, cx| workspace
                .active_item(cx)
                .is_some_and(|item| item.downcast::<Editor>().is_some())),
            "the text editor must be active before inspecting its saved bytes"
        );
        cx.update(|window, cx| {
            assert!(
                window.is_action_available(&OpenInBinaryViewer, cx),
                "the workspace must register the binary inspection command"
            )
        });
        cx.dispatch_action(OpenInBinaryViewer);
        draw(cx);
        assert_eq!(
            text.read_with(cx, |editor, cx| editor.text(cx)),
            "unsaved text"
        );
        let viewer = workspace.read_with(cx, |workspace, cx| {
            workspace
                .active_item(cx)
                .unwrap()
                .downcast::<BinaryView>()
                .unwrap()
        });
        assert!(viewer.read_with(cx, |view, _| view.bytes == b"saved text"));
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

    pub struct BinaryViewerDb(ThreadSafeConnection);
    impl Domain for BinaryViewerDb {
        const NAME: &str = stringify!(BinaryViewerDb);
        const MIGRATIONS: &[&str] = &[sql!(
            CREATE TABLE binary_viewers (
                workspace_id INTEGER,
                item_id INTEGER UNIQUE,
                binary_path BLOB,
                byte_offset TEXT NOT NULL,
                view_mode TEXT NOT NULL,
                encoding TEXT NOT NULL,
                separate_view INTEGER NOT NULL,
                PRIMARY KEY(workspace_id, item_id),
                FOREIGN KEY(workspace_id) REFERENCES workspaces(workspace_id) ON DELETE CASCADE
            ) STRICT;
        )];
    }
    db::static_connection!(BinaryViewerDb, [WorkspaceDb]);
    impl BinaryViewerDb {
        query! {
            pub async fn save_binary_state(item_id: ItemId, workspace_id: WorkspaceId, binary_path: PathBuf, byte_offset: String, view_mode: String, encoding: String, separate_view: i64) -> Result<()> {
                INSERT OR REPLACE INTO binary_viewers(item_id, workspace_id, binary_path, byte_offset, view_mode, encoding, separate_view) VALUES (?, ?, ?, ?, ?, ?, ?)
            }
        }
        query! {
            pub fn get_binary_state(item_id: ItemId, workspace_id: WorkspaceId) -> Result<Option<(PathBuf, String, String, String, i64)>> {
                SELECT binary_path, byte_offset, view_mode, encoding, separate_view FROM binary_viewers WHERE item_id = ? AND workspace_id = ?
            }
        }
    }
}
