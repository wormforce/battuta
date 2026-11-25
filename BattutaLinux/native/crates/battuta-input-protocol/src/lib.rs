//! Wire codec for the Battuta Linux input broker protocol v1.
//!
//! The byte layout is frozen by `shared/contracts/LINUX_INPUT_BROKER_PROTOCOL.md`.
//! The C# implementation in `shared/dotnet/Battuta.Engine/Input/Broker/` must
//! stay byte-identical: the golden fixtures under
//! `shared/contracts/fixtures/broker/` are the cross-implementation gate, and
//! both test suites assert encode/decode roundtrips against them.

use std::fmt;

/// Magic bytes at the start of every frame: ASCII "BTTI".
pub const MAGIC: [u8; 4] = [0x42, 0x54, 0x54, 0x49];

/// Current protocol major. A mismatch is a hard error.
pub const PROTOCOL_MAJOR: u16 = 1;

/// Current protocol minor. Larger minors may append payload bytes.
pub const PROTOCOL_MINOR: u16 = 0;

/// Frame header length: magic(4) + type(1) + flags(1) + payload_length(2).
pub const HEADER_LENGTH: usize = 8;

/// Maximum payload length in bytes.
pub const MAX_PAYLOAD_LENGTH: usize = 4096;

/// Maximum total frame length (header + payload).
pub const MAX_FRAME_LENGTH: usize = HEADER_LENGTH + MAX_PAYLOAD_LENGTH;

/// Maximum client_name length in hello.
pub const MAX_CLIENT_NAME_LENGTH: usize = 64;

/// Maximum seat name length in hello_ack.
pub const MAX_SEAT_NAME_LENGTH: usize = 32;

/// Maximum diagnostic message length in error.
pub const MAX_DIAGNOSTIC_MESSAGE_LENGTH: usize = 128;

/// Fixed payload length of keyboard and pointer_button.
pub const EVENT_PAYLOAD_LENGTH: usize = 24;

/// Fixed payload length of reset (sequence + monotonic + reason + reserved).
pub const RESET_PAYLOAD_LENGTH: usize = 24;

/// Fixed payload length of heartbeat (sequence + monotonic only).
pub const HEARTBEAT_PAYLOAD_LENGTH: usize = 16;

/// evdev value for a release transition.
pub const EVDEV_VALUE_RELEASE: u16 = 0;

/// evdev value for a press transition.
pub const EVDEV_VALUE_PRESS: u16 = 1;

/// evdev value for an auto-repeat event.
pub const EVDEV_VALUE_REPEAT: u16 = 2;

/// Raw evdev BTN_* codes carried by pointer_button frames (v1 allow list).
pub mod btn {
    pub const LEFT: u16 = 0x110;
    pub const RIGHT: u16 = 0x111;
    pub const MIDDLE: u16 = 0x112;
    pub const SIDE: u16 = 0x113;
    pub const EXTRA: u16 = 0x114;
}

/// Wire message type bytes (section 3 of the contract).
pub mod message_type {
    pub const HELLO: u8 = 0x01;
    pub const HELLO_ACK: u8 = 0x02;
    pub const KEYBOARD: u8 = 0x03;
    pub const POINTER_BUTTON: u8 = 0x04;
    pub const RESET: u8 = 0x05;
    pub const HEARTBEAT: u8 = 0x06;
    pub const ERROR: u8 = 0x07;
}

/// Event origin byte. Unknown values decode to [`Origin::Unknown`].
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
#[repr(u8)]
pub enum Origin {
    Hardware = 0,
    Synthetic = 1,
    Unknown = 2,
}

impl Origin {
    pub fn from_raw(value: u8) -> Self {
        match value {
            0 => Origin::Hardware,
            1 => Origin::Synthetic,
            _ => Origin::Unknown,
        }
    }
}

/// Reset reason byte. Unknown values decode to [`ResetReason::Other`].
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
#[repr(u8)]
pub enum ResetReason {
    SequenceGap = 1,
    DeviceChange = 2,
    SessionPause = 3,
    SessionResume = 4,
    BacklogDrop = 5,
    Reconnect = 6,
    Other = 7,
}

