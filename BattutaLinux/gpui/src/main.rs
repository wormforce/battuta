mod dashboard;
mod editor;
mod runtime;
mod statistics;
mod tray;
mod ui;

use gpui::{prelude::*, *};
use runtime::{Runtime, Snapshot};
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};
use std::{
    path::PathBuf,
    sync::Arc,
    time::{Duration, Instant},
};
use ui::Palette;

actions!(battuta, [FocusNext, FocusPrevious]);

#[derive(Clone, Copy, PartialEq, Eq)]
enum Page {
    Main,
    Statistics,
    Editor,
}

#[derive(Clone, Copy, Default, PartialEq, Eq, Deserialize, Serialize)]
enum Appearance {
    #[default]
    System,
    Light,
    Dark,
}

#[derive(Clone, Copy, Default, PartialEq, Eq, Deserialize, Serialize)]
enum Language {
    #[default]
    System,
    Chinese,
    English,
}

#[derive(Default, Deserialize, Serialize)]
struct Preferences {
    appearance: Appearance,
    language: Language,
}

fn preferences_path() -> Option<PathBuf> {
    std::env::var_os("XDG_CONFIG_HOME")
        .map(PathBuf::from)
        .filter(|path| path.is_absolute())
        .or_else(|| std::env::var_os("HOME").map(|home| PathBuf::from(home).join(".config")))
        .map(|path| path.join("battuta/gpui.json"))
}

struct Battuta {
    runtime: Runtime,
    root_focus: FocusHandle,
    snapshot: Snapshot,
    palette: Palette,
    preferences: Preferences,
    system_language_is_chinese: bool,
    page: Page,
    statistics_tab: statistics::Tab,
    menu: Option<&'static str>,
    slider_drag: Option<(&'static str, f64)>,
    selected_heat_key: Option<String>,
    split: Option<(String, f64, f64)>,
    active_field: Option<editor::MetadataField>,
    text: Entity<editor::TextField>,
    error: Option<String>,
    icon: Arc<Image>,
    last_refresh: Instant,
    poll_pending: bool,
    dirty_draft: bool,
    quit_prompt_open: bool,
    window: Option<AnyWindowHandle>,
    tray: Option<ksni::blocking::Handle<tray::BattutaTray>>,
    tray_started: bool,
}

impl Battuta {
    fn new(runtime: Runtime, cx: &mut Context<Self>) -> Self {
        let preferences = preferences_path()
            .and_then(|path| std::fs::read(path).ok())
            .and_then(|bytes| serde_json::from_slice(&bytes).ok())
            .unwrap_or_default();
        Self {
            runtime,
            root_focus: cx.focus_handle(),
            snapshot: Snapshot::default(),
            palette: Palette::dark(),
            preferences,
            system_language_is_chinese: ["LC_ALL", "LC_MESSAGES", "LANG"]
                .iter()
                .find_map(|name| std::env::var(name).ok().filter(|value| !value.is_empty()))
                .is_some_and(|locale| locale.starts_with("zh")),
            page: Page::Main,
            statistics_tab: statistics::Tab::Overview,
            menu: None,
            slider_drag: None,
            selected_heat_key: None,
            split: None,
            active_field: None,
            text: cx.new(editor::TextField::new),
            error: None,
            icon: Arc::new(Image::from_bytes(
                ImageFormat::Png,
                include_bytes!("../../../shared/brand/source/AppIconSquare.png").to_vec(),
            )),
            last_refresh: Instant::now(),
            poll_pending: false,
            dirty_draft: false,
            quit_prompt_open: false,
            window: None,
            tray: None,
            tray_started: false,
        }
    }

    fn say<'a>(&self, zh: &'a str, en: &'a str) -> &'a str {
        let chinese = match self.preferences.language {
            Language::Chinese => true,
            Language::English => false,
            Language::System => self.system_language_is_chinese,
        };
        if chinese { zh } else { en }
    }

    fn send(&mut self, request: Value) {
        self.error = None;
        if !self.runtime.send(request) {
            self.error = Some(
                self.say("Battuta 后端已退出", "Battuta runtime stopped")
                    .into(),
            );
        }
    }

