use crate::{
    Battuta,
    runtime::{Diy, Split},
    ui::{content, heading, row},
};
use gpui::{prelude::*, *};
use serde_json::{Value, json};
use std::{ops::Range, path::PathBuf};

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum MetadataField {
    Name,
    Author,
    Notes,
}

impl MetadataField {
    fn command(self) -> &'static str {
        match self {
            Self::Name => "diyName",
            Self::Author => "diyAuthor",
            Self::Notes => "diyNotes",
        }
    }
}

impl Battuta {
    pub fn editor(&self, cx: &mut Context<Self>) -> Div {
        let p = self.palette;
        let header = row()
            .p_4()
            .border_b_1()
            .border_color(rgb(p.border))
            .child(heading(
                p,
                self.say("DIY 音色编辑器", "DIY Sound Pack Editor"),
                self.say(
                    "音色库 · 键盘 · 音频分配",
                    "Sound packs · Keyboard · Assignments",
                ),
            ))
            .child(self.home_button(cx));
        let Some(diy) = &self.snapshot.diy else {
            return div().size_full().child(header).child(
                div()
                    .p_4()
                    .child(p.note(self.say("正在读取音色库…", "Loading sound packs…"))),
            );
        };
        if let Some(split) = &diy.split {
            return div().size_full().flex().flex_col().child(header).child(
                content("split-scroll").child(div().p_4().child(self.split_editor(split, cx))),
            );
        }
        let sidebar = content("editor-library").child(
            p.card()
                .child(p.heading("▤", self.say("音色库", "Sound Packs"), p.accent))
                .child(self.guarded(
                    "new-pack",
                    self.say("新建空白音色包", "New Blank Pack"),
                    json!({"action":"diyNew"}),
                    cx,
                ))
                .child(self.guarded(
                    "clone-pack",
                    self.say("从当前音色创建", "Create from Current Sound"),
                    json!({"action":"diyClone"}),
                    cx,
                ))
                .child(
                    p.button(
                        "import-pack",
                        self.say("导入音色包…", "Import Sound Pack…"),
                        false,
                    )
                    .on_click(cx.listener(|view, _, window, cx| {
                        view.choose_file("diyImportPack", false, true, window, cx)
                    })),
                )
                .children(diy.packs.iter().map(|pack| {
                    let id = pack.id.clone();
                    p.button(
                        (ElementId::from("pack"), SharedString::from(id.clone())),
                        pack.name.clone(),
                        Some(id.as_str()) == diy.selected_pack_id.as_deref(),
                    )
                    .justify_start()
                    .on_click(cx.listener(move |view, _, window, cx| {
                        view.guard_diy(json!({"action":"diySelect","value":id}), window, cx)
                    }))
                }))
                .when(diy.can_export, |panel| {
                    panel.child(
                        p.button(
                            "export-pack",
                            self.say("导出音色包…", "Export Sound Pack…"),
                            false,
                        )
                        .on_click(cx.listener(|view, _, _, cx| view.export_pack(cx))),
                    )
                })
                .when(diy.can_delete, |panel| {
                    panel.child(
                        p.button(
                            "delete-pack",
                            self.say("删除音色包", "Delete Sound Pack"),
                            false,
                        )
                        .on_click(cx.listener(|view, _, window, cx| {
                            view.confirm_diy(
                                json!({"action":"diyDelete"}),
                                view.say("删除选中的音色包？", "Delete the selected sound pack?"),
                                window,
                                cx,
                            )
                        })),
                    )
                }),
        );
        let workspace = if diy.has_draft {
            p.card()
                .child(p.heading("⌨", self.say("键盘试听", "Keyboard Preview"), p.accent))
                .child(p.note(self.say(
                    "点击键帽来选择按键并试听。",
                    "Select a keycap to hear its sound.",
                )))
                .child(self.keyboard(&diy.keys, Some(&diy.selected_key), true, cx))
        } else {
            p.card().child(p.note(self.say(
                "选择、新建或导入音色包来开始编辑。",
                "Select, create or import a sound pack to begin.",
            )))
        };
        let inspector = content("editor-inspector")
            .when(diy.has_draft, |panel| panel.child(self.inspector(diy, cx)));
        div()
            .size_full()
            .flex()
            .flex_col()
            .child(header)
            .child(
                div()
                    .flex_1()
                    .min_h_0()
                    .flex()
                    .gap_3()
                    .p_3()
                    .child(
                        div()
                            .w(px(175.))
                            .flex_none()
                            .flex()
                            .flex_col()
                            .child(sidebar),
                    )
                    .child(
                        div()
                            .flex_1()
                            .min_w_0()
                            .flex()
                            .flex_col()
                            .child(content("editor-workspace").child(workspace)),
                    )
                    .child(
                        div()
                            .w(px(255.))
                            .flex_none()
                            .flex()
                            .flex_col()
                            .child(inspector),
                    ),
            )
            .child(
                row()
                    .px_3()
                    .py_2()
                    .border_t_1()
                    .border_color(rgb(p.border))
                    .child(
                        p.note(
                            diy.error
                                .clone()
                                .or_else(|| diy.status_message.clone())
                                .unwrap_or_else(|| {
                                    if diy.is_dirty {
                                        self.say("有未保存的修改", "Unsaved changes").into()
                                    } else {
                                        String::new()
                                    }
                                }),
                        ),
                    )
                    .when(diy.has_draft, |footer| {
                        footer.child(
                            div().flex().gap_2().children(
                                [
                                    (false, self.say("保存", "Save")),
                                    (true, self.say("保存并启用", "Save & Enable")),
                                ]
                                .into_iter()
                                .map(|(enable, label)| {
                                    p.button(("save", usize::from(enable)), label, enable)
                                        .on_click(cx.listener(move |view, _, _, cx| {
                                            view.commit_field(cx);
                                            view.send(json!({"action":"diySave","value":enable}));
                                            cx.notify();
                                        }))
                                }),
                            ),
                        )
                    }),
            )
    }

