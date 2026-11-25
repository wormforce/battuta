use crate::{
    Battuta, Page,
    runtime::{Keycap, Stats},
    ui::{content, heading, row},
};
use gpui::{prelude::*, *};
use serde_json::json;

#[derive(Clone, Copy, Default, PartialEq, Eq)]
pub enum Tab {
    #[default]
    Overview,
    Keyboard,
    History,
    Annual,
}

pub struct Tooltip(pub SharedString);

impl Render for Tooltip {
    fn render(&mut self, _: &mut Window, _: &mut Context<Self>) -> impl IntoElement {
        div()
            .rounded_md()
            .bg(rgb(0x202920))
            .text_color(rgb(0xf3f6ef))
            .px_2()
            .py_1()
            .text_size(px(11.))
            .shadow_md()
            .child(self.0.clone())
    }
}

pub fn tooltip(text: String) -> impl Fn(&mut Window, &mut App) -> AnyView + 'static {
    let text = SharedString::from(text);
    move |_, cx| cx.new(|_| Tooltip(text.clone())).into()
}

impl Battuta {
    pub fn statistics(&self, cx: &mut Context<Self>) -> Div {
        let p = self.palette;
        let header = row()
            .p_4()
            .border_b_1()
            .border_color(rgb(p.border))
            .child(heading(
                p,
                self.say("输入统计", "Typing Statistics"),
                self.say(
                    "仅在此设备保存的输入活动",
                    "Input activity stored on this device",
                ),
            ))
            .child(self.home_button(cx));
        let tabs = div()
            .px_4()
            .py_2()
            .flex()
            .gap_2()
            .border_b_1()
            .border_color(rgb(p.border))
            .children(
                [
                    (Tab::Overview, self.say("概览", "Overview")),
                    (Tab::Keyboard, self.say("键盘", "Keyboard")),
                    (Tab::History, self.say("历史", "History")),
                    (Tab::Annual, self.say("年度报告", "Annual Report")),
                ]
                .into_iter()
                .enumerate()
                .map(|(index, (tab, label))| {
                    p.button(("stat-tab", index), label, self.statistics_tab == tab)
                        .on_click(cx.listener(move |view, _, _, cx| {
                            view.statistics_tab = tab;
                            view.selected_heat_key = None;
                            if tab == Tab::Annual {
                                view.send(json!({"action":"statsReport"}));
                            }
                            cx.notify();
                        }))
                }),
            );
        let body = match &self.snapshot.stats {
            Some(stats) => match self.statistics_tab {
                Tab::Overview => self.stats_overview(stats, cx),
                Tab::Keyboard => self.stats_keyboard(stats, cx),
                Tab::History => self.stats_history(stats, cx),
                Tab::Annual => self.stats_report(),
            },
            None => p.note(self.say("正在读取统计…", "Loading statistics…")),
        };
        let footer = self.snapshot.stats.as_ref().map(|stats| {
            div()
                .px_4()
                .py_2()
                .border_t_1()
                .border_color(rgb(p.border))
                .child(
                    p.note(
                        stats
                            .status_message
                            .as_ref()
                            .unwrap_or(&stats.data_source_text)
                            .clone(),
                    ),
                )
        });
        div()
            .size_full()
            .flex()
            .flex_col()
            .child(header)
            .child(tabs)
            .child(
                content(match self.statistics_tab {
                    Tab::Overview => "statistics-overview",
                    Tab::Keyboard => "statistics-keyboard",
                    Tab::History => "statistics-history",
                    Tab::Annual => "statistics-annual",
                })
                .child(div().p_4().child(body)),
            )
            .children(footer)
    }

    pub(crate) fn home_button(&self, cx: &mut Context<Self>) -> Stateful<Div> {
        self.palette
            .button(
                "home",
                self.say("‹  返回 Battuta", "‹  Back to Battuta"),
                false,
            )
            .on_click(cx.listener(|view, _, window, cx| view.navigate(Page::Main, window, cx)))
    }

