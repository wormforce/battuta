use crate::{
    Appearance, Battuta, Language, Page,
    runtime::Profile,
    ui::{content, row},
};
use gpui::{prelude::*, *};
use serde_json::json;

impl Battuta {
    pub fn dashboard(&self, cx: &mut Context<Self>) -> Div {
        let p = self.palette;
        let status = if self.snapshot.settings.is_none() {
            self.say("正在启动…", "Starting…")
        } else if self.snapshot.connected {
            self.say("输入监控正在运行", "Input monitoring is running")
        } else {
            self.say("键盘与点击监听未运行", "Input monitoring is offline")
        };
        let mut cards = div().flex().flex_col().gap_3().p(px(14.));
        if !self.snapshot.connected && self.snapshot.settings.is_some() {
            let unavailable = self.snapshot.broker_phase.as_deref() == Some("Unavailable");
            cards = cards.child(
                p.card()
                    .child(p.heading("!", self.say("输入监控", "Input monitoring"), p.warning))
                    .child(p.note(if unavailable {
                        self.say(
                            "请安装并启用 Battuta 输入服务，然后重试。",
                            "Install and enable the Battuta input service, then retry.",
                        )
                    } else {
                        self.say(
                            "允许系统输入监控后，即可在其他应用中使用键盘与点击音效。",
                            "Authorize input monitoring to play sounds while using other apps.",
                        )
                    }))
                    .when_some(
                        self.snapshot
                            .authorization
                            .as_ref()
                            .filter(|message| !message.is_empty()),
                        |card, message| card.child(p.note(message.clone())),
                    )
                    .when_some(self.snapshot.broker_detail.as_ref(), |card, message| {
                        card.child(p.note(message.clone()))
                    })
                    .child(
                        div()
                            .flex()
                            .gap_2()
                            .child(self.action(
                                "authorize",
                                self.say("请求授权", "Authorize"),
                                "authorize",
                                true,
                                cx,
                            ))
                            .child(self.action(
                                "retry",
                                self.say("重试", "Retry"),
                                "retry",
                                false,
                                cx,
                            )),
                    ),
            );
        }
        if let Some(settings) = &self.snapshot.settings {
            if let Some(message) = self.snapshot.sound_pack_error.as_deref() {
                cards = cards.child(
                    p.card()
                        .child(p.heading(
                            "!",
                            self.say("音色加载失败", "Sound pack unavailable"),
                            p.warning,
                        ))
                        .child(p.note(message.to_owned())),
                );
            } else if !self.snapshot.audio_ready {
                cards = cards.child(p.card().child(p.heading(
                    "!",
                    self.say("正在连接 PipeWire", "Connecting to PipeWire"),
                    p.warning,
                )));
            }
            cards = cards
                .child(p.card()
                    .child(row().child(p.heading("⌁", self.say("输入统计", "Typing Statistics"), p.cyan))
                        .child(self.toggle("isTypingStatsEnabled", self.say("输入统计", "Typing statistics"), settings.is_typing_stats_enabled, cx)))
                    .child(p.note(self.say("仅在本机保存汇总数据，包括字符数、物理按键和前台应用。", "Stores aggregate counts, physical keys and foreground apps on this device.")))
                    .child(row()
                        .child(p.metric(self.say("今日字符", "Characters today"), self.snapshot.today.count.to_string()))
                        .child(p.metric(self.say("今日峰值", "Peak today"), format!("{}/s", self.snapshot.today.peak))))
                    .child(p.note(format!("{}  {}", self.say("今日最多应用", "Top app today"),
                        if self.snapshot.today.app.is_empty() || self.snapshot.today.app == "暂无" { "—" } else { &self.snapshot.today.app })))
                    .child(p.button("open-stats", self.say("查看详细统计  ↗", "View detailed statistics  ↗"), false)
                        .on_click(cx.listener(|view, _, window, cx| view.navigate(Page::Statistics, window, cx)))))
                .child(p.card()
                    .child(row().child(p.heading("⌨", self.say("键盘声音", "Keyboard Sounds"), p.accent))
                        .child(self.toggle("isEnabled", self.say("键盘声音", "Keyboard sounds"), settings.is_enabled, cx)))
                    .child(self.profile_picker("selectedProfileId", &settings.selected_profile_id,
                        self.snapshot.profiles.as_deref().unwrap_or_default(), cx))
                    .child(div().flex().gap_2()
                        .child(self.action("preview", self.say("▶  试听", "▶  Preview"), "preview", true, cx))
                        .child(p.button("open-diy", self.say("DIY 音色编辑器", "DIY Sound Pack Editor"), false)
                            .on_click(cx.listener(|view, _, window, cx| view.navigate(Page::Editor, window, cx)))))
                    .child(self.volume("volume", self.say("键盘音量", "Keyboard volume"), settings.volume, cx))
                    .child(self.check("playsReleaseSound", self.say("播放键盘回弹音", "Play key release sounds"), settings.plays_release_sound, cx))
                    .child(self.check("playsKeyRepeatSound", self.say("长按连续发声", "Play sounds on key repeat"), settings.plays_key_repeat_sound, cx))
                    .child(self.check("usesPitchVariation", self.say("自然音色变化", "Natural sound variation"), settings.uses_pitch_variation, cx)))
                .child(p.card()
                    .child(row().child(p.heading("◉", self.say("鼠标与触控板", "Mouse & Trackpad"), p.violet))
                        .child(self.toggle("isPointerSoundEnabled", self.say("点击音效", "Click sounds"), settings.is_pointer_sound_enabled, cx)))
                    .child(self.profile_picker("selectedPointerProfileId", &settings.selected_pointer_profile_id,
                        self.snapshot.pointer_profiles.as_deref().unwrap_or_default(), cx))
                    .child(div().flex().items_center().gap_2()
                        .child(self.action("preview-pointer", self.say("▶  试听", "▶  Preview"), "previewPointer", false, cx))
                        .child(self.check("playsPointerReleaseSound", self.say("播放抬起音", "Play release sounds"), settings.plays_pointer_release_sound, cx)))
                    .child(self.volume("pointerVolume", self.say("点击音量", "Click volume"), settings.pointer_volume, cx)))
                .child(self.preferences_card(cx))
                .child(p.card()
                    .child(row().child(p.heading("↱", self.say("登录时启动", "Launch at Login"), p.cyan))
                        .child(self.toggle("isLaunchAtLoginEnabled", self.say("登录时启动", "Launch at login"), settings.is_launch_at_login_enabled, cx))))
                .child(p.card()
                    .child(p.heading("↻", self.say("软件更新", "Software Update"), p.accent))
                    .child(self.check("automaticUpdateCheckPreference", self.say("打开面板时自动检查", "Check automatically when opened"),
                        settings.automatic_update_check_preference == 1, cx))
                    .child(p.note(self.snapshot.update_status.clone().unwrap_or_else(||
                        self.say("尚未检查更新", "Updates have not been checked").into())))
                    .child(div().flex().gap_2()
                        .child(self.action("check-update", self.say("检查更新", "Check for Updates"), "checkUpdate", false, cx))
                        .when_some(self.snapshot.release_url.clone(), |bar, url| bar.child(p.button("release",
                            self.say("发行详情 ↗", "Release Notes ↗"), false).on_click(move |_, _, cx| cx.open_url(&url))))));
        }
        div()
            .size_full()
            .flex()
            .flex_col()
            .child(
                row()
                    .px(px(14.))
                    .py_3()
                    .border_b_1()
                    .border_color(rgb(p.border))
                    .child(
                        div()
                            .flex()
                            .items_center()
                            .gap_2()
                            .child(img(self.icon.clone()).size(px(40.)).rounded_lg())
                            .child(
                                div()
                                    .child(
                                        div()
                                            .text_size(px(17.))
                                            .font_weight(FontWeight::SEMIBOLD)
                                            .child("Battuta"),
                                    )
                                    .child(p.note(status)),
                            ),
                    )
                    .child(
                        div()
                            .size(px(12.))
                            .rounded_full()
                            .bg(rgb(if self.snapshot.connected {
                                p.tint
                            } else {
                                p.recessed
                            }))
                            .flex()
                            .items_center()
                            .justify_center()
                            .child(div().size(px(6.)).rounded_full().bg(rgb(
                                if self.snapshot.connected {
                                    p.accent
                                } else {
                                    p.warning
                                },
                            ))),
                    ),
            )
            .child(content("settings-scroll").child(cards))
            .child(
                row()
                    .px(px(14.))
                    .py_2()
                    .border_t_1()
                    .border_color(rgb(p.border))
                    .child(p.note(format!("Battuta {}", self.snapshot.version)))
                    .child(
                        div()
                            .flex()
                            .gap_2()
                            .child(p.button("hide", self.say("隐藏", "Hide"), false).on_click(
                                cx.listener(|view, _, window, cx| {
                                    view.prepare_to_close(cx);
                                    window.remove_window();
                                }),
                            ))
                            .child(
                                p.button("quit", self.say("退出", "Quit"), false)
                                    .on_click(cx.listener(|view, _, _, cx| view.exit(cx))),
                            ),
                    ),
            )
    }

