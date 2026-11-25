//! Real-machine end-to-end validation of the PR 5 evdev path: a virtual
//! uinput device is read by the broker through the normal discovery → EV_KEY
//! filter → hub → subscriber pipeline over a real SOCK_SEQPACKET socket.
//!
//! Requires read access to `/dev/input` and write access to `/dev/uinput`.
//! Skipped gracefully when unavailable (CI containers).
//!
//! Timing note: evdev delivers events only to clients that had the device
//! node open when the event fired. The broker's directory scan runs
//! concurrently with connection setup, so a single fire-and-forget emit can
//! race discovery and be lost forever. Every emit therefore retries until the
//! broker delivers the expected event, which is exactly how a live client
//! experiences input.

use battuta_input_protocol as proto;
use battuta_inputd::seqpacket::SeqPacketStream;
use battuta_inputd::uinput::UinputDevice;
use std::io::ErrorKind;
use std::process::{Child, Command};
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, Instant};

// evdev codes used by the virtual devices.
const KEY_A: u16 = 30;
const KEY_Y: u16 = 21;
const KEY_Z: u16 = 44;
const BTN_LEFT: u16 = 0x110;
const BTN_FORWARD: u16 = 0x115; // NOT forwarded by the broker

struct BrokerProcess(Child);

impl Drop for BrokerProcess {
    fn drop(&mut self) {
        let _ = self.0.kill();
        let _ = self.0.wait();
    }
}

fn unique_socket_path(tag: &str) -> std::path::PathBuf {
    static COUNTER: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);
    let unique = COUNTER.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
    std::env::temp_dir().join(format!(
        "battuta-evdev-e2e-{}-{tag}-{unique}/input.sock",
        std::process::id()
    ))
}

fn uinput_available() -> bool {
    std::fs::OpenOptions::new()
        .read(true)
        .write(true)
        .open("/dev/uinput")
        .is_ok()
}

fn spawn_real_broker(socket_path: &std::path::Path, extra: &[&str]) -> BrokerProcess {
    let child = Command::new(env!("CARGO_BIN_EXE_battuta-inputd"))
        .arg("--socket-path")
        .arg(socket_path)
        .arg("--dev-auth")
        .args(extra)
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::inherit())
        .spawn()
        .expect("spawn battuta-inputd");
    BrokerProcess(child)
}

fn connect_with_retry(socket_path: &std::path::Path) -> SeqPacketStream {
    let deadline = Instant::now() + Duration::from_secs(5);
    loop {
        match SeqPacketStream::connect(socket_path) {
            Ok(stream) => return stream,
            Err(error)
                if error.kind() == ErrorKind::NotFound
                    || error.kind() == ErrorKind::ConnectionRefused =>
            {
                assert!(
                    Instant::now() < deadline,
                    "broker socket never appeared at {}",
                    socket_path.display()
                );
                std::thread::sleep(Duration::from_millis(20));
            }
            Err(error) => panic!("connect failed: {error}"),
        }
    }
}

fn handshake(socket_path: &std::path::Path) -> SeqPacketStream {
    let stream = connect_with_retry(socket_path);
    let hello = proto::Message::Hello {
        major: proto::PROTOCOL_MAJOR,
        minor: proto::PROTOCOL_MINOR,
        caps: 0,
        client_name: "evdev-e2e".to_owned(),
    };
    stream
        .send(&proto::encode_frame(&hello).expect("encode hello"))
        .expect("send hello");
    stream
        .set_recv_timeout(Some(Duration::from_secs(5)))
        .expect("set recv timeout");
    let mut buffer = [0_u8; proto::MAX_FRAME_LENGTH];
    let count = stream.recv(&mut buffer).expect("recv hello_ack");
    let (message, _) = proto::decode(&buffer[..count]).expect("decode hello_ack");
    assert!(
        matches!(message, proto::Message::HelloAck { .. }),
        "expected hello_ack, got {message:?}"
    );
    stream
}

/// A delivered protocol message.
struct Delivered(proto::Message);

/// Reads one message with a per-call timeout. `None` when nothing arrived in
/// the window (the caller decides whether to retry or fail).
fn next_delivered(stream: &SeqPacketStream, wait: Duration) -> Option<Delivered> {
    stream
        .set_recv_timeout(Some(wait))
        .expect("set recv timeout");
    let mut buffer = [0_u8; proto::MAX_FRAME_LENGTH];
    loop {
        match stream.recv(&mut buffer) {
            Err(error) if error.kind() == ErrorKind::WouldBlock => return None,
            Err(error) => panic!("recv must not fail while the broker is alive: {error}"),
            Ok(0) => panic!("broker closed the connection unexpectedly"),
            Ok(count) => {
                let (message, _consumed) = proto::decode(&buffer[..count]).expect("decode frame");
                if let proto::Message::Heartbeat { .. } = message {
                    continue; // none by default, but tolerate a future change
                }
                return Some(Delivered(message));
            }
        }
    }
}