    fn stats_overview(&self, stats: &Stats, cx: &mut Context<Self>) -> Div {
        let p = self.palette;
        let counts = row()
            .child(p.metric(
                self.say("今日字符", "Characters today"),
                self.snapshot.today.count.to_string(),
            ))
            .child(p.metric(
                self.say("今日峰值", "Peak today"),
                format!("{}/s", self.snapshot.today.peak),
            ))
            .child(p.metric(
                self.say("活跃分钟", "Active minutes"),
                stats.today_active_minutes.to_string(),
            ))
            .child(p.metric(
                self.say("物理按键", "Physical presses"),
                stats.today_physical_presses.to_string(),
            ));
        let ranges = div().flex().gap_1().children(
            [
                ("SevenDays", self.say("7 天", "7 days")),
                ("TwentyFourHours", self.say("24 小时", "24 hours")),
                ("SixHours", self.say("6 小时", "6 hours")),
                ("OneHour", self.say("1 小时", "1 hour")),
            ]
            .into_iter()
            .map(|(id, label)| {
                p.button(id, label, stats.range == id).on_click(cx.listener(
                    move |view, _, _, _| view.send(json!({"action":"statsRange","value":id})),
                ))
            }),
        );
        let chart = div()
            .flex()
            .items_end()
            .gap(px(2.))
            .h(px(100.))
            .rounded_md()
            .bg(rgb(p.recessed))
            .p_2()
            .children(stats.bars.iter().enumerate().map(|(index, bar)| {
                div()
                    .id(("bar", index))
                    .relative()
                    .flex_1()
                    .h_full()
                    .min_w_0()
                    .tooltip(tooltip(format!("{}  ·  {}", bar.label, bar.count)))
                    .child(
                        div()
                            .absolute()
                            .bottom_0()
                            .w_full()
                            .rounded_sm()
                            .h(px(2. + 77. * bar.height_fraction))
                            .bg(rgb(if bar.count == 0 { p.border } else { p.accent })),
                    )
            }));
        let apps = div()
            .flex()
            .flex_col()
            .gap_2()
            .children(stats.apps.iter().map(|app| {
                div()
                    .child(
                        row()
                            .child(
                                div()
                                    .flex_1()
                                    .min_w_0()
                                    .truncate()
                                    .child(app.display_name.clone()),
                            )
                            .child(p.note(format!(
                                "{}  ·  {:.0}%",
                                app.character_count,
                                app.share * 100.
                            ))),
                    )
                    .child(
                        div().h(px(4.)).rounded_full().bg(rgb(p.recessed)).child(
                            div()
                                .h_full()
                                .rounded_full()
                                .w(relative(app.share.clamp(0., 1.)))
                                .bg(rgb(p.cyan)),
                        ),
                    )
            }));
        div()
            .flex()
            .flex_col()
            .gap_3()
            .child(
                p.card()
                    .child(p.heading("⌁", self.say("今日概览", "Today"), p.accent))
                    .child(counts),
            )
            .child(
                p.card()
                    .child(
                        row()
                            .child(p.heading(
                                "▥",
                                self.say("输入趋势", "Typing Activity"),
                                p.accent,
                            ))
                            .child(ranges),
                    )
                    .child(chart),
            )
            .child(
                p.card()
                    .child(p.heading("▣", self.say("应用分布", "Applications"), p.cyan))
                    .child(if stats.apps.is_empty() {
                        p.note(self.say("暂无应用记录", "No application activity yet"))
                    } else {
                        apps
                    }),
            )
    }

    fn stats_keyboard(&self, stats: &Stats, cx: &mut Context<Self>) -> Div {
        let p = self.palette;
        let scope = if stats.is_all_time_heatmap_scope {
            self.say("显示今日", "Show Today")
        } else {
            self.say("显示累计", "Show All Time")
        };
        p.card()
            .child(
                row()
                    .child(p.heading("⌨", self.say("逐键热力图", "Keyboard Heatmap"), p.accent))
                    .child(self.action("stats-scope", scope, "statsScope", false, cx)),
            )
            .child(p.note(if stats.is_all_time_heatmap_scope {
                self.say("累计物理按键次数", "All-time physical presses")
            } else {
                self.say("今日物理按键次数", "Physical presses today")
            }))
            .child(self.keyboard(&stats.keys, self.selected_heat_key.as_deref(), false, cx))
            .when_some(
                stats
                    .keys
                    .iter()
                    .find(|key| self.selected_heat_key.as_ref() == Some(&key.id)),
                |card, key| card.child(p.note(format!("{}  ·  {}", key.label, key.count))),
            )
    }