    pub fn action(
        &self,
        id: &'static str,
        label: &str,
        action: &'static str,
        primary: bool,
        cx: &mut Context<Self>,
    ) -> Stateful<Div> {
        self.palette
            .button(id, label.to_owned(), primary)
            .on_click(cx.listener(move |view, _, _, _| view.send(json!({"action":action}))))
    }

    fn toggle(
        &self,
        name: &'static str,
        label: &str,
        value: bool,
        cx: &mut Context<Self>,
    ) -> Stateful<Div> {
        let p = self.palette;
        div()
            .id(name)
            .w(px(34.))
            .h(px(19.))
            .rounded_full()
            .border_1()
            .cursor_pointer()
            .bg(rgb(if value { p.accent } else { p.recessed }))
            .border_color(rgb(if value { p.accent } else { p.border }))
            .flex()
            .items_center()
            .px(px(2.))
            .when(value, |control| control.justify_end())
            .child(
                div()
                    .size(px(13.))
                    .rounded_full()
                    .bg(rgb(if value { p.ink } else { p.muted })),
            )
            .tooltip({
                let label: SharedString = label.to_owned().into();
                move |_, cx| cx.new(|_| super::statistics::Tooltip(label.clone())).into()
            })
            .on_click(cx.listener(move |view, _, _, _| {
                view.send(json!({"action":"setting","name":name,"value":!value}))
            }))
    }