impl ResetReason {
    pub fn from_raw(value: u8) -> Self {
        match value {
            1 => ResetReason::SequenceGap,
            2 => ResetReason::DeviceChange,
            3 => ResetReason::SessionPause,
            4 => ResetReason::SessionResume,
            5 => ResetReason::BacklogDrop,
            6 => ResetReason::Reconnect,
            _ => ResetReason::Other,
        }
    }
}

/// Error code. Unknown values decode to [`ErrorCode::Internal`].
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
#[repr(u32)]
pub enum ErrorCode {
    VersionMismatch = 1,
    FrameTooLarge = 2,
    BadMagic = 3,
    Unauthorized = 4,
    Busy = 5,
    InvalidPayload = 6,
    SequenceRegression = 7,
    Internal = 8,
}

impl ErrorCode {
    pub fn from_raw(value: u32) -> Self {
        match value {
            1 => ErrorCode::VersionMismatch,
            2 => ErrorCode::FrameTooLarge,
            3 => ErrorCode::BadMagic,
            4 => ErrorCode::Unauthorized,
            5 => ErrorCode::Busy,
            6 => ErrorCode::InvalidPayload,
            7 => ErrorCode::SequenceRegression,
            _ => ErrorCode::Internal,
        }
    }
}

/// One decoded broker message. Payload bytes beyond the documented length of
/// a known message are ignored (minor-version tolerance).
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Message {
    Hello {
        major: u16,
        minor: u16,
        caps: u32,
        client_name: String,
    },
    HelloAck {
        major: u16,
        minor: u16,
        caps: u32,
        heartbeat_interval_ms: u32,
        seat: String,
    },
    Keyboard {
        sequence: u64,
        monotonic_ns: u64,
        code: u16,
        value: u16,
        origin: Origin,
    },
    PointerButton {
        sequence: u64,
        monotonic_ns: u64,
        code: u16,
        value: u16,
        origin: Origin,
    },
    Reset {
        sequence: u64,
        monotonic_ns: u64,
        reason: ResetReason,
    },
    Heartbeat {
        sequence: u64,
        monotonic_ns: u64,
    },
    Error {
        code: ErrorCode,
        message: String,
    },
    /// A frame whose message type is unknown to this implementation. Re-encoding
    /// reproduces the original bytes.
    Unknown {
        type_id: u8,
        payload: Vec<u8>,
    },
}

impl Message {
    /// The wire message type byte this message encodes to.
    pub fn message_type(&self) -> u8 {
        match self {
            Message::Hello { .. } => message_type::HELLO,
            Message::HelloAck { .. } => message_type::HELLO_ACK,
            Message::Keyboard { .. } => message_type::KEYBOARD,
            Message::PointerButton { .. } => message_type::POINTER_BUTTON,
            Message::Reset { .. } => message_type::RESET,
            Message::Heartbeat { .. } => message_type::HEARTBEAT,
            Message::Error { .. } => message_type::ERROR,
            Message::Unknown { type_id, .. } => *type_id,
        }
    }
}

/// Encoding failure. String fields are already valid UTF-8, so only length
/// and version rules can fail.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum EncodeError {
    NameTooLong {
        field: &'static str,
        length: usize,
        max: usize,
    },
    InvalidMajor {
        major: u16,
    },
    BufferTooSmall {
        needed: usize,
        available: usize,
    },
}

impl fmt::Display for EncodeError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            EncodeError::NameTooLong { field, length, max } => {
                write!(
                    f,
                    "{field} is {length} bytes, exceeding the {max}-byte limit"
                )
            }
            EncodeError::InvalidMajor { major } => {
                write!(
                    f,
                    "only protocol major {PROTOCOL_MAJOR} can be encoded, got {major}"
                )
            }
            EncodeError::BufferTooSmall { needed, available } => {
                write!(
                    f,
                    "destination buffer is too small: needs {needed} bytes, has {available}"
                )
            }
        }
    }
}

impl std::error::Error for EncodeError {}

/// Decoding failure. `Incomplete` is not fatal: the transport simply has to
/// deliver more bytes. Every other variant is a protocol violation.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum DecodeError {
    Incomplete { needed: usize },
    BadMagic,
    FrameTooLarge { length: usize },
    InvalidPayload(&'static str),
}

