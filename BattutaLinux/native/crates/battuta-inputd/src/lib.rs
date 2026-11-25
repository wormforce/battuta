//! battuta-inputd: Battuta's system input broker.
//!
//! PR 5 scope: real evdev reading — capability-based device discovery, EV_KEY
//! filtering, inotify hotplug, device-removal resets, and the VT-switch
//! session gate — feeding the PR 4 handshake/subscriber machinery through a
//! shared event hub. PR 5b adds the real `SystemAuthorizer` (logind session
//! validation + polkit) and the lock-screen/sleep gate on a minimal hand-rolled
//! D-Bus client (`dbus`).

pub mod activation;
pub mod auth;
pub mod dbus;
pub mod evdev;
pub mod gate;
pub mod hub;
pub mod logind;
pub mod perms;
pub mod polkit;
pub mod seqpacket;
pub mod server;
pub mod session;
pub mod synthetic;
pub mod uinput;
