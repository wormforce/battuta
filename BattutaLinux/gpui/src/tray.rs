use async_channel::Sender;
use ksni::{Tray, blocking::TrayMethods, menu::StandardItem};

pub enum Action {
    Show(Option<String>),
    Quit,
}

pub struct BattutaTray {
    actions: Sender<Action>,
    chinese: bool,
}

impl BattutaTray {
    fn show(&self) {
        let token = ksni::current_xdg_activation_token().or_else(|| {
            // Plasma may send the token immediately after the activation call.
            std::thread::sleep(std::time::Duration::from_millis(1));
            ksni::current_xdg_activation_token()
        });
        let _ = self.actions.try_send(Action::Show(token));
    }

    pub fn start(actions: Sender<Action>, chinese: bool) -> Option<ksni::blocking::Handle<Self>> {
        Self { actions, chinese }.spawn().ok()
    }
}

impl Tray for BattutaTray {
    fn id(&self) -> String {
        "io.github.wormforce.Battuta".into()
    }

    fn title(&self) -> String {
        "Battuta".into()
    }

    fn icon_name(&self) -> String {
        self.id()
    }

    fn activate(&mut self, _: i32, _: i32) {
        self.show();
    }

    fn menu(&self) -> Vec<ksni::MenuItem<Self>> {
        let (show, quit) = if self.chinese {
            ("打开 Battuta", "退出")
        } else {
            ("Open Battuta", "Quit")
        };
        vec![
            StandardItem {
                label: show.into(),
                activate: Box::new(|tray: &mut Self| tray.show()),
                ..Default::default()
            }
            .into(),
            StandardItem {
                label: quit.into(),
                activate: Box::new(|tray: &mut Self| {
                    let _ = tray.actions.try_send(Action::Quit);
                }),
                ..Default::default()
            }
            .into(),
        ]
    }
}