    fn stats_history(&self, stats: &Stats, cx: &mut Context<Self>) -> Div {
        let p = self.palette;
        p.card()
            .child(
                row()
                    .child(p.heading("▦", self.say("最近 14 天", "Last 14 Days"), p.accent))
                    .child(
                        p.button(
                            "clear-stats",
                            self.say("清除统计", "Clear Statistics"),
                            false,
                        )
                        .on_click(cx.listener(|view, _, window, cx| {
                            let answer = window.prompt(
                                PromptLevel::Warning,
                                view.say(
                                    "清除本机的全部输入统计？",
                                    "Clear all typing statistics on this device?",
                                ),
                                None,
                                &[view.say("清除", "Clear"), view.say("取消", "Cancel")],
                                cx,
                            );
                            cx.spawn(async move |this, cx| {
                                if answer.await == Ok(0) {
                                    let _ = this.update(cx, |view, _| {
                                        view.send(json!({"action":"statsClear"}))
                                    });
                                }
                            })
                            .detach();
                        })),
                    ),
            )
            .when(stats.history.is_empty(), |panel| {
                panel.child(p.note(self.say("暂无历史记录", "No history yet")))
            })
            .children(stats.history.iter().map(|day| {
                row()
                    .px_2()
                    .py_2()
                    .rounded_md()
                    .bg(rgb(p.recessed))
                    .child(div().min_w(px(95.)).child(day.date_text.clone()))
                    .child(p.note(format!("{}", day.character_count)))
                    .child(p.note(format!("{}/s", day.peak_cps)))
                    .child(
                        p.note(
                            day.top_application_name
                                .clone()
                                .unwrap_or_else(|| "—".into()),
                        ),
                    )
            }))
    }

    fn stats_report(&self) -> Div {
        let p = self.palette;
        let Some(report) = &self.snapshot.report else {
            return p.note(self.say("正在读取年度报告…", "Loading annual report…"));
        };
        let offset = report.days.first().map_or(0, |day| (day.weekday + 6) % 7);
        let maximum = report
            .days
            .iter()
            .map(|day| day.character_count)
            .max()
            .unwrap_or(0)
            .max(1);
        let calendar =
            div()
                .relative()
                .h(px(90.))
                .w(px(670.))
                .children(report.days.iter().enumerate().map(|(index, day)| {
                    let index = index + offset;
                    let intensity = (day.character_count as f32 / maximum as f32).powf(0.5);
                    div()
                        .id(("day", index))
                        .absolute()
                        .top(px((index % 7) as f32 * 12.))
                        .left(px((index / 7) as f32 * 12.))
                        .size(px(10.))
                        .rounded_sm()
                        .bg(heat_color(intensity, p.recessed))
                        .tooltip(tooltip(format!("{}  ·  {}", day.date, day.character_count)))
                }));
        div()
            .flex()
            .flex_col()
            .gap_3()
            .child(
                p.card()
                    .child(p.heading("▦", self.say("最近 365 天", "Last 365 Days"), p.accent))
                    .child(
                        row()
                            .child(p.metric(
                                self.say("字符数", "Characters"),
                                report.current.to_string(),
                            ))
                            .child(p.metric(
                                self.say("上一个周期", "Previous period"),
                                report.previous.to_string(),
                            ))
                            .child(p.metric(
                                self.say("活跃天数", "Active days"),
                                report.active_day_count.to_string(),
                            ))
                            .child(p.metric(
                                self.say("最长连续天数", "Longest streak"),
                                report.longest_active_day_streak.to_string(),
                            )),
                    )
                    .child(
                        div()
                            .id("calendar-scroll")
                            .overflow_x_scroll()
                            .child(calendar),
                    ),
            )
            .child(
                p.card()
                    .child(p.heading("▣", self.say("应用变化", "Application Activity"), p.cyan))
                    .when(report.apps.is_empty(), |card| {
                        card.child(p.note(self.say("暂无应用记录", "No application activity yet")))
                    })
                    .children(report.apps.iter().map(|app| {
                        row()
                            .px_2()
                            .py_1()
                            .rounded_md()
                            .bg(rgb(p.recessed))
                            .child(
                                div()
                                    .flex_1()
                                    .min_w_0()
                                    .truncate()
                                    .child(app.display_name.clone()),
                            )
                            .child(p.note(format!(
                                "{}  ·  {}",
                                app.character_count, app.comparison_character_count
                            )))
                    })),
            )
    }

