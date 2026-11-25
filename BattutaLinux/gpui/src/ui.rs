use gpui::{prelude::*, *};

#[derive(Clone, Copy)]
pub struct Palette {
    pub canvas: u32,
    pub surface: u32,
    pub recessed: u32,
    pub border: u32,
    pub text: u32,
    pub muted: u32,
    pub accent: u32,
    pub tint: u32,
    pub ink: u32,
    pub cyan: u32,
    pub violet: u32,
    pub warning: u32,
}

impl Palette {
    pub fn dark() -> Self {
        Self {
            canvas: 0x1a211a,
            surface: 0x252925,
            recessed: 0x171c17,
            border: 0x404940,
            text: 0xf3f6ef,
            muted: 0xaeb9ab,
            accent: 0xb8e84d,
            tint: 0x344321,
            ink: 0x242e0e,
            cyan: 0x40b8d1,
            violet: 0xb3a1f8,
            warning: 0xf2ab33,
        }
    }

    pub fn light() -> Self {
        Self {
            canvas: 0xf1f4ed,
            surface: 0xfafbf8,
            recessed: 0xe8eee2,
            border: 0xd2dacb,
            text: 0x20291f,
            muted: 0x586453,
            accent: 0x4a7a0a,
            tint: 0xe2edcb,
            ink: 0xffffff,
            cyan: 0x237b91,
            violet: 0x6c4eb3,
            warning: 0x9b5705,
        }
    }

    pub fn card(self) -> Div {
        div()
            .flex()
            .flex_col()
            .gap_2()
            .p_3()
            .rounded(px(12.))
            .bg(rgb(self.surface))
            .border_1()
            .border_color(rgb(self.border))
    }

    pub fn note(self, text: impl Into<SharedString>) -> Div {
        div()
            .text_size(px(11.))
            .text_color(rgb(self.muted))
            .child(text.into())
    }

    pub fn heading(self, icon: &str, text: &str, tint: u32) -> Div {
        div()
            .flex()
            .items_center()
            .gap_2()
            .child(
                div()
                    .size(px(28.))
                    .rounded_md()
                    .bg(rgb(self.recessed))
                    .text_color(rgb(tint))
                    .flex()
                    .items_center()
                    .justify_center()
                    .child(icon.to_owned()),
            )
            .child(
                div()
                    .font_weight(FontWeight::SEMIBOLD)
                    .child(text.to_owned()),
            )
    }

    pub fn button(
        self,
        id: impl Into<ElementId>,
        text: impl Into<SharedString>,
        primary: bool,
    ) -> Stateful<Div> {
        let text = text.into();
        div()
            .id(id)
            .cursor_pointer()
            .focusable()
            .tab_stop(true)
            .focus(move |style| style.border_color(rgb(self.accent)))
            .flex()
            .items_center()
            .justify_center()
            .min_h(px(27.))
            .px_2()
            .rounded_md()
            .border_1()
            .text_size(px(11.))
            .border_color(rgb(if primary { self.accent } else { self.border }))
            .bg(rgb(if primary { self.accent } else { self.recessed }))
            .text_color(rgb(if primary { self.ink } else { self.text }))
            .hover(move |style| style.border_color(rgb(self.accent)))
            .active(|style| style.opacity(0.8))
            .when(!text.is_empty(), |control| control.child(text))
    }

    pub fn metric(self, title: &str, value: String) -> Div {
        div()
            .flex_1()
            .min_w_0()
            .px_2()
            .py_2()
            .rounded_md()
            .bg(rgb(self.recessed))
            .child(
                div()
                    .font_family("monospace")
                    .text_size(px(15.))
                    .text_color(rgb(self.accent))
                    .child(value),
            )
            .child(self.note(title.to_owned()))
    }
}

pub fn row() -> Div {
    div().flex().items_center().justify_between().gap_2()
}

pub fn content(id: &'static str) -> Stateful<Div> {
    div().id(id).flex_1().min_h_0().overflow_y_scroll()
}

pub fn heading(p: Palette, title: &str, sub: &str) -> Div {
    div()
        .flex()
        .flex_col()
        .gap_1()
        .child(
            div()
                .text_size(px(21.))
                .font_weight(FontWeight::SEMIBOLD)
                .child(title.to_owned()),
        )
        .child(p.note(sub.to_owned()))
}