/// Asserts the per-session sequence contract and advances `last_sequence`.
fn tracked_sequence(message: &proto::Message, last_sequence: &mut u64) {
    let sequence = match message {
        proto::Message::Keyboard { sequence, .. }
        | proto::Message::PointerButton { sequence, .. }
        | proto::Message::Reset { sequence, .. } => *sequence,
        _ => panic!("unexpected non-event message: {message:?}"),
    };
    assert_eq!(
        sequence,
        *last_sequence + 1,
        "per-session sequence must be strictly +1 across all traffic"
    );
    *last_sequence = sequence;
}

fn keyboard_of(message: &proto::Message) -> Option<(u16, u16)> {
    match message {
        proto::Message::Keyboard { code, value, .. } => Some((*code, *value)),
        _ => None,
    }
}

/// Emits `emit` and waits for `predicate` to be satisfied. Each retry first
/// emits `retake` before `emit` again: the kernel drops a duplicate down or
/// duplicate up transition (`test_and_set_bit` in `input_handle_event`), so
/// re-emitting the same transition forever would never reach the broker. This
/// mirrors a real user retapping the key. Consumed-but-unmatched traffic
/// (from the machine's real devices or earlier rounds) keeps the sequence
/// contract checked.
fn emit_until(
    stream: &SeqPacketStream,
    last_sequence: &mut u64,
    emit: impl Fn() -> std::io::Result<()>,
    retake: impl Fn() -> std::io::Result<()>,
    predicate: impl Fn(&proto::Message) -> bool,
    deadline: Instant,
) -> proto::Message {
    let mut first = true;
    loop {
        assert!(Instant::now() < deadline, "timed out waiting for event");
        if first {
            first = false;
        } else {
            retake().expect("retake");
        }
        emit().expect("emit");
        if let Some(Delivered(message)) = next_delivered(stream, Duration::from_millis(300)) {
            tracked_sequence(&message, last_sequence);
            if predicate(&message) {
                return message;
            }
        }
    }
}

/// Waits for an already-in-flight event without re-emitting.
fn wait_for(
    stream: &SeqPacketStream,
    last_sequence: &mut u64,
    predicate: impl Fn(&proto::Message) -> bool,
    deadline: Instant,
) -> proto::Message {
    loop {
        assert!(Instant::now() < deadline, "timed out waiting for event");
        let Some(Delivered(message)) = next_delivered(stream, Duration::from_millis(300)) else {
            continue;
        };
        tracked_sequence(&message, last_sequence);
        if predicate(&message) {
            return message;
        }
    }
}

fn lock_serial() -> std::sync::MutexGuard<'static, ()> {
    static LOCK: OnceLock<Mutex<()>> = OnceLock::new();
    let lock = LOCK.get_or_init(|| Mutex::new(()));
    lock.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