    fn inspector(&self, diy: &Diy, cx: &mut Context<Self>) -> Div {
        let p = self.palette;
        let mut panel =
            p.card()
                .child(p.heading("≡", self.say("音色设置", "Pack Settings"), p.violet));
        for (field, label, value) in [
            (MetadataField::Name, self.say("名称", "Name"), &diy.name),
            (
                MetadataField::Author,
                self.say("作者", "Author"),
                &diy.author,
            ),
            (MetadataField::Notes, self.say("备注", "Notes"), &diy.notes),
        ] {
            let value = value.clone().unwrap_or_default();
            panel = panel.child(div().flex().flex_col().gap_1().child(p.note(label)).child(
                if self.active_field == Some(field) {
                    div()
                        .flex()
                        .items_center()
                        .gap_1()
                        .child(div().flex_1().min_w_0().child(self.text.clone()))
                        .child(
                            p.button(field.command(), self.say("确定", "Apply"), true)
                                .on_click(cx.listener(|view, _, _, cx| {
                                    view.commit_field(cx);
                                    cx.notify();
                                })),
                        )
                        .into_any_element()
                } else {
                    p.button(
                        field.command(),
                        if value.is_empty() {
                            "—".into()
                        } else {
                            value.clone()
                        },
                        false,
                    )
                    .justify_start()
                    .overflow_hidden()
                    .on_click(cx.listener(move |view, _, window, cx| {
                        view.commit_field(cx);
                        view.active_field = Some(field);
                        view.text.update(cx, |input, _| {
                            input.set(value.clone());
                            input.focus.focus(window);
                        });
                        cx.notify();
                    }))
                    .into_any_element()
                },
            ));
        }
        panel = panel
            .child(p.note(self.say("映射方式", "Mapping Mode")))
            .child(
                div().flex().flex_wrap().gap_1().children(
                    [
                        ("Generic", self.say("通用", "Generic")),
                        ("Recommended", self.say("推荐位", "Recommended")),
                        ("PerKey", self.say("逐键", "Per key")),
                    ]
                    .into_iter()
                    .map(|(mode, label)| {
                        p.button(mode, label, diy.mode == mode)
                            .on_click(cx.listener(move |view, _, _, cx| {
                                view.commit_field(cx);
                                view.send(json!({"action":"diyMode","value":mode}));
                                cx.notify();
                            }))
                    }),
                ),
            );
        if diy.mode == "Recommended" {
            panel = panel.child(
                div().flex().flex_wrap().gap_1().children(
                    [
                        ("R0", "1–0"),
                        ("R1", "Q"),
                        ("R2", "A"),
                        ("R3", "Z"),
                        ("R4", "Fn"),
                        ("Space", self.say("空格", "Space")),
                        ("Enter", self.say("回车", "Enter")),
                        ("Backspace", "⌫"),
                    ]
                    .into_iter()
                    .map(|(slot, label)| {
                        p.button(slot, label, diy.slot.as_deref() == Some(slot))
                            .on_click(cx.listener(move |view, _, _, _| {
                                view.send(json!({"action":"diySlot","value":slot}))
                            }))
                    }),
                ),
            );
        }
        panel
            .child(
                div()
                    .font_weight(FontWeight::SEMIBOLD)
                    .child(diy.slot_name.clone()),
            )
            .child(self.assignment(diy, false, cx))
            .child(self.assignment(diy, true, cx))
            .child(
                p.button(
                    "analyze",
                    self.say("导入完整敲击并拆分…", "Import & Split Full Keystroke…"),
                    false,
                )
                .on_click(cx.listener(|view, _, window, cx| {
                    view.choose_file("diyAnalyze", false, false, window, cx)
                })),
            )
    }