    pub(crate) fn keyboard(
        &self,
        keys: &[Keycap],
        selected: Option<&str>,
        editing: bool,
        cx: &mut Context<Self>,
    ) -> Stateful<Div> {
        let p = self.palette;
        let unit = 34.;
        let mut board = div().relative().h(px(6. * unit)).w(px(15. * unit));
        let mut extras = div().flex().flex_wrap().gap_1().pt_2().w(px(15. * unit));
        for key in keys {
            let mut cap = if let Some(id) = &key.key {
                let physical_id = id.clone();
                let heat_id = key.id.clone();
                let selected = selected == Some(if editing { id } else { &key.id });
                let label = if !editing && key.count > 0 {
                    format!("{} {}", key.label, compact_count(key.count))
                } else {
                    key.label.clone()
                };
                let mut cap = p
                    .button(
                        (ElementId::from("key"), SharedString::from(key.id.clone())),
                        label,
                        selected,
                    )
                    .min_h_0()
                    .overflow_hidden()
                    .px(px(1.))
                    .text_size(px(10.));
                if !editing {
                    cap = cap.bg(if selected {
                        rgb(p.tint)
                    } else {
                        heat_color(key.intensity, p.surface)
                    });
                }
                let press_id = physical_id.clone();
                cap.tooltip(tooltip(if editing { key.label.clone() } else { format!("{} · {}", key.label, key.count) }))
                    .when(editing, |cap| cap.on_mouse_down(MouseButton::Left, cx.listener(move |view, _, _, _| {
                        view.send(json!({"action":"diyKey","value":press_id}));
                    })))
                    .on_click(cx.listener(move |view, event, _, cx| {
                        if editing {
                            view.send(json!({"action":"diyKey","value":physical_id, "release":matches!(event, ClickEvent::Mouse(_))}));
                        } else {
                            view.selected_heat_key = Some(heat_id.clone());
                            cx.notify();
                        }
                    }))
            } else {
                div().id((
                    ElementId::from("spacer"),
                    SharedString::from(key.id.clone()),
                ))
            };
            if key.extended {
                cap = cap.min_h(px(24.)).min_w(px(32.)).px_1();
                extras = extras.child(cap);
            } else {
                board = board.child(
                    cap.absolute()
                        .left(px(key.x_units * unit))
                        .top(px(key.y_units * unit))
                        .w(px(key.width_units * unit - 3.))
                        .h(px(key.height_units * unit - 3.)),
                );
            }
        }
        div()
            .id(if editing { "editor-keys" } else { "stats-keys" })
            .overflow_x_scroll()
            .p_2()
            .rounded_md()
            .bg(rgb(p.recessed))
            .child(board)
            .child(extras)
    }
}

fn heat_color(intensity: f32, surface: u32) -> Rgba {
    let base = rgb(surface);
    let hue = rgb(if intensity > 0.75 {
        0xf0645c
    } else if intensity > 0.45 {
        0xf2ab33
    } else {
        0x91c92b
    });
    let mix = intensity.clamp(0., 1.) * 0.83;
    Rgba {
        r: base.r + (hue.r - base.r) * mix,
        g: base.g + (hue.g - base.g) * mix,
        b: base.b + (hue.b - base.b) * mix,
        a: 1.,
    }
}

fn compact_count(count: u64) -> String {
    match count {
        0..=999 => count.to_string(),
        1_000..=999_999 => format!("{:.1}k", count as f64 / 1_000.),
        _ => format!("{:.1}m", count as f64 / 1_000_000.),
    }
}
