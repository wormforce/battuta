//! End-to-end validation of the PR 4 verification goal: handshake,
//! single-subscriber enforcement, event sequencing, disconnect, and reconnect
//! with a fresh session — against a real broker process speaking over a real
//! SOCK_SEQPACKET socket, driven by the synthetic event source.

use battuta_input_protocol as proto;
use battuta_inputd::seqpacket::SeqPacketStream;
use std::io::ErrorKind;
use std::process::{Child, Command};
use std::time::{Duration, Instant};

struct BrokerProcess(Child);

impl Drop for BrokerProcess {
    fn drop(&mut self) {
        let _ = self.0.kill();
        let _ = self.0.wait();
    }
}

fn unique_socket_path() -> std::path::PathBuf {
    static COUNTER: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);
    let unique = COUNTER.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
    std::env::temp_dir().join(format!(
        "battuta-inputd-e2e-{}-{unique}/input.sock",
        std::process::id()
    ))
}

fn spawn_broker(socket_path: &std::path::Path) -> BrokerProcess {
    let child = Command::new(env!("CARGO_BIN_EXE_battuta-inputd"))
        .arg("--socket-path")
        .arg(socket_path)
        .arg("--synthetic")
        .arg("--synthetic-interval-ms")
        .arg("10")
        .arg("--dev-auth")
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

fn recv_message(stream: &SeqPacketStream) -> proto::Message {
    stream
        .set_recv_timeout(Some(Duration::from_secs(2)))
        .expect("set recv timeout");
    let mut buffer = [0_u8; proto::MAX_FRAME_LENGTH];
    loop {
        let count = stream
            .recv(&mut buffer)
            .expect("recv must not fail while the broker is alive");
        if count == 0 {
            panic!("broker closed the connection unexpectedly");
        }
        let (message, _consumed) = proto::decode(&buffer[..count]).expect("valid frame");
        // The synthetic session interleaves no heartbeats by default.
        if let proto::Message::Heartbeat { .. } = message {
            continue;
        }
        return message;
    }
}

fn handshake(socket_path: &std::path::Path) -> (SeqPacketStream, proto::Message) {
    let stream = connect_with_retry(socket_path);
    let hello = proto::Message::Hello {
        major: proto::PROTOCOL_MAJOR,
        minor: proto::PROTOCOL_MINOR,
        caps: 0,
        client_name: "e2e-test".to_owned(),
    };
    stream
        .send(&proto::encode_frame(&hello).expect("encode hello"))
        .expect("send hello");
    let ack = recv_message(&stream);
    (stream, ack)
}

#[test]
fn handshake_events_busy_reject_and_fresh_session_after_disconnect() {
    let socket_path = unique_socket_path();
    let _broker = spawn_broker(&socket_path);

    // --- 1. Client 1: handshake + scripted events with strict sequencing.
    let (client1, ack) = handshake(&socket_path);
    let proto::Message::HelloAck {
        major,
        minor,
        heartbeat_interval_ms,
        seat,
        ..
    } = ack
    else {
        panic!("expected hello_ack, got {ack:?}");
    };
    assert_eq!(major, proto::PROTOCOL_MAJOR);
    assert_eq!(minor, proto::PROTOCOL_MINOR);
    assert_eq!(heartbeat_interval_ms, 0);
    assert_eq!(seat, "seat0");

    // The script starts: A press/repeat/release, shift press, left press, …
    // Sequence numbers are strictly +1 from 1.
    let expected: Vec<proto::Message> = vec![
        proto::Message::Keyboard {
            sequence: 1,
            monotonic_ns: 0, // checked loosely below
            code: battuta_inputd::synthetic::key::KEY_A,
            value: proto::EVDEV_VALUE_PRESS,
            origin: proto::Origin::Synthetic,
        },
        proto::Message::Keyboard {
            sequence: 2,
            monotonic_ns: 0,
            code: battuta_inputd::synthetic::key::KEY_A,
            value: proto::EVDEV_VALUE_REPEAT,
            origin: proto::Origin::Synthetic,
        },
        proto::Message::Keyboard {
            sequence: 3,
            monotonic_ns: 0,
            code: battuta_inputd::synthetic::key::KEY_A,
            value: proto::EVDEV_VALUE_RELEASE,
            origin: proto::Origin::Synthetic,
        },
        proto::Message::Keyboard {
            sequence: 4,
            monotonic_ns: 0,
            code: battuta_inputd::synthetic::key::KEY_LEFTSHIFT,
            value: proto::EVDEV_VALUE_PRESS,
            origin: proto::Origin::Synthetic,
        },
        proto::Message::PointerButton {
            sequence: 5,
            monotonic_ns: 0,
            code: battuta_inputd::synthetic::button::LEFT,
            value: proto::EVDEV_VALUE_PRESS,
            origin: proto::Origin::Synthetic,
        },
    ];
    let mut last_sequence = 0_u64;
    for (index, expected_message) in expected.iter().enumerate() {
        let message = recv_message(&client1);
        assert_eq!(
            message.message_type(),
            expected_message.message_type(),
            "event {index} type"
        );
        match (&message, expected_message) {
            (
                proto::Message::Keyboard {
                    sequence,
                    monotonic_ns,
                    code,
                    value,
                    origin,
                },
                proto::Message::Keyboard {
                    code: e_code,
                    value: e_value,
                    origin: e_origin,
                    ..
                },
            )
            | (
                proto::Message::PointerButton {
                    sequence,
                    monotonic_ns,
                    code,
                    value,
                    origin,
                },
                proto::Message::PointerButton {
                    code: e_code,
                    value: e_value,
                    origin: e_origin,
                    ..
                },
            ) => {
                assert_eq!(code, e_code);
                assert_eq!(value, e_value);
                assert_eq!(origin, e_origin);
                assert_eq!(*sequence, last_sequence + 1, "sequence must be strictly +1");
                assert!(*monotonic_ns > 0);
            }
            (
                proto::Message::Reset {
                    sequence, reason, ..
                },
                proto::Message::Reset {
                    reason: e_reason, ..
                },
            ) => {
                assert_eq!(reason, e_reason);
                assert_eq!(*sequence, last_sequence + 1);
            }
            (actual, expected_message) => {
                panic!("message mismatch: {actual:?} vs {expected_message:?}")
            }
        }
        last_sequence = match &message {
            proto::Message::Keyboard { sequence, .. }
            | proto::Message::PointerButton { sequence, .. }
            | proto::Message::Reset { sequence, .. } => *sequence,
            _ => unreachable!(),
        };
    }

    // --- 2. Client 2 while client 1 is active: rejected as busy.
    {
        let (client2, ack2) = handshake(&socket_path);
        // The broker either rejects the hello with an error before the ack, or
        // answers the handshake and then refuses the subscription. Accept both
        // orders but require an explicit busy error either way.
        let error = match ack2 {
            proto::Message::Error {
                code: proto::ErrorCode::Busy,
                ..
            } => ack2,
            proto::Message::HelloAck { .. } => recv_message(&client2),
            other => panic!("expected busy error or ack, got {other:?}"),
        };
        assert!(
            matches!(
                error,
                proto::Message::Error {
                    code: proto::ErrorCode::Busy,
                    ..
                }
            ),
            "expected busy error, got {error:?}"
        );
        drop(client2);
    }

    // --- 3. Disconnect client 1; the next client gets a fresh session.
    drop(client1);
    std::thread::sleep(Duration::from_millis(200)); // allow the slot to free

    let (client3, ack3) = handshake(&socket_path);
    assert!(
        matches!(ack3, proto::Message::HelloAck { .. }),
        "expected hello_ack after reconnect, got {ack3:?}"
    );
    // Sequence restarts at 1 per session (contract section 4).
    let first = recv_message(&client3);
    match first {
        proto::Message::Keyboard { sequence, .. } => assert_eq!(sequence, 1),
        other => panic!("expected a keyboard event to restart the session, got {other:?}"),
    }
}