#[test]
fn real_evdev_path_filters_hotplug_and_reports_removal() {
    if !uinput_available() {
        eprintln!("skipping: /dev/uinput unavailable in this environment");
        return;
    }
    let _serial = lock_serial();

    // Create the first virtual keyboard before the broker starts: the initial
    // directory scan must discover it.
    let device1 =
        UinputDevice::create_named("battuta-test-vkbd-1", &[KEY_A, KEY_Y, KEY_Z, 42, BTN_LEFT])
            .expect("create uinput keyboard 1");
    // udev needs a moment to chmod the new event node from 0600 root:root to
    // group-readable; the broker's periodic resync recovers either way, but a
    // short settle keeps the first discovery deterministic.
    std::thread::sleep(Duration::from_millis(1500));
    let socket_path = unique_socket_path("main");
    let _broker = spawn_real_broker(&socket_path, &["--no-vt-gate"]);

    let client = handshake(&socket_path);
    let mut last_sequence = 0_u64;
    let step_deadline = || Instant::now() + Duration::from_secs(10);

    // 1. Press → repeat → release of KEY_A through the real kernel path.
    let message = emit_until(
        &client,
        &mut last_sequence,
        || device1.emit_key(KEY_A, 1),
        || device1.emit_key(KEY_A, 0),
        |m| keyboard_of(m) == Some((KEY_A, proto::EVDEV_VALUE_PRESS)),
        step_deadline(),
    );
    match message {
        proto::Message::Keyboard {
            origin,
            monotonic_ns,
            ..
        } => {
            assert_eq!(origin, proto::Origin::Hardware);
            assert!(monotonic_ns > 0);
        }
        other => panic!("expected keyboard event, got {other:?}"),
    }
    emit_until(
        &client,
        &mut last_sequence,
        || device1.emit_key(KEY_A, 2),
        || {
            device1.emit_key(KEY_A, 0)?;
            device1.emit_key(KEY_A, 1)
        },
        |m| keyboard_of(m) == Some((KEY_A, proto::EVDEV_VALUE_REPEAT)),
        step_deadline(),
    );
    emit_until(
        &client,
        &mut last_sequence,
        || device1.emit_key(KEY_A, 0),
        || device1.emit_key(KEY_A, 1),
        |m| keyboard_of(m) == Some((KEY_A, proto::EVDEV_VALUE_RELEASE)),
        step_deadline(),
    );

    // 2. Mouse button: BTN_LEFT is one of the five forwarded buttons.
    emit_until(
        &client,
        &mut last_sequence,
        || device1.emit_key(BTN_LEFT, 1),
        || device1.emit_key(BTN_LEFT, 0),
        |m| {
            matches!(
                m,
                proto::Message::PointerButton {
                    code: BTN_LEFT,
                    value: 1,
                    ..
                }
            )
        },
        step_deadline(),
    );
    emit_until(
        &client,
        &mut last_sequence,
        || device1.emit_key(BTN_LEFT, 0),
        || device1.emit_key(BTN_LEFT, 1),
        |m| {
            matches!(
                m,
                proto::Message::PointerButton {
                    code: BTN_LEFT,
                    value: 0,
                    ..
                }
            )
        },
        step_deadline(),
    );

    // 3. Code filtering: BTN_FORWARD (0x115) is not among the forwarded
    //    buttons and must never reach the subscriber; the following KEY_Y
    //    press proves the pipeline stayed live.
    let deadline = step_deadline();
    loop {
        assert!(Instant::now() < deadline, "timed out scanning for KEY_Y");
        device1
            .emit_key(BTN_FORWARD, 1)
            .expect("emit filtered button");
        device1
            .emit_key(BTN_FORWARD, 0)
            .expect("emit filtered button");
        device1.emit_key(KEY_Y, 1).expect("emit Y press");
        let Some(Delivered(message)) = next_delivered(&client, Duration::from_millis(300)) else {
            continue;
        };
        tracked_sequence(&message, &mut last_sequence);
        if let Some((code, _value)) = keyboard_of(&message) {
            assert_ne!(
                code, BTN_FORWARD,
                "BTN_FORWARD must be dropped at the read boundary"
            );
        }
        if keyboard_of(&message) == Some((KEY_Y, proto::EVDEV_VALUE_PRESS)) {
            break;
        }
    }
    emit_until(
        &client,
        &mut last_sequence,
        || device1.emit_key(KEY_Y, 0),
        || device1.emit_key(KEY_Y, 1),
        |m| keyboard_of(m) == Some((KEY_Y, proto::EVDEV_VALUE_RELEASE)),
        step_deadline(),
    );

    // 4. Hotplug: a second device created AFTER the broker started must be
    //    discovered via inotify. The settle gives udev time to make the new
    //    node readable so the first discovery is deterministic.
    let device2 =
        UinputDevice::create_named("battuta-test-vkbd-2", &[KEY_Z]).expect("create keyboard 2");
    std::thread::sleep(Duration::from_millis(500));
    emit_until(
        &client,
        &mut last_sequence,
        || device2.emit_key(KEY_Z, 1),
        || device2.emit_key(KEY_Z, 0),
        |m| keyboard_of(m) == Some((KEY_Z, proto::EVDEV_VALUE_PRESS)),
        Instant::now() + Duration::from_secs(10),
    );

    // 5. Removal: destroy device 2 while KEY_Z is held. The broker must
    //    synthesize the release and a device-change reset so no key sticks.
    device2.destroy().expect("destroy device 2");
    wait_for(
        &client,
        &mut last_sequence,
        |m| keyboard_of(m) == Some((KEY_Z, proto::EVDEV_VALUE_RELEASE)),
        step_deadline(),
    );
    wait_for(
        &client,
        &mut last_sequence,
        |m| {
            matches!(
                m,
                proto::Message::Reset {
                    reason: proto::ResetReason::DeviceChange,
                    ..
                }
            )
        },
        step_deadline(),
    );

    // The first device stays alive; its pipeline must still work.
    emit_until(
        &client,
        &mut last_sequence,
        || device1.emit_key(KEY_A, 1),
        || device1.emit_key(KEY_A, 0),
        |m| keyboard_of(m) == Some((KEY_A, proto::EVDEV_VALUE_PRESS)),
        step_deadline(),
    );
    device1.destroy().expect("destroy device 1");
}

#[test]
fn session_gate_pauses_delivery_when_graphical_vt_is_absent() {
    let _serial = lock_serial();
    // VT 999 never exists, so the gate must close and the first message the
    // subscriber sees is Reset(SessionPause) — no events before it.
    let socket_path = unique_socket_path("gate");
    let _broker = spawn_real_broker(
        &socket_path,
        &["--graphical-vtnr", "999", "--devices-dir", "/dev/input"],
    );
    let client = handshake(&socket_path);
    let Some(Delivered(message)) = next_delivered(&client, Duration::from_secs(5)) else {
        panic!("no message after handshake; expected Reset(SessionPause)");
    };
    match message {
        proto::Message::Reset {
            reason: proto::ResetReason::SessionPause,
            sequence,
            ..
        } => assert_eq!(sequence, 1, "the pause reset opens the session sequence"),
        other => panic!("expected Reset(SessionPause) as first message, got {other:?}"),
    }
}