impl fmt::Display for DecodeError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            DecodeError::Incomplete { needed } => {
                write!(f, "incomplete frame: need {needed} more bytes")
            }
            DecodeError::BadMagic => write!(f, "frame magic mismatch"),
            DecodeError::FrameTooLarge { length } => {
                write!(
                    f,
                    "payload length {length} exceeds the {MAX_PAYLOAD_LENGTH}-byte limit"
                )
            }
            DecodeError::InvalidPayload(why) => write!(f, "invalid payload: {why}"),
        }
    }
}

impl std::error::Error for DecodeError {}

/// Exact encoded frame length for a message.
pub fn frame_length(message: &Message) -> Result<usize, EncodeError> {
    let payload = match message {
        Message::Hello { client_name, .. } => {
            9 + checked_name_length(client_name, MAX_CLIENT_NAME_LENGTH, "client name")?
        }
        Message::HelloAck { seat, .. } => {
            13 + checked_name_length(seat, MAX_SEAT_NAME_LENGTH, "seat")?
        }
        Message::Keyboard { .. } | Message::PointerButton { .. } => EVENT_PAYLOAD_LENGTH,
        Message::Reset { .. } => RESET_PAYLOAD_LENGTH,
        Message::Heartbeat { .. } => HEARTBEAT_PAYLOAD_LENGTH,
        Message::Error { message, .. } => {
            5 + checked_name_length(message, MAX_DIAGNOSTIC_MESSAGE_LENGTH, "error message")?
        }
        Message::Unknown { payload, .. } => payload.len(),
    };
    Ok(HEADER_LENGTH + payload)
}

fn checked_name_length(value: &str, max: usize, field: &'static str) -> Result<usize, EncodeError> {
    let length = value.len();
    if length > max {
        return Err(EncodeError::NameTooLong { field, length, max });
    }
    Ok(length)
}

/// Appends one encoded frame to `out`.
pub fn encode(message: &Message, out: &mut Vec<u8>) -> Result<(), EncodeError> {
    let total = frame_length(message)?;
    let payload_len = (total - HEADER_LENGTH) as u16;
    let start = out.len();
    out.resize(start + total, 0);
    let frame = &mut out[start..];

    frame[..4].copy_from_slice(&MAGIC);
    frame[4] = message.message_type();
    frame[5] = 0; // flags
    frame[6..8].copy_from_slice(&payload_len.to_le_bytes());
    let payload = &mut frame[HEADER_LENGTH..];

    match message {
        Message::Hello {
            major,
            minor,
            caps,
            client_name,
        } => {
            encode_hello_payload(payload, *major, *minor, *caps, client_name)?;
        }
        Message::HelloAck {
            major,
            minor,
            caps,
            heartbeat_interval_ms,
            seat,
        } => {
            encode_hello_ack_payload(payload, *major, *minor, *caps, *heartbeat_interval_ms, seat)?;
        }
        Message::Keyboard {
            sequence,
            monotonic_ns,
            code,
            value,
            origin,
        }
        | Message::PointerButton {
            sequence,
            monotonic_ns,
            code,
            value,
            origin,
        } => {
            encode_event_payload(payload, *sequence, *monotonic_ns, *code, *value, *origin);
        }
        Message::Reset {
            sequence,
            monotonic_ns,
            reason,
        } => {
            payload[..8].copy_from_slice(&sequence.to_le_bytes());
            payload[8..16].copy_from_slice(&monotonic_ns.to_le_bytes());
            payload[16] = *reason as u8;
            // payload[17..24] stays zero.
        }
        Message::Heartbeat {
            sequence,
            monotonic_ns,
        } => {
            payload[..8].copy_from_slice(&sequence.to_le_bytes());
            payload[8..16].copy_from_slice(&monotonic_ns.to_le_bytes());
        }
        Message::Error { code, message } => {
            payload[..4].copy_from_slice(&(*code as u32).to_le_bytes());
            encode_length_prefixed_utf8(
                &mut payload[4..],
                message,
                MAX_DIAGNOSTIC_MESSAGE_LENGTH,
                "error message",
            )?;
        }
        Message::Unknown {
            payload: unknown_payload,
            ..
        } => {
            payload[..unknown_payload.len()].copy_from_slice(unknown_payload);
        }
    }

    Ok(())
}