    fn check(
        &self,
        name: &'static str,
        label: &str,
        value: bool,
        cx: &mut Context<Self>,
    ) -> Stateful<Div> {
        let p = self.palette;
        div()
            .id(name)
            .flex()
            .items_center()
            .gap_2()
            .cursor_pointer()
            .text_size(px(11.))
            .child(
                div()
                    .size(px(14.))
                    .rounded_sm()
                    .border_1()
                    .border_color(rgb(if value { p.accent } else { p.border }))
                    .when(value, |box_| {
                        box_.bg(rgb(p.accent))
                            .text_color(rgb(p.ink))
                            .flex()
                            .items_center()
                            .justify_center()
                            .child("✓")
                    }),
            )
            .child(label.to_owned())
            .on_click(cx.listener(move |view, _, _, _| {
                view.send(json!({"action":"setting","name":name,"value":!value}))
            }))
    }

    fn profile_picker(
        &self,
        name: &'static str,
        active: &str,
        profiles: &[Profile],
        cx: &mut Context<Self>,
    ) -> Div {
        let p = self.palette;
        let label = profiles
            .iter()
            .find(|profile| profile.id == active)
            .map(|profile| profile.display_name.as_str())
            .unwrap_or(active);
        div()
            .flex()
            .flex_col()
            .gap_1()
            .child(
                p.button(name, "", false)
                    .justify_between()
                    .child(label.to_owned())
                    .child("⌄")
                    .on_click(cx.listener(move |view, _, _, cx| {
                        view.menu = if view.menu == Some(name) {
                            None
                        } else {
                            Some(name)
                        };
                        cx.notify();
                    })),
            )
            .when(self.menu == Some(name), |section| {
                section.child(
                    div()
                        .id((ElementId::from("profile-options"), name))
                        .max_h(px(185.))
                        .overflow_y_scroll()
                        .px_1()
                        .py_1()
                        .rounded_md()
                        .bg(rgb(p.recessed))
                        .children(profiles.iter().map(|profile| {
                            let id = profile.id.clone();
                            p.button(
                                (ElementId::from("profile"), SharedString::from(id.clone())),
                                profile.display_name.clone(),
                                id == active,
                            )
                            .w_full()
                            .justify_start()
                            .my(px(1.))
                            .on_click(cx.listener(
                                move |view, _, _, cx| {
                                    view.menu = None;
                                    view.send(json!({"action":"setting","name":name,"value":id}));
                                    cx.notify();
                                },
                            ))
                        })),
                )
            })
    }

    fn volume(&self, key: &'static str, label: &str, value: f64, cx: &mut Context<Self>) -> Div {
        let p = self.palette;
        div()
            .child(
                row()
                    .child(p.note(label.to_owned()))
                    .child(p.note(format!("{:.0}%", value * 100.))),
            )
            .child(self.scale(
                key,
                value,
                move |view, value, _| {
                    view.send(json!({
                        "action":"setting", "name":key, "value":value
                    }))
                },
                cx,
            ))
    }