    fn assignment(&self, diy: &Diy, release: bool, cx: &mut Context<Self>) -> Div {
        let p = self.palette;
        let (menu, current, heading) = if release {
            (
                "release-asset",
                &diy.release,
                self.say("回弹音", "Release Sound"),
            )
        } else {
            ("press-asset", &diy.press, self.say("按下音", "Press Sound"))
        };
        let current = match current.as_str() {
            "继承上一级" => self.say("继承上一级", "Inherit"),
            "静音" => self.say("静音", "Silent"),
            other => other,
        };
        div()
            .flex()
            .flex_col()
            .gap_1()
            .pt_2()
            .border_t_1()
            .border_color(rgb(p.border))
            .child(p.note(heading))
            .child(
                p.button(menu, current.to_owned(), false)
                    .justify_between()
                    .child("⌄")
                    .on_click(cx.listener(move |view, _, _, cx| {
                        view.menu = if view.menu == Some(menu) {
                            None
                        } else {
                            Some(menu)
                        };
                        cx.notify();
                    })),
            )
            .when(self.menu == Some(menu), |assignment| {
                assignment.child(
                    div()
                        .id((ElementId::from("asset-options"), menu))
                        .max_h(px(132.))
                        .overflow_y_scroll()
                        .bg(rgb(p.recessed))
                        .child(
                            p.button(
                                (ElementId::from("inherit"), menu),
                                self.say("继承", "Inherit"),
                                false,
                            )
                            .w_full()
                            .on_click(cx.listener(
                                move |view, _, _, cx| {
                                    view.menu = None;
                                    view.send(
                                        json!({"action":"diyAsset","value":null,"release":release}),
                                    );
                                    cx.notify();
                                },
                            )),
                        )
                        .when(diy.mode == "PerKey", |items| {
                            items.child(
                                p.button(
                                    (ElementId::from("silent"), menu),
                                    self.say("静音", "Silent"),
                                    false,
                                )
                                .w_full()
                                .on_click(cx.listener(
                                    move |view, _, _, cx| {
                                        view.menu = None;
                                        view.send(json!({"action":"diySilent","release":release}));
                                        cx.notify();
                                    },
                                )),
                            )
                        })
                        .children(diy.assets.iter().map(|asset| {
                            let id = asset.id.clone();
                            p.button(
                                (ElementId::from("asset"), SharedString::from(id.clone())),
                                asset.name.clone(),
                                false,
                            )
                            .w_full()
                            .on_click(cx.listener(
                                move |view, _, _, cx| {
                                    view.menu = None;
                                    view.send(
                                        json!({"action":"diyAsset","value":id,"release":release}),
                                    );
                                    cx.notify();
                                },
                            ))
                        })),
                )
            })
            .child(
                div()
                    .flex()
                    .gap_1()
                    .child(
                        p.button(
                            (ElementId::from("import"), menu),
                            self.say("导入音频…", "Import Audio…"),
                            false,
                        )
                        .on_click(cx.listener(
                            move |view, _, window, cx| {
                                view.choose_file("diyImportAudio", release, false, window, cx)
                            },
                        )),
                    )
                    .child(
                        p.button(
                            (ElementId::from("audition"), menu),
                            self.say("试听", "Preview"),
                            true,
                        )
                        .on_click(cx.listener(move |view, _, _, _| {
                            view.send(json!({"action":"diyPreview","release":release}))
                        })),
                    ),
            )
    }