fn encode_hello_payload(
    payload: &mut [u8],
    major: u16,
    minor: u16,
    caps: u32,
    client_name: &str,
) -> Result<(), EncodeError> {
    if major != PROTOCOL_MAJOR {
        return Err(EncodeError::InvalidMajor { major });
    }
    payload[..2].copy_from_slice(&major.to_le_bytes());
    payload[2..4].copy_from_slice(&minor.to_le_bytes());
    payload[4..8].copy_from_slice(&caps.to_le_bytes());
    encode_length_prefixed_utf8(
        &mut payload[8..],
        client_name,
        MAX_CLIENT_NAME_LENGTH,
        "client name",
    )
}

fn encode_hello_ack_payload(
    payload: &mut [u8],
    major: u16,
    minor: u16,
    caps: u32,
    heartbeat_interval_ms: u32,
    seat: &str,
) -> Result<(), EncodeError> {
    if major != PROTOCOL_MAJOR {
        return Err(EncodeError::InvalidMajor { major });
    }
    payload[..2].copy_from_slice(&major.to_le_bytes());
    payload[2..4].copy_from_slice(&minor.to_le_bytes());
    payload[4..8].copy_from_slice(&caps.to_le_bytes());
    payload[8..12].copy_from_slice(&heartbeat_interval_ms.to_le_bytes());
    encode_length_prefixed_utf8(&mut payload[12..], seat, MAX_SEAT_NAME_LENGTH, "seat")
}

fn encode_event_payload(
    payload: &mut [u8],
    sequence: u64,
    monotonic_ns: u64,
    code: u16,
    value: u16,
    origin: Origin,
) {
    payload[..8].copy_from_slice(&sequence.to_le_bytes());
    payload[8..16].copy_from_slice(&monotonic_ns.to_le_bytes());
    payload[16..18].copy_from_slice(&code.to_le_bytes());
    payload[18..20].copy_from_slice(&value.to_le_bytes());
    payload[20] = origin as u8;
    payload[21] = 0; // reserved
    payload[22..24].copy_from_slice(&0_u16.to_le_bytes()); // reserved
}

fn encode_length_prefixed_utf8(
    destination: &mut [u8],
    value: &str,
    max: usize,
    field: &'static str,
) -> Result<(), EncodeError> {
    let bytes = value.as_bytes();
    if bytes.len() > max {
        return Err(EncodeError::NameTooLong {
            field,
            length: bytes.len(),
            max,
        });
    }
    destination[0] = bytes.len() as u8;
    destination[1..=bytes.len()].copy_from_slice(bytes);
    Ok(())
}

/// Encodes one frame into a freshly allocated vector.
pub fn encode_frame(message: &Message) -> Result<Vec<u8>, EncodeError> {
    let mut out = Vec::with_capacity(frame_length(message)?);
    encode(message, &mut out)?;
    Ok(out)
}