    pub(crate) fn scale(
        &self,
        id: &'static str,
        value: f64,
        update: impl Fn(&mut Self, f64, &mut Context<Self>) + Copy + 'static,
        cx: &mut Context<Self>,
    ) -> Stateful<Div> {
        let p = self.palette;
        let display = self
            .slider_drag
            .filter(|(active, _)| *active == id)
            .map_or(value, |(_, fraction)| fraction);
        let entity = cx.entity().downgrade();
        div()
            .id(id)
            .relative()
            .h(px(20.))
            .flex()
            .items_center()
            .cursor_pointer()
            .focusable()
            .tab_stop(true)
            .focus(move |style| style.bg(rgb(p.tint)).rounded_md())
            .on_key_down(cx.listener(move |view, event: &KeyDownEvent, _, cx| {
                let fraction = match event.keystroke.key.as_str() {
                    "left" | "down" => value - 0.01,
                    "right" | "up" => value + 0.01,
                    "home" => 0.,
                    "end" => 1.,
                    _ => return,
                };
                update(view, fraction.clamp(0., 1.), cx);
                cx.stop_propagation();
            }))
            .child(
                div()
                    .absolute()
                    .w_full()
                    .h(px(4.))
                    .rounded_full()
                    .bg(rgb(p.border)),
            )
            .child(
                div()
                    .absolute()
                    .w(relative(display as f32))
                    .h(px(4.))
                    .rounded_full()
                    .bg(rgb(p.accent)),
            )
            .child(
                div()
                    .absolute()
                    .left(relative(display as f32))
                    .ml(px(-5.))
                    .size(px(10.))
                    .rounded_full()
                    .bg(rgb(p.accent)),
            )
            .child(
                canvas(
                    |bounds, window, _| window.insert_hitbox(bounds, HitboxBehavior::Normal),
                    move |bounds, hitbox, window, _| {
                        let fraction_at = move |position: Pixels| -> f64 {
                            (f64::from(position - bounds.origin.x) / f64::from(bounds.size.width))
                                .clamp(0., 1.)
                        };
                        let mouse_down = entity.clone();
                        let hitbox = hitbox.id;
                        window.on_mouse_event(move |event: &MouseDownEvent, phase, window, cx| {
                            if phase == DispatchPhase::Bubble
                                && event.button == MouseButton::Left
                                && hitbox.is_hovered(window)
                            {
                                let _ = mouse_down.update(cx, |view, cx| {
                                    view.slider_drag = Some((id, fraction_at(event.position.x)));
                                    cx.notify();
                                });
                            }
                        });
                        let mouse_move = entity.clone();
                        window.on_mouse_event(move |event: &MouseMoveEvent, phase, _, cx| {
                            if phase == DispatchPhase::Bubble
                                && event.pressed_button == Some(MouseButton::Left)
                            {
                                let _ = mouse_move.update(cx, |view, cx| {
                                    if let Some((active, fraction)) = &mut view.slider_drag
                                        && *active == id
                                    {
                                        *fraction = fraction_at(event.position.x);
                                        cx.notify();
                                    }
                                });
                            }
                        });
                        window.on_mouse_event(move |event: &MouseUpEvent, phase, _, cx| {
                            if phase == DispatchPhase::Bubble && event.button == MouseButton::Left {
                                let _ = entity.update(cx, |view, cx| {
                                    if view.slider_drag.is_some_and(|(active, _)| active == id) {
                                        view.slider_drag = None;
                                        update(view, fraction_at(event.position.x), cx);
                                        cx.notify();
                                    }
                                });
                            }
                        });
                    },
                )
                .absolute()
                .size_full(),
            )
    }

    fn preferences_card(&self, cx: &mut Context<Self>) -> Div {
        let p = self.palette;
        p.card()
            .child(p.heading("◐", self.say("界面偏好", "Appearance & Language"), p.violet))
            .child(
                row().child(p.note(self.say("外观", "Appearance"))).child(
                    div().flex().gap_1().children(
                        [
                            (self.say("系统", "System"), Appearance::System),
                            (self.say("浅色", "Light"), Appearance::Light),
                            (self.say("深色", "Dark"), Appearance::Dark),
                        ]
                        .into_iter()
                        .enumerate()
                        .map(|(index, (label, choice))| {
                            p.button(
                                ("appearance", index),
                                label.to_owned(),
                                choice == self.preferences.appearance,
                            )
                            .on_click(cx.listener(
                                move |view, _, _, cx| {
                                    view.preferences.appearance = choice;
                                    view.save_preferences();
                                    cx.notify();
                                },
                            ))
                        }),
                    ),
                ),
            )
            .child(
                row().child(p.note(self.say("语言", "Language"))).child(
                    div().flex().gap_1().children(
                        [
                            (self.say("系统", "System"), Language::System),
                            ("简体中文", Language::Chinese),
                            ("English", Language::English),
                        ]
                        .into_iter()
                        .enumerate()
                        .map(|(index, (label, choice))| {
                            p.button(
                                ("language", index),
                                label.to_owned(),
                                choice == self.preferences.language,
                            )
                            .on_click(cx.listener(
                                move |view, _, _, cx| {
                                    view.preferences.language = choice;
                                    view.save_preferences();
                                    cx.notify();
                                },
                            ))
                        }),
                    ),
                ),
            )
    }
}