    fn split_editor(&self, analysis: &Split, cx: &mut Context<Self>) -> Div {
        let p = self.palette;
        let Some((_, start, end)) = self.split.as_ref() else {
            return p.card();
        };
        let duration = analysis.duration_seconds;
        let (start, end) = (*start, *end);
        let waveform = div()
            .relative()
            .h(px(148.))
            .rounded_md()
            .bg(rgb(p.recessed))
            .child(div().absolute().size_full().flex().items_center().children(
                analysis.waveform.iter().map(|point| {
                    let height = ((point.maximum - point.minimum).abs() * 70.).clamp(1., 140.);
                    div().flex_1().min_w_0().h(px(height)).bg(rgb(p.cyan))
                }),
            ))
            .child(
                div()
                    .absolute()
                    .top_0()
                    .h_full()
                    .left(relative((start / duration) as f32))
                    .w(px(2.))
                    .bg(rgb(p.accent)),
            )
            .child(
                div()
                    .absolute()
                    .top_0()
                    .h_full()
                    .left(relative((end / duration) as f32))
                    .w(px(2.))
                    .bg(rgb(p.warning)),
            );
        p.card()
            .child(p.heading("⌁", self.say("拆分按下与回弹音", "Split Press & Release Sounds"), p.accent))
            .child(p.note(format!("{} {:.0}%", self.say("分析置信度", "Confidence"), analysis.confidence * 100.)))
            .child(waveform)
            .child(self.split_control(false, start, duration, cx))
            .child(self.split_control(true, end, duration, cx))
            .when(!analysis.warnings.is_empty(), |panel| panel.child(p.note(format!("{} {}", self.say("分析提示", "Analysis warnings"), analysis.warnings.join(", ")))))
            .child(row()
                .child(div().flex().gap_2().children([
                    (false, self.say("试听按下", "Preview Press")), (true, self.say("试听回弹", "Preview Release")),
                ].into_iter().map(|(release, label)| p.button(("split-preview", usize::from(release)), label, false)
                    .on_click(cx.listener(move |view, _, _, _| view.send(json!({
                        "action":"diyPreviewSplit","value":{"start":start,"end":end},"release":release
                    })))))))
                .child(div().flex().gap_2()
                    .child(self.action("cancel-split", self.say("取消", "Cancel"), "diyCancelSplit", false, cx))
                    .child(p.button("confirm-split", self.say("应用拆分", "Apply Split"), true)
                        .on_click(cx.listener(move |view, _, _, _| view.send(json!({
                            "action":"diyConfirmSplit","value":{"start":start,"end":end}
                        })))))))
    }

