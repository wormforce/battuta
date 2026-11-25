//! Deterministic synthetic event source (the plan's PR 4 requirement): the
//! broker replays a scripted stream of keyboard/pointer/reset frames so
//! handshake, disconnect, reconnect, and reset semantics can be validated
//! without touching real evdev devices.

use std::time::Duration;

/// One scripted step.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ScriptStep {
    /// A KEY_* press/release/repeat with the raw evdev code and value.
    Key { code: u16, value: u16 },
    /// A BTN_* press/release with the raw evdev code and value.
    Button { code: u16, value: u16 },
    /// A reset frame with the documented reason.
    Reset {
        reason: battuta_input_protocol::ResetReason,
    },
}

/// Key codes used by the script (evdev `KEY_*` values).
pub mod key {
    pub const KEY_A: u16 = 30;
    pub const KEY_LEFTSHIFT: u16 = 42;
    pub const KEY_Z: u16 = 44;
}

/// Button codes used by the script (mirrors protocol `btn`).
pub mod button {
    pub const LEFT: u16 = 0x110;
}

/// The deterministic synthetic script. It deliberately exercises press,
/// repeat, release, pointer buttons, and a broker-side reset so clients must
/// handle every v1 message shape.
pub fn script() -> Vec<ScriptStep> {
    use battuta_input_protocol::{
        EVDEV_VALUE_PRESS, EVDEV_VALUE_RELEASE, EVDEV_VALUE_REPEAT, ResetReason,
    };
    vec![
        ScriptStep::Key {
            code: key::KEY_A,
            value: EVDEV_VALUE_PRESS,
        },
        ScriptStep::Key {
            code: key::KEY_A,
            value: EVDEV_VALUE_REPEAT,
        },
        ScriptStep::Key {
            code: key::KEY_A,
            value: EVDEV_VALUE_RELEASE,
        },
        ScriptStep::Key {
            code: key::KEY_LEFTSHIFT,
            value: EVDEV_VALUE_PRESS,
        },
        ScriptStep::Button {
            code: button::LEFT,
            value: EVDEV_VALUE_PRESS,
        },
        ScriptStep::Button {
            code: button::LEFT,
            value: EVDEV_VALUE_RELEASE,
        },
        ScriptStep::Key {
            code: key::KEY_LEFTSHIFT,
            value: EVDEV_VALUE_RELEASE,
        },
        ScriptStep::Key {
            code: key::KEY_Z,
            value: EVDEV_VALUE_PRESS,
        },
        ScriptStep::Reset {
            reason: ResetReason::BacklogDrop,
        },
        ScriptStep::Key {
            code: key::KEY_Z,
            value: EVDEV_VALUE_RELEASE,
        },
    ]
}

#[derive(Clone, Copy, Debug)]
pub struct SyntheticConfig {
    /// Pause between scripted steps.
    pub interval: Duration,
    /// Restart the script from the top when finished.
    pub loop_forever: bool,
}

impl Default for SyntheticConfig {
    fn default() -> Self {
        Self {
            interval: Duration::from_millis(100),
            loop_forever: true,
        }
    }
}