/// Decodes one frame from the front of `source`. Returns the message and the
/// number of bytes consumed. `DecodeError::Incomplete` means the caller must
/// supply more bytes (stream transports); every other error is fatal.
pub fn decode(source: &[u8]) -> Result<(Message, usize), DecodeError> {
    if source.len() < HEADER_LENGTH {
        return Err(DecodeError::Incomplete {
            needed: HEADER_LENGTH - source.len(),
        });
    }

    if source[..4] != MAGIC {
        return Err(DecodeError::BadMagic);
    }

    let message_type = source[4];
    let payload_len = u16::from_le_bytes([source[6], source[7]]) as usize;
    if payload_len > MAX_PAYLOAD_LENGTH {
        return Err(DecodeError::FrameTooLarge {
            length: payload_len,
        });
    }

    let frame_len = HEADER_LENGTH + payload_len;
    if source.len() < frame_len {
        return Err(DecodeError::Incomplete {
            needed: frame_len - source.len(),
        });
    }

    let payload = &source[HEADER_LENGTH..frame_len];
    let message = match message_type {
        message_type::HELLO => decode_hello(payload)?,
        message_type::HELLO_ACK => decode_hello_ack(payload)?,
        message_type::KEYBOARD => {
            if payload.len() < EVENT_PAYLOAD_LENGTH {
                return Err(DecodeError::InvalidPayload("keyboard payload too short"));
            }
            Message::Keyboard {
                sequence: u64::from_le_bytes(payload[0..8].try_into().unwrap()),
                monotonic_ns: u64::from_le_bytes(payload[8..16].try_into().unwrap()),
                code: u16::from_le_bytes(payload[16..18].try_into().unwrap()),
                value: u16::from_le_bytes(payload[18..20].try_into().unwrap()),
                origin: Origin::from_raw(payload[20]),
            }
        }
        message_type::POINTER_BUTTON => {
            if payload.len() < EVENT_PAYLOAD_LENGTH {
                return Err(DecodeError::InvalidPayload(
                    "pointer_button payload too short",
                ));
            }
            Message::PointerButton {
                sequence: u64::from_le_bytes(payload[0..8].try_into().unwrap()),
                monotonic_ns: u64::from_le_bytes(payload[8..16].try_into().unwrap()),
                code: u16::from_le_bytes(payload[16..18].try_into().unwrap()),
                value: u16::from_le_bytes(payload[18..20].try_into().unwrap()),
                origin: Origin::from_raw(payload[20]),
            }
        }
        message_type::RESET => {
            if payload.len() < RESET_PAYLOAD_LENGTH {
                return Err(DecodeError::InvalidPayload("reset payload too short"));
            }
            Message::Reset {
                sequence: u64::from_le_bytes(payload[0..8].try_into().unwrap()),
                monotonic_ns: u64::from_le_bytes(payload[8..16].try_into().unwrap()),
                reason: ResetReason::from_raw(payload[16]),
            }
        }
        message_type::HEARTBEAT => {
            if payload.len() < HEARTBEAT_PAYLOAD_LENGTH {
                return Err(DecodeError::InvalidPayload("heartbeat payload too short"));
            }
            Message::Heartbeat {
                sequence: u64::from_le_bytes(payload[0..8].try_into().unwrap()),
                monotonic_ns: u64::from_le_bytes(payload[8..16].try_into().unwrap()),
            }
        }
        message_type::ERROR => decode_error(payload)?,
        _ => Message::Unknown {
            type_id: message_type,
            payload: payload.to_vec(),
        },
    };

    Ok((message, frame_len))
}

/// Decodes exactly one complete frame; any trailing byte is a violation.
pub fn decode_frame(frame: &[u8]) -> Result<Message, DecodeError> {
    let (message, consumed) = decode(frame)?;
    if consumed != frame.len() {
        return Err(DecodeError::InvalidPayload("trailing bytes after frame"));
    }
    Ok(message)
}

fn decode_hello(payload: &[u8]) -> Result<Message, DecodeError> {
    if payload.len() < 9 {
        return Err(DecodeError::InvalidPayload("hello payload too short"));
    }
    Ok(Message::Hello {
        major: u16::from_le_bytes(payload[0..2].try_into().unwrap()),
        minor: u16::from_le_bytes(payload[2..4].try_into().unwrap()),
        caps: u32::from_le_bytes(payload[4..8].try_into().unwrap()),
        client_name: decode_length_prefixed_utf8(&payload[8..], MAX_CLIENT_NAME_LENGTH)?,
    })
}

fn decode_hello_ack(payload: &[u8]) -> Result<Message, DecodeError> {
    if payload.len() < 13 {
        return Err(DecodeError::InvalidPayload("hello_ack payload too short"));
    }
    Ok(Message::HelloAck {
        major: u16::from_le_bytes(payload[0..2].try_into().unwrap()),
        minor: u16::from_le_bytes(payload[2..4].try_into().unwrap()),
        caps: u32::from_le_bytes(payload[4..8].try_into().unwrap()),
        heartbeat_interval_ms: u32::from_le_bytes(payload[8..12].try_into().unwrap()),
        seat: decode_length_prefixed_utf8(&payload[12..], MAX_SEAT_NAME_LENGTH)?,
    })
}