    fn split_control(
        &self,
        release_end: bool,
        current: f64,
        duration: f64,
        cx: &mut Context<Self>,
    ) -> Div {
        let p = self.palette;
        let (id, label) = if release_end {
            ("end", self.say("回弹结束", "Release End"))
        } else {
            ("split", self.say("拆分点", "Split Point"))
        };
        div()
            .child(
                row()
                    .child(p.note(format!("{label}  {:.0} ms", current * 1000.)))
                    .child(div().flex().gap_1().children(
                        [(-0.001, "−"), (0.001, "+")].into_iter().enumerate().map(
                            |(index, (step, label))| {
                                p.button((id, index), label, false).on_click(cx.listener(
                                    move |view, _, _, cx| {
                                        view.move_split(release_end, current + step, duration, cx)
                                    },
                                ))
                            },
                        ),
                    )),
            )
            .child(self.scale(
                id,
                (current / duration).clamp(0., 1.),
                move |view, fraction, cx| {
                    view.move_split(release_end, fraction * duration, duration, cx)
                },
                cx,
            ))
    }

    fn move_split(&mut self, release_end: bool, value: f64, duration: f64, cx: &mut Context<Self>) {
        if let Some((_, start, end)) = &mut self.split {
            if release_end {
                *end = value.clamp((*start + 0.013).min(duration), duration);
            } else {
                *start = value.clamp(0.012, (*end - 0.013).max(0.012));
            }
            cx.notify();
        }
    }

    pub fn commit_field(&mut self, cx: &mut Context<Self>) {
        if let Some(field) = self.active_field.take() {
            let draft = self.text.read(cx).text.clone();
            if draft != self.text.read(cx).original {
                self.dirty_draft = true;
                self.send(json!({"action":field.command(),"value":draft}));
            }
        }
    }

    fn guarded(
        &self,
        id: &'static str,
        label: &str,
        command: Value,
        cx: &mut Context<Self>,
    ) -> Stateful<Div> {
        self.palette.button(id, label.to_owned(), false).on_click(
            cx.listener(move |view, _, window, cx| view.guard_diy(command.clone(), window, cx)),
        )
    }

    fn guard_diy(&mut self, command: Value, window: &mut Window, cx: &mut Context<Self>) {
        if self.dirty_draft || self.active_field.is_some() {
            self.confirm_diy(
                command,
                self.say("放弃未保存的修改？", "Discard unsaved changes?"),
                window,
                cx,
            );
        } else {
            self.send(command);
        }
    }

    fn confirm_diy(
        &mut self,
        command: Value,
        message: &str,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let answer = window.prompt(
            PromptLevel::Warning,
            message,
            None,
            &[self.say("继续", "Continue"), self.say("取消", "Cancel")],
            cx,
        );
        cx.spawn(async move |this, cx| {
            if answer.await == Ok(0) {
                let _ = this.update(cx, |view, cx| {
                    view.active_field = None;
                    view.send(command);
                    cx.notify();
                });
            }
        })
        .detach();
    }

    fn choose_file(
        &mut self,
        action: &'static str,
        release: bool,
        guard: bool,
        window: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let options = PathPromptOptions {
            files: true,
            directories: false,
            multiple: false,
            prompt: Some(self.say("选择文件", "Choose a File").into()),
        };
        let ask = (guard && (self.dirty_draft || self.active_field.is_some())).then(|| {
            window.prompt(
                PromptLevel::Warning,
                self.say("放弃未保存的修改？", "Discard unsaved changes?"),
                None,
                &[self.say("继续", "Continue"), self.say("取消", "Cancel")],
                cx,
            )
        });
        cx.spawn(async move |this, cx| {
            if let Some(answer) = ask
                && answer.await != Ok(0)
            {
                return;
            }
            let Ok(picker) = cx.update(|app| app.prompt_for_paths(options)) else {
                return;
            };
            match picker.await {
                Ok(Ok(Some(paths))) => {
                    if let Some(path) = paths.first() {
                        let _ = this.update(cx, |view, cx| {
                            if guard {
                                view.active_field = None;
                            } else {
                                view.commit_field(cx);
                            }
                            view.send(json!({"action":action,"value":path,"release":release}));
                            cx.notify();
                        });
                    }
                }
                Ok(Err(error)) => {
                    let _ = this.update(cx, |view, cx| {
                        view.error = Some(error.to_string());
                        cx.notify();
                    });
                }
                _ => {}
            }
        })
        .detach();
    }