    fn navigate(&mut self, page: Page, window: &mut Window, cx: &mut Context<Self>) {
        self.commit_field(cx);
        self.menu = None;
        self.slider_drag = None;
        let previous_page = self.page;
        self.page = page;
        match page {
            Page::Main => {}
            Page::Statistics => self.send(json!({"action":"statsRefresh"})),
            Page::Editor => self.send(json!({"action":"diyOpen"})),
        }
        let view = cx.entity();
        window.defer(cx, move |old_window, cx| {
            // GPUI 0.2.2's Wayland resize sends device pixels as surface geometry.
            // Opening each view with its own logical bounds also matches the desktop clients.
            if open_page(view.clone(), page, cx) {
                old_window.remove_window();
            } else {
                view.update(cx, |view, cx| {
                    view.page = previous_page;
                    view.error = Some(view.say("无法打开窗口", "Could not open the window").into());
                    cx.notify();
                });
            }
        });
    }

    fn prepare_to_close(&mut self, cx: &mut Context<Self>) {
        self.commit_field(cx);
        self.menu = None;
        self.slider_drag = None;
        self.window = None;
    }

    fn save_preferences(&mut self) {
        let result = preferences_path().map(|path| -> std::io::Result<()> {
            std::fs::create_dir_all(path.parent().unwrap())?;
            let pending = path.with_extension("tmp");
            std::fs::write(&pending, serde_json::to_vec(&self.preferences)?)?;
            std::fs::rename(pending, path)
        });
        if let Some(Err(error)) = result {
            self.error = Some(error.to_string());
        }
    }

    fn receive(&mut self, reply: Result<Snapshot, String>, cx: &mut Context<Self>) {
        let mut incoming = match reply {
            Ok(snapshot) => snapshot,
            Err(error) => {
                self.poll_pending = false;
                self.error = Some(error);
                cx.notify();
                return;
            }
        };
        if incoming.activate {
            let view = cx.entity();
            cx.defer(move |cx| {
                show_window(&view, None, cx);
            });
            return;
        }
        self.poll_pending = false;
        if let Some(error) = incoming.error.take().or(incoming.fatal.take()) {
            self.error = Some(error);
            cx.notify();
            return;
        }
        let mut changed = false;
        let profiles = merge_optional(
            incoming.profiles.take(),
            self.snapshot.profiles.take(),
            &mut changed,
        );
        let pointer_profiles = merge_optional(
            incoming.pointer_profiles.take(),
            self.snapshot.pointer_profiles.take(),
            &mut changed,
        );
        let stats = merge_optional(
            incoming.stats.take(),
            self.snapshot.stats.take(),
            &mut changed,
        );
        let report = merge_optional(
            incoming.report.take(),
            self.snapshot.report.take(),
            &mut changed,
        );
        let diy = merge_optional(incoming.diy.take(), self.snapshot.diy.take(), &mut changed);
        if let Some(diy) = &diy {
            self.dirty_draft = diy.is_dirty;
            if let Some(split) = &diy.split {
                if self
                    .split
                    .as_ref()
                    .is_none_or(|selected| selected.0 != split.id)
                {
                    self.split = Some((split.id.clone(), split.start, split.end));
                }
            } else {
                self.split = None;
            }
        }
        if !self.tray_started && incoming.settings.is_some() {
            self.start_tray(cx);
        }
        changed |= incoming != self.snapshot;
        self.snapshot = Snapshot {
            profiles,
            pointer_profiles,
            stats,
            report,
            diy,
            ..incoming
        };
        if changed && self.window.is_some() {
            cx.notify();
        }
    }