fn decode_error(payload: &[u8]) -> Result<Message, DecodeError> {
    if payload.len() < 5 {
        return Err(DecodeError::InvalidPayload("error payload too short"));
    }
    Ok(Message::Error {
        code: ErrorCode::from_raw(u32::from_le_bytes(payload[0..4].try_into().unwrap())),
        message: decode_length_prefixed_utf8(&payload[4..], MAX_DIAGNOSTIC_MESSAGE_LENGTH)?,
    })
}

fn decode_length_prefixed_utf8(payload: &[u8], max: usize) -> Result<String, DecodeError> {
    let length = payload[0] as usize;
    if length > max || payload.len() < 1 + length {
        return Err(DecodeError::InvalidPayload("length prefix out of range"));
    }

    let bytes = &payload[1..=length];
    String::from_utf8(bytes.to_vec())
        .map_err(|_| DecodeError::InvalidPayload("field is not valid UTF-8"))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sample_messages() -> Vec<Message> {
        vec![
            Message::Hello {
                major: 1,
                minor: 0,
                caps: 0,
                client_name: "battuta-test/1.0".to_owned(),
            },
            Message::HelloAck {
                major: 1,
                minor: 0,
                caps: 0,
                heartbeat_interval_ms: 1000,
                seat: "seat0".to_owned(),
            },
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
            Message::Reset {
                sequence: 9,
                monotonic_ns: 100_000_100_000,
                reason: ResetReason::BacklogDrop,
            },
            Message::Heartbeat {
                sequence: 12,
                monotonic_ns: 100_000_200_000,
            },
            Message::Error {
                code: ErrorCode::Busy,
                message: "another subscriber is active".to_owned(),
            },
            Message::Unknown {
                type_id: 0xC8,
                payload: b"future-extension-data".to_vec(),
            },
        ]
    }

    #[test]
    fn roundtrips_preserve_messages() {
        for message in sample_messages() {
            let frame = encode_frame(&message).expect("encode");
            let (decoded, consumed) = decode(&frame).expect("decode");
            assert_eq!(consumed, frame.len());
            assert_eq!(decoded, message);
        }
    }

    #[test]
    fn header_layout_matches_contract() {
        let frame = encode_frame(&Message::Keyboard {
            sequence: 1,
            monotonic_ns: 100_000_000_000,
            code: 30,
            value: 1,
            origin: Origin::Hardware,
        })
        .unwrap();

        assert_eq!(frame.len(), HEADER_LENGTH + 24);
        assert_eq!(&frame[..4], &MAGIC);
        assert_eq!(frame[4], message_type::KEYBOARD);
        assert_eq!(frame[5], 0);
        assert_eq!(&frame[6..8], &24_u16.to_le_bytes());
    }

    #[test]
    fn invalid_major_is_rejected_on_encode() {
        let message = Message::Hello {
            major: 2,
            minor: 0,
            caps: 0,
            client_name: "x".to_owned(),
        };
        assert_eq!(
            encode_frame(&message),
            Err(EncodeError::InvalidMajor { major: 2 })
        );
    }

    #[test]
    fn oversized_names_are_rejected_on_encode() {
        let message = Message::Hello {
            major: 1,
            minor: 0,
            caps: 0,
            client_name: "x".repeat(MAX_CLIENT_NAME_LENGTH + 1),
        };
        assert!(matches!(
            encode_frame(&message),
            Err(EncodeError::NameTooLong { .. })
        ));
    }

    #[test]
    fn truncated_input_reports_needed_bytes() {
        let frame = encode_frame(&sample_messages()[0]).unwrap();

        let error = decode(&frame[..3]).unwrap_err();
        assert_eq!(
            error,
            DecodeError::Incomplete {
                needed: HEADER_LENGTH - 3
            }
        );

        let error = decode(&frame[..frame.len() - 1]).unwrap_err();
        assert_eq!(error, DecodeError::Incomplete { needed: 1 });
    }

    #[test]
    fn bad_magic_is_fatal() {
        let mut frame = encode_frame(&sample_messages()[0]).unwrap();
        frame[0] = 0;
        assert_eq!(decode(&frame), Err(DecodeError::BadMagic));
    }

    #[test]
    fn oversized_payload_length_is_fatal() {
        let mut frame = encode_frame(&sample_messages()[5]).unwrap();
        frame[6..8].copy_from_slice(&(MAX_PAYLOAD_LENGTH as u16 + 1).to_le_bytes());
        assert_eq!(
            decode(&frame),
            Err(DecodeError::FrameTooLarge {
                length: MAX_PAYLOAD_LENGTH + 1
            })
        );
    }

    #[test]
    fn trailing_bytes_after_frame_are_rejected_by_decode_frame() {
        let mut frame = encode_frame(&sample_messages()[6]).unwrap();
        frame.push(0);
        assert!(matches!(
            decode_frame(&frame),
            Err(DecodeError::InvalidPayload("trailing bytes after frame"))
        ));
    }

    #[test]
    fn unknown_enum_values_map_to_unknown_counterparts() {
        let mut frame = encode_frame(&Message::Keyboard {
            sequence: 1,
            monotonic_ns: 10,
            code: 30,
            value: 1,
            origin: Origin::Hardware,
        })
        .unwrap();
        frame[HEADER_LENGTH + 20] = 3;
        let (decoded, _) = decode(&frame).unwrap();
        assert_eq!(
            decoded,
            Message::Keyboard {
                sequence: 1,
                monotonic_ns: 10,
                code: 30,
                value: 1,
                origin: Origin::Unknown
            }
        );

        let mut frame = encode_frame(&Message::Reset {
            sequence: 1,
            monotonic_ns: 10,
            reason: ResetReason::SessionPause,
        })
        .unwrap();
        frame[HEADER_LENGTH + 16] = 8;
        let (decoded, _) = decode(&frame).unwrap();
        assert_eq!(
            decoded,
            Message::Reset {
                sequence: 1,
                monotonic_ns: 10,
                reason: ResetReason::Other
            }
        );

        let mut frame = encode_frame(&Message::Error {
            code: ErrorCode::Busy,
            message: "m".to_owned(),
        })
        .unwrap();
        frame[HEADER_LENGTH..HEADER_LENGTH + 4].copy_from_slice(&99_u32.to_le_bytes());
        let (decoded, _) = decode(&frame).unwrap();
        assert_eq!(
            decoded,
            Message::Error {
                code: ErrorCode::Internal,
                message: "m".to_owned()
            }
        );
    }

    #[test]
    fn invalid_utf8_in_name_is_rejected() {
        let mut frame = encode_frame(&Message::Hello {
            major: 1,
            minor: 0,
            caps: 0,
            client_name: "ab".to_owned(),
        })
        .unwrap();
        frame[HEADER_LENGTH + 9] = 0xFF;
        assert!(matches!(
            decode(&frame),
            Err(DecodeError::InvalidPayload("field is not valid UTF-8"))
        ));
    }

    #[test]
    fn name_length_beyond_payload_is_rejected() {
        let mut frame = encode_frame(&Message::Hello {
            major: 1,
            minor: 0,
            caps: 0,
            client_name: "ab".to_owned(),
        })
        .unwrap();
        frame[HEADER_LENGTH + 8] = 200;
        assert!(matches!(
            decode(&frame),
            Err(DecodeError::InvalidPayload("length prefix out of range"))
        ));
    }

    #[test]
    fn minor_extension_payload_bytes_are_ignored() {
        let mut frame = encode_frame(&Message::Keyboard {
            sequence: 1,
            monotonic_ns: 10,
            code: 30,
            value: 1,
            origin: Origin::Hardware,
        })
        .unwrap();
        let appended = 4_u16;
        let original_len = frame.len() as u16;
        frame.extend_from_slice(&[0xAA, 0xBB, 0xCC, 0xDD]);
        frame[6..8]
            .copy_from_slice(&(original_len - HEADER_LENGTH as u16 + appended).to_le_bytes());

        let (decoded, consumed) = decode(&frame).unwrap();
        assert_eq!(consumed, frame.len());
        assert_eq!(
            decoded,
            Message::Keyboard {
                sequence: 1,
                monotonic_ns: 10,
                code: 30,
                value: 1,
                origin: Origin::Hardware
            }
        );
    }
}
