//! Golden fixture contract: the Rust codec must decode every committed
//! fixture to the documented message and re-encode it byte-identically. This
//! is the cross-implementation gate against the C# codec in Battuta.Engine.

use battuta_input_protocol::*;
use std::path::PathBuf;

fn fixtures_dir() -> PathBuf {
    let mut path = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    // crate -> crates -> native -> BattutaLinux -> repo root
    path.pop();
    path.pop();
    path.pop();
    path.pop();
    path.join("shared/contracts/fixtures/broker")
}

fn read_fixture(name: &str) -> Vec<u8> {
    std::fs::read(fixtures_dir().join(name))
        .unwrap_or_else(|error| panic!("failed to read fixture {name}: {error}"))
}

fn expected_hello() -> Message {
    Message::Hello {
        major: 1,
        minor: 0,
        caps: 0,
        client_name: "battuta-test/1.0".to_owned(),
    }
}

fn expected_hello_ack() -> Message {
    Message::HelloAck {
        major: 1,
        minor: 0,
        caps: 0,
        heartbeat_interval_ms: 1000,
        seat: "seat0".to_owned(),
    }
}

#[test]
fn hello_fixture_is_canonical() {
    let bytes = read_fixture("broker.v1.hello.bin");
    let (decoded, consumed) = decode(&bytes).unwrap();
    assert_eq!(consumed, bytes.len());
    assert_eq!(decoded, expected_hello());
    assert_eq!(encode_frame(&decoded).unwrap(), bytes);
}

#[test]
fn hello_ack_fixture_is_canonical() {
    let bytes = read_fixture("broker.v1.hello-ack.bin");
    let (decoded, consumed) = decode(&bytes).unwrap();
    assert_eq!(consumed, bytes.len());
    assert_eq!(decoded, expected_hello_ack());
    assert_eq!(encode_frame(&decoded).unwrap(), bytes);
}

#[test]
fn keyboard_press_fixture_is_canonical() {
    let bytes = read_fixture("broker.v1.keyboard-press.bin");
    let expected = Message::Keyboard {
        sequence: 1,
        monotonic_ns: 100_000_000_000,
        code: 30, // KEY_A
        value: EVDEV_VALUE_PRESS,
        origin: Origin::Hardware,
    };
    let (decoded, consumed) = decode(&bytes).unwrap();
    assert_eq!(consumed, bytes.len());
    assert_eq!(decoded, expected);
    assert_eq!(encode_frame(&decoded).unwrap(), bytes);
}

#[test]
fn keyboard_repeat_fixture_is_canonical() {
    let bytes = read_fixture("broker.v1.keyboard-repeat.bin");
    let expected = Message::Keyboard {
        sequence: 42,
        monotonic_ns: 100_000_050_000,
        code: 30,
        value: EVDEV_VALUE_REPEAT,
        origin: Origin::Hardware,
    };
    let (decoded, consumed) = decode(&bytes).unwrap();
    assert_eq!(consumed, bytes.len());
    assert_eq!(decoded, expected);
    assert_eq!(encode_frame(&decoded).unwrap(), bytes);
}

#[test]
fn pointer_button_fixture_is_canonical() {
    let bytes = read_fixture("broker.v1.pointer-button.bin");
    let expected = Message::PointerButton {
        sequence: 7,
        monotonic_ns: 100_000_025_000,
        code: btn::LEFT,
        value: EVDEV_VALUE_PRESS,
        origin: Origin::Hardware,
    };
    let (decoded, consumed) = decode(&bytes).unwrap();
    assert_eq!(consumed, bytes.len());
    assert_eq!(decoded, expected);
    assert_eq!(encode_frame(&decoded).unwrap(), bytes);
}

#[test]
fn reset_fixture_is_canonical() {
    let bytes = read_fixture("broker.v1.reset.bin");
    let expected = Message::Reset {
        sequence: 9,
        monotonic_ns: 100_000_100_000,
        reason: ResetReason::BacklogDrop,
    };
    let (decoded, consumed) = decode(&bytes).unwrap();
    assert_eq!(consumed, bytes.len());
    assert_eq!(decoded, expected);
    assert_eq!(encode_frame(&decoded).unwrap(), bytes);
}

#[test]
fn heartbeat_fixture_is_canonical() {
    let bytes = read_fixture("broker.v1.heartbeat.bin");
    let expected = Message::Heartbeat {
        sequence: 12,
        monotonic_ns: 100_000_200_000,
    };
    let (decoded, consumed) = decode(&bytes).unwrap();
    assert_eq!(consumed, bytes.len());
    assert_eq!(decoded, expected);
    assert_eq!(encode_frame(&decoded).unwrap(), bytes);
}

#[test]
fn error_fixture_is_canonical() {
    let bytes = read_fixture("broker.v1.error.bin");
    let expected = Message::Error {
        code: ErrorCode::Busy,
        message: "another subscriber is active".to_owned(),
    };
    let (decoded, consumed) = decode(&bytes).unwrap();
    assert_eq!(consumed, bytes.len());
    assert_eq!(decoded, expected);
    assert_eq!(encode_frame(&decoded).unwrap(), bytes);
}

#[test]
fn unknown_skip_fixture_decodes_as_unknown() {
    let bytes = read_fixture("broker.v1.unknown-skip.bin");
    let expected = Message::Unknown {
        type_id: 0xC8,
        payload: b"future-extension-data".to_vec(),
    };
    let (decoded, consumed) = decode(&bytes).unwrap();
    assert_eq!(consumed, bytes.len());
    assert_eq!(decoded, expected);
    assert_eq!(encode_frame(&decoded).unwrap(), bytes);
}

#[test]
fn session_sample_fixture_decodes_to_exact_transcript() {
    let bytes = read_fixture("broker.v1.session-sample.bin");
    let expected = vec![
        expected_hello(),
        expected_hello_ack(),
        Message::Keyboard {
            sequence: 1,
            monotonic_ns: 100_000_000_000,
            code: 30,
            value: EVDEV_VALUE_PRESS,
            origin: Origin::Hardware,
        },
        Message::Keyboard {
            sequence: 42,
            monotonic_ns: 100_000_050_000,
            code: 30,
            value: EVDEV_VALUE_REPEAT,
            origin: Origin::Hardware,
        },
        Message::PointerButton {
            sequence: 7,
            monotonic_ns: 100_000_025_000,
            code: btn::LEFT,
            value: EVDEV_VALUE_PRESS,
            origin: Origin::Hardware,
        },
        Message::Heartbeat {
            sequence: 12,
            monotonic_ns: 100_000_200_000,
        },
        Message::Reset {
            sequence: 9,
            monotonic_ns: 100_000_100_000,
            reason: ResetReason::BacklogDrop,
        },
        Message::Keyboard {
            sequence: 13,
            monotonic_ns: 100_000_300_000,
            code: 30,
            value: EVDEV_VALUE_RELEASE,
            origin: Origin::Hardware,
        },
    ];

    let mut offset = 0;
    let mut decoded = Vec::new();
    while offset < bytes.len() {
        let (message, consumed) = decode(&bytes[offset..]).expect("session-sample must decode");
        offset += consumed;
        decoded.push(message);
    }

    assert_eq!(decoded, expected);
}