    fn export_pack(&self, cx: &mut Context<Self>) {
        let folder = std::env::var_os("HOME")
            .map(PathBuf::from)
            .unwrap_or_else(|| PathBuf::from("/tmp"));
        let picked = cx.prompt_for_new_path(&folder, Some("Battuta.simuboardpack"));
        cx.spawn(async move |this, cx| match picked.await {
            Ok(Ok(Some(path))) => {
                let _ = this.update(cx, |view, _| {
                    view.send(json!({"action":"diyExportPack","value":path}));
                });
            }
            Ok(Err(error)) => {
                let _ = this.update(cx, |view, cx| {
                    view.error = Some(error.to_string());
                    cx.notify();
                });
            }
            _ => {}
        })
        .detach();
    }
}

pub struct TextField {
    focus: FocusHandle,
    text: String,
    original: String,
    selection: Range<usize>,
    marked: Option<Range<usize>>,
}

impl TextField {
    pub fn new(cx: &mut Context<Self>) -> Self {
        Self {
            focus: cx.focus_handle(),
            text: String::new(),
            original: String::new(),
            selection: 0..0,
            marked: None,
        }
    }

    fn set(&mut self, text: String) {
        self.original = text.clone();
        self.selection = 0..text.len();
        self.text = text;
        self.marked = None;
    }

    fn byte(&self, offset: usize) -> usize {
        let mut units = 0;
        for (index, ch) in self.text.char_indices() {
            if units >= offset {
                return index;
            }
            units += ch.len_utf16();
        }
        self.text.len()
    }

    fn utf16(&self, range: &Range<usize>) -> Range<usize> {
        self.text[..range.start].encode_utf16().count()
            ..self.text[..range.end].encode_utf16().count()
    }

    fn replace(&mut self, range: Option<Range<usize>>, text: &str) -> Range<usize> {
        let range = range
            .map(|r| self.byte(r.start)..self.byte(r.end))
            .or_else(|| self.marked.take())
            .unwrap_or_else(|| self.selection.clone());
        let text = text.replace(['\r', '\n'], " ");
        let insertion = range.start..range.start + text.len();
        self.text.replace_range(range, &text);
        self.selection = insertion.end..insertion.end;
        self.marked = None;
        insertion
    }

    fn movement(&mut self, key: &str) -> bool {
        let previous = self.text[..self.selection.start]
            .char_indices()
            .last()
            .map_or(0, |(index, _)| index);
        let next = self.selection.end
            + self.text[self.selection.end..]
                .chars()
                .next()
                .map_or(0, char::len_utf8);
        match key {
            "left" => self.selection = previous..previous,
            "right" => self.selection = next..next,
            "home" => self.selection = 0..0,
            "end" => self.selection = self.text.len()..self.text.len(),
            "backspace" | "delete" => {
                if self.selection.is_empty() {
                    self.selection = if key == "backspace" {
                        previous..self.selection.start
                    } else {
                        self.selection.end..next
                    };
                }
                self.replace(None, "");
            }
            _ => return false,
        }
        true
    }
}

impl EntityInputHandler for TextField {
    fn text_for_range(
        &mut self,
        range: Range<usize>,
        actual: &mut Option<Range<usize>>,
        _: &mut Window,
        _: &mut Context<Self>,
    ) -> Option<String> {
        let bytes = self.byte(range.start)..self.byte(range.end);
        *actual = Some(self.utf16(&bytes));
        Some(self.text[bytes].into())
    }