    fn start_tray(&mut self, cx: &mut Context<Self>) {
        self.tray_started = true;
        let (actions, receiver) = async_channel::unbounded();
        let chinese = self.say("zh", "en") == "zh";
        let launch = cx.background_spawn(async move { tray::BattutaTray::start(actions, chinese) });
        cx.spawn(async move |this, cx| {
            let Some(handle) = launch.await else { return };
            if this.update(cx, |view, _| view.tray = Some(handle)).is_err() {
                return;
            }
            while let Ok(action) = receiver.recv().await {
                let handled = cx.update(|cx| {
                    let Some(view) = this.upgrade() else {
                        return false;
                    };
                    match action {
                        tray::Action::Show(token) => {
                            show_window(&view, token, cx);
                        }
                        tray::Action::Quit => view.update(cx, |view, cx| view.exit(cx)),
                    }
                    true
                });
                if !matches!(handled, Ok(true)) {
                    break;
                }
            }
        })
        .detach();
    }

    fn poll_window(&mut self, window: &Window) {
        if !self.poll_pending {
            self.poll_pending = self.runtime.send(json!({"action":"poll"}));
        }
        let show_stats = self.page == Page::Statistics
            || (self.page == Page::Main
                && self
                    .snapshot
                    .settings
                    .as_ref()
                    .is_some_and(|s| s.is_typing_stats_enabled));
        if show_stats
            && window.is_window_active()
            && self.last_refresh.elapsed() >= Duration::from_secs(5)
        {
            self.last_refresh = Instant::now();
            self.runtime.send(json!({"action":"statsRefresh"}));
        }
    }

    fn exit(&mut self, cx: &mut Context<Self>) {
        if !self.dirty_draft && self.active_field.is_none() {
            cx.quit();
            return;
        }
        if self.quit_prompt_open {
            return;
        }
        let view = cx.entity();
        cx.defer(move |cx| {
            if let Some(handle) = show_window(&view, None, cx) {
                let _ = handle.update(cx, |_, window, cx| {
                    view.update(cx, |view, cx| view.confirm_exit(window, cx))
                });
            }
        });
    }