    fn selected_text_range(
        &mut self,
        _: bool,
        _: &mut Window,
        _: &mut Context<Self>,
    ) -> Option<UTF16Selection> {
        Some(UTF16Selection {
            range: self.utf16(&self.selection),
            reversed: false,
        })
    }

    fn marked_text_range(&self, _: &mut Window, _: &mut Context<Self>) -> Option<Range<usize>> {
        self.marked.as_ref().map(|range| self.utf16(range))
    }

    fn unmark_text(&mut self, _: &mut Window, _: &mut Context<Self>) {
        self.marked = None;
    }

    fn replace_text_in_range(
        &mut self,
        range: Option<Range<usize>>,
        text: &str,
        _: &mut Window,
        cx: &mut Context<Self>,
    ) {
        self.replace(range, text);
        cx.notify();
    }

    fn replace_and_mark_text_in_range(
        &mut self,
        range: Option<Range<usize>>,
        text: &str,
        _: Option<Range<usize>>,
        _: &mut Window,
        cx: &mut Context<Self>,
    ) {
        let marked = self.replace(range, text);
        self.marked = (!marked.is_empty()).then_some(marked);
        cx.notify();
    }

    fn bounds_for_range(
        &mut self,
        _: Range<usize>,
        bounds: Bounds<Pixels>,
        _: &mut Window,
        _: &mut Context<Self>,
    ) -> Option<Bounds<Pixels>> {
        Some(bounds)
    }

    fn character_index_for_point(
        &mut self,
        _: Point<Pixels>,
        _: &mut Window,
        _: &mut Context<Self>,
    ) -> Option<usize> {
        None
    }
}

impl Render for TextField {
    fn render(&mut self, _: &mut Window, cx: &mut Context<Self>) -> impl IntoElement {
        let entity = cx.entity();
        let focus = self.focus.clone();
        div()
            .relative()
            .id("diy-text")
            .track_focus(&self.focus)
            .cursor(CursorStyle::IBeam)
            .px_1()
            .py_1()
            .border_1()
            .rounded_md()
            .border_color(rgb(0x91c92b))
            .overflow_hidden()
            .on_key_down(cx.listener(|field, event: &KeyDownEvent, _, cx| {
                let key = event.keystroke.key.as_str();
                if event.keystroke.modifiers.control {
                    match key {
                        "a" => field.selection = 0..field.text.len(),
                        "c" | "x" if !field.selection.is_empty() => {
                            cx.write_to_clipboard(ClipboardItem::new_string(
                                field.text[field.selection.clone()].to_owned(),
                            ));
                            if key == "x" {
                                field.replace(None, "");
                            }
                        }
                        "v" => {
                            if let Some(text) =
                                cx.read_from_clipboard().and_then(|item| item.text())
                            {
                                field.replace(None, &text);
                            }
                        }
                        _ => return,
                    }
                } else if !field.movement(key) {
                    return;
                }
                cx.stop_propagation();
                cx.notify();
            }))
            .child(
                div()
                    .flex()
                    .overflow_hidden()
                    .text_size(px(12.))
                    .child(self.text[..self.selection.start].to_owned())
                    .child(if self.selection.is_empty() {
                        div().w(px(1.)).bg(rgb(0x91c92b))
                    } else {
                        div()
                            .bg(rgb(0x4a7a0a))
                            .text_color(rgb(0xffffff))
                            .child(self.text[self.selection.clone()].to_owned())
                    })
                    .child(self.text[self.selection.end..].to_owned()),
            )
            .child(
                canvas(
                    |bounds, _, _| bounds,
                    move |bounds, _, window, cx| {
                        if focus.is_focused(window) {
                            window.handle_input(
                                &focus,
                                ElementInputHandler::new(bounds, entity),
                                cx,
                            );
                        }
                    },
                )
                .absolute()
                .size_full(),
            )
    }
}