    fn confirm_exit(&mut self, window: &mut Window, cx: &mut Context<Self>) {
        self.quit_prompt_open = true;
        let choice = window.prompt(
            PromptLevel::Warning,
            self.say(
                "放弃未保存的音色包？",
                "Discard unsaved sound pack changes?",
            ),
            None,
            &[
                self.say("放弃并退出", "Discard and quit"),
                self.say("取消", "Cancel"),
            ],
            cx,
        );
        cx.spawn(async move |this, cx| {
            let discard = choice.await == Ok(0);
            let _ = this.update(cx, |view, cx| {
                view.quit_prompt_open = false;
                if discard {
                    cx.quit();
                }
            });
        })
        .detach();
    }
}

fn merge_optional<T: PartialEq>(
    incoming: Option<T>,
    previous: Option<T>,
    changed: &mut bool,
) -> Option<T> {
    if incoming.is_some() {
        *changed |= incoming != previous;
        incoming
    } else {
        previous
    }
}

impl Render for Battuta {
    fn render(&mut self, window: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        self.palette = match self.preferences.appearance {
            Appearance::Dark => Palette::dark(),
            Appearance::Light => Palette::light(),
            Appearance::System => match window.appearance() {
                WindowAppearance::Dark | WindowAppearance::VibrantDark => Palette::dark(),
                _ => Palette::light(),
            },
        };
        let p = self.palette;
        div()
            .id("battuta-root")
            .key_context("Battuta")
            .track_focus(&self.root_focus)
            .on_action(|_: &FocusNext, window, _| window.focus_next())
            .on_action(|_: &FocusPrevious, window, _| window.focus_prev())
            .size_full()
            .bg(rgb(p.canvas))
            .text_color(rgb(p.text))
            .text_size(px(13.))
            .flex()
            .flex_col()
            .child(match self.page {
                Page::Main => self.dashboard(cx).into_any_element(),
                Page::Statistics => self.statistics(cx).into_any_element(),
                Page::Editor => self.editor(cx).into_any_element(),
            })
            .when_some(self.error.clone(), |root, message| {
                root.child(
                    div()
                        .px_3()
                        .py_2()
                        .bg(rgb(p.recessed))
                        .text_color(rgb(p.warning))
                        .text_size(px(11.))
                        .child(message),
                )
            })
    }
}

fn main() {
    let compositor = gpui::guess_compositor();
    if compositor != "Wayland"
        && std::env::var_os("XDG_SESSION_TYPE").as_deref() == Some("wayland".as_ref())
    {
        eprintln!(
            "Battuta requires the native Wayland display in a Wayland session. Launch it from the graphical session with WAYLAND_DISPLAY set."
        );
        std::process::exit(1);
    }
    if compositor == "Headless" {
        eprintln!("Battuta requires a graphical Wayland or X11 session.");
        std::process::exit(1);
    }
    let (runtime, replies) = Runtime::start();
    runtime.send(json!({"action":"init"}));
    Application::new().run(move |cx| {
        cx.bind_keys([
            KeyBinding::new("tab", FocusNext, Some("Battuta")),
            KeyBinding::new("shift-tab", FocusPrevious, Some("Battuta")),
        ]);
        let view = cx.new(|cx| Battuta::new(runtime, cx));
        cx.spawn(async move |cx| {
            let mut started = false;
            while let Ok(reply) = replies.recv().await {
                let running = cx.update(|cx| {
                    if reply.as_ref().is_ok_and(|snapshot| snapshot.secondary) {
                        cx.quit();
                        return false;
                    }
                    if !started {
                        started = true;
                        if !open_page(view.clone(), Page::Main, cx) {
                            cx.quit();
                            return false;
                        }
                        cx.activate(true);
                    }
                    if !reply.as_ref().is_ok_and(|snapshot| snapshot.primary) {
                        view.update(cx, |view, cx| view.receive(reply, cx));
                    }
                    true
                });
                if !matches!(running, Ok(true)) {
                    break;
                }
            }
        })
        .detach();
    });
}

fn show_window(
    view: &Entity<Battuta>,
    token: Option<String>,
    cx: &mut App,
) -> Option<AnyWindowHandle> {
    let existing = view.read(cx).window;
    if existing.is_none() {
        open_page(view.clone(), view.read(cx).page, cx);
    }
    let handle = view.read(cx).window?;
    if token.is_some() || existing.is_some() {
        let _ = handle.update(cx, |_, window, _| {
            if let Some(token) = token {
                window.activate_window_with_token(token);
            } else {
                window.activate_window();
            }
        });
    }
    Some(handle)
}

fn open_page(view: Entity<Battuta>, page: Page, cx: &mut App) -> bool {
    let (width, height, title) = match page {
        Page::Main => (360., 760., "Battuta"),
        Page::Statistics => (
            900.,
            700.,
            view.read(cx)
                .say("Battuta · 输入统计", "Battuta · Typing Statistics"),
        ),
        Page::Editor => (
            1110.,
            700.,
            view.read(cx)
                .say("Battuta · DIY 音色编辑器", "Battuta · Sound Pack Editor"),
        ),
    };
    let bounds = Bounds::centered(None, size(px(width), px(height)), cx);
    let result = cx.open_window(
        WindowOptions {
            window_bounds: Some(WindowBounds::Windowed(bounds)),
            titlebar: Some(TitlebarOptions {
                title: Some(title.into()),
                ..Default::default()
            }),
            window_min_size: Some(size(px(340.), px(430.))),
            app_id: Some("io.github.wormforce.Battuta".into()),
            ..Default::default()
        },
        |window, cx| {
            window.set_window_title(title);
            view.read(cx).root_focus.focus(window);
            let weak = view.downgrade();
            window.on_window_should_close(cx, move |_, cx| {
                let _ = weak.update(cx, |view, cx| view.prepare_to_close(cx));
                true
            });
            let handle = window.window_handle();
            view.update(cx, |view, cx| {
                view.window = Some(handle);
                cx.spawn(async move |this, cx| {
                    loop {
                        cx.background_executor().timer(Duration::from_secs(1)).await;
                        if handle
                            .update(cx, |_, window, cx| {
                                this.update(cx, |view, _| view.poll_window(window))
                            })
                            .is_err()
                        {
                            break;
                        }
                    }
                })
                .detach();
            });
            view
        },
    );
    if let Err(error) = result {
        eprintln!("Battuta could not open its window: {error:#}");
        false
    } else {
        true
    }
}
