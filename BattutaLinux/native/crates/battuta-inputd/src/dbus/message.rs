//! Minimal D-Bus wire-format subset: value model, marshaling (little-endian)
//! and parsing (both endiannesses).
//!
//! The broker only ever *speaks* little-endian, but system services emit
//! signals in their own byte order, so the parser accepts `'B'` messages too.
//! Everything outside the subset the broker needs (fd passing, 64-bit arrays,
//! UNIX_FD) is rejected loudly rather than silently misparsed.
//!
//! Array alignment follows the rule "an array starts at its element type's
//! alignment, the u32 length sits at that alignment, then padding re-aligns
//! the first element". This is validated against real system services in the
//! integration tests (`polkit` subject dicts, logind reply structs) and
//! against GIO in the mock-bus tests, which are strict independent
//! implementations of the same format.

use std::fmt;

/// Maximum marshaled message size we are willing to produce or accept
/// (D-Bus caps messages at 128 MiB; we stay well below to bound memory).
pub const MAX_MESSAGE_SIZE: usize = 64 * 1024 * 1024;

pub const METHOD_CALL: u8 = 1;
pub const METHOD_RETURN: u8 = 2;
pub const METHOD_ERROR: u8 = 3;
pub const SIGNAL: u8 = 4;

/// Header field codes (D-Bus spec §5.4.3).
const FIELD_PATH: u8 = 1;
const FIELD_INTERFACE: u8 = 2;
const FIELD_MEMBER: u8 = 3;
const FIELD_ERROR_NAME: u8 = 4;
const FIELD_REPLY_SERIAL: u8 = 5;
const FIELD_DESTINATION: u8 = 6;
const FIELD_SENDER: u8 = 7;
const FIELD_SIGNATURE: u8 = 8;

#[derive(Debug, Clone, PartialEq)]
pub enum Value {
    Byte(u8),
    Bool(bool),
    U16(u16),
    I16(i16),
    U32(u32),
    I32(i32),
    U64(u64),
    I64(i64),
    Str(String),
    Path(String),
    Signature(String),
    Array { elem_sig: String, items: Vec<Value> },
    Struct(Vec<Value>),
    DictEntry { key: Box<Value>, value: Box<Value> },
    Variant(Box<Value>),
}

impl Value {
    pub fn str(value: impl Into<String>) -> Self {
        Value::Str(value.into())
    }

    pub fn u32(value: u32) -> Self {
        Value::U32(value)
    }

    /// An empty `a{sv}` / `a{ss}` array (the most common empty argument).
    /// `elem_sig` is the *complete element type*, braces included (`"{sv}"`).
    pub fn empty_dict(elem_sig: &str) -> Self {
        Value::Array {
            elem_sig: elem_sig.to_owned(),
            items: Vec::new(),
        }
    }

    pub fn as_str(&self) -> Option<&str> {
        match self {
            Value::Str(value) | Value::Path(value) | Value::Signature(value) => Some(value),
            _ => None,
        }
    }

    pub fn as_u32(&self) -> Option<u32> {
        match self {
            Value::U32(value) => Some(*value),
            _ => None,
        }
    }

    pub fn as_u64(&self) -> Option<u64> {
        match self {
            Value::U64(value) => Some(*value),
            _ => None,
        }
    }

    pub fn as_bool(&self) -> Option<bool> {
        match self {
            Value::Bool(value) => Some(*value),
            _ => None,
        }
    }

    pub fn as_struct(&self) -> Option<&[Value]> {
        match self {
            Value::Struct(fields) => Some(fields),
            _ => None,
        }
    }

    /// The key/value pair of a dict entry (`a{...}` element).
    pub fn as_dict_entry(&self) -> Option<(&Value, &Value)> {
        match self {
            Value::DictEntry { key, value } => Some((key, value)),
            _ => None,
        }
    }

    pub fn as_array(&self) -> Option<(&str, &[Value])> {
        match self {
            Value::Array { elem_sig, items } => Some((elem_sig.as_str(), items)),
            _ => None,
        }
    }
}

impl fmt::Display for Value {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Value::Byte(value) => write!(f, "{value}y"),
            Value::Bool(value) => write!(f, "{value}"),
            Value::U16(value) => write!(f, "{value}q"),
            Value::I16(value) => write!(f, "{value}n"),
            Value::U32(value) => write!(f, "{value}u"),
            Value::I32(value) => write!(f, "{value}i"),
            Value::U64(value) => write!(f, "{value}t"),
            Value::I64(value) => write!(f, "{value}x"),
            Value::Str(value) => write!(f, "{value:?}"),
            Value::Path(value) => write!(f, "@{value}"),
            Value::Signature(value) => write!(f, "g\"{value}\""),
            Value::Array { items, .. } => {
                f.write_str("[")?;
                for (index, item) in items.iter().enumerate() {
                    if index > 0 {
                        f.write_str(", ")?;
                    }
                    write!(f, "{item}")?;
                }
                f.write_str("]")
            }
            Value::Struct(fields) => {
                f.write_str("(")?;
                for (index, field) in fields.iter().enumerate() {
                    if index > 0 {
                        f.write_str(", ")?;
                    }
                    write!(f, "{field}")?;
                }
                f.write_str(")")
            }
            Value::DictEntry { key, value } => write!(f, "{key}: {value}"),
            Value::Variant(inner) => write!(f, "<{inner}>"),
        }
    }
}

/// A decoded D-Bus message.
#[derive(Debug, Clone, PartialEq)]
pub struct Message {
    pub kind: u8,
    pub flags: u8,
    pub serial: u32,
    pub path: Option<String>,
    pub interface: Option<String>,
    pub member: Option<String>,
    pub error_name: Option<String>,
    pub reply_serial: Option<u32>,
    pub destination: Option<String>,
    pub sender: Option<String>,
    pub signature: String,
    pub values: Vec<Value>,
}

impl Message {
    pub fn method_call(
        destination: &str,
        path: &str,
        interface: &str,
        member: &str,
        values: Vec<Value>,
        signature: &str,
    ) -> Message {
        Message {
            kind: METHOD_CALL,
            flags: 0,
            serial: 0,
            path: Some(path.to_owned()),
            interface: Some(interface.to_owned()),
            member: Some(member.to_owned()),
            error_name: None,
            reply_serial: None,
            destination: Some(destination.to_owned()),
            sender: None,
            signature: signature.to_owned(),
            values,
        }
    }

    pub fn method_reply(
        reply_serial: u32,
        destination: Option<String>,
        values: Vec<Value>,
        signature: &str,
    ) -> Message {
        Message {
            kind: METHOD_RETURN,
            flags: 0,
            serial: 0,
            path: None,
            interface: None,
            member: None,
            error_name: None,
            reply_serial: Some(reply_serial),
            destination,
            sender: None,
            signature: signature.to_owned(),
            values,
        }
    }

    pub fn method_error(
        reply_serial: u32,
        destination: Option<String>,
        error_name: &str,
        error_text: &str,
    ) -> Message {
        Message {
            kind: METHOD_ERROR,
            flags: 0,
            serial: 0,
            path: None,
            interface: None,
            member: None,
            error_name: Some(error_name.to_owned()),
            reply_serial: Some(reply_serial),
            destination,
            sender: None,
            signature: "s".to_owned(),
            values: vec![Value::str(error_text)],
        }
    }

    pub fn signal(
        path: &str,
        interface: &str,
        member: &str,
        values: Vec<Value>,
        signature: &str,
    ) -> Message {
        Message {
            kind: SIGNAL,
            flags: 1, // no reply expected
            serial: 0,
            path: Some(path.to_owned()),
            interface: Some(interface.to_owned()),
            member: Some(member.to_owned()),
            error_name: None,
            reply_serial: None,
            destination: None,
            sender: None,
            signature: signature.to_owned(),
            values,
        }
    }

    /// Human-readable routing identity for logs and tests.
    pub fn describe(&self) -> String {
        match self.kind {
            METHOD_CALL => format!(
                "call {}.{} on {}",
                self.interface.as_deref().unwrap_or("?"),
                self.member.as_deref().unwrap_or("?"),
                self.path.as_deref().unwrap_or("?")
            ),
            METHOD_RETURN => format!("reply to #{}", self.reply_serial.unwrap_or(0)),
            METHOD_ERROR => format!(
                "error {}: {}",
                self.error_name.as_deref().unwrap_or("?"),
                self.values.first().and_then(Value::as_str).unwrap_or("")
            ),
            SIGNAL => format!(
                "signal {}.{} on {}",
                self.interface.as_deref().unwrap_or("?"),
                self.member.as_deref().unwrap_or("?"),
                self.path.as_deref().unwrap_or("?")
            ),
            other => format!("message type {other}"),
        }
    }
}

#[derive(Debug)]
pub struct FormatError(pub String);

impl fmt::Display for FormatError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "dbus format error: {}", self.0)
    }
}

impl std::error::Error for FormatError {}

fn format_error(what: impl fmt::Display) -> FormatError {
    FormatError(what.to_string())
}

fn align(pos: usize, alignment: usize) -> usize {
    (pos + alignment - 1) & !(alignment - 1)
}

/// The value boxed by a variant (identity for non-variants).
fn variant_inner(value: &Value) -> &Value {
    match value {
        Value::Variant(inner) => inner,
        other => other,
    }
}

/// Alignment of a complete type (first type in `sig`).
fn type_alignment(sig: &str) -> usize {
    match sig.as_bytes().first() {
        Some(b'y') | Some(b'v') | Some(b'g') => 1,
        Some(b'n') | Some(b'q') => 2,
        Some(b'b') | Some(b'i') | Some(b'u') | Some(b's') | Some(b'o') => 4,
        Some(b'x') | Some(b't') | Some(b'd') => 8,
        Some(b'(') | Some(b'{') => 8,
        // Per the spec alignment table, an array starts at a 4-byte boundary
        // (its length field); elements are re-aligned afterwards.
        Some(b'a') => 4,
        _ => 1,
    }
}

/// Extracts the first complete type from a signature iterator position.
/// Returns the type text and advances `cursor` past it.
fn next_complete_type(sig: &str, cursor: &mut usize) -> Result<String, FormatError> {
    let bytes = sig.as_bytes();
    let start = *cursor;
    let Some(&first) = bytes.get(*cursor) else {
        return Err(format_error("signature ended unexpectedly"));
    };
    *cursor += 1;
    match first {
        b'y' | b'b' | b'n' | b'q' | b'i' | b'u' | b'x' | b't' | b'd' | b's' | b'o' | b'g'
        | b'v' => Ok(sig[start..*cursor].to_owned()),
        b'a' => {
            next_complete_type(sig, cursor)?;
            Ok(sig[start..*cursor].to_owned())
        }
        b'(' | b'{' => {
            let close = if first == b'(' { b')' } else { b'}' };
            loop {
                let Some(&next) = bytes.get(*cursor) else {
                    return Err(format_error("unterminated container in signature"));
                };
                if next == close {
                    *cursor += 1;
                    break;
                }
                next_complete_type(sig, cursor)?;
            }
            Ok(sig[start..*cursor].to_owned())
        }
        other => Err(format_error(format!(
            "unknown signature byte {:?} in {sig:?}",
            other as char
        ))),
    }
}

/// Verifies that `signature` matches the values about to be marshaled.
fn signature_of(value: &Value) -> String {
    match value {
        Value::Byte(_) => "y".to_owned(),
        Value::Bool(_) => "b".to_owned(),
        Value::U16(_) => "q".to_owned(),
        Value::I16(_) => "n".to_owned(),
        Value::U32(_) => "u".to_owned(),
        Value::I32(_) => "i".to_owned(),
        Value::U64(_) => "t".to_owned(),
        Value::I64(_) => "x".to_owned(),
        Value::Str(_) => "s".to_owned(),
        Value::Path(_) => "o".to_owned(),
        Value::Signature(_) => "g".to_owned(),
        Value::Array { elem_sig, .. } => format!("a{elem_sig}"),
        Value::Struct(fields) => {
            let inner: String = fields.iter().map(signature_of).collect();
            format!("({inner})")
        }
        Value::DictEntry { key, value } => {
            format!("{{{}{}}}", signature_of(key), signature_of(value))
        }
        Value::Variant(_) => "v".to_owned(),
    }
}

/// Verifies that `signature` matches the values about to be marshaled.
/// Recurses into containers so an `a{sv}` element that forgot its `Variant`
/// wrapper is rejected here instead of corrupting the wire stream.
fn check_signature(signature: &str, values: &[Value]) -> Result<(), FormatError> {
    let mut cursor = 0;
    for value in values {
        let expected = next_complete_type(signature, &mut cursor)?;
        check_value_matches(value, &expected)?;
    }
    if cursor != signature.len() {
        return Err(format_error(format!(
            "signature {signature:?} has more types than values"
        )));
    }
    Ok(())
}

fn check_value_matches(value: &Value, expected: &str) -> Result<(), FormatError> {
    let actual = signature_of(value);
    match (value, expected.as_bytes().first()) {
        (Value::Array { elem_sig, items }, Some(b'a')) => {
            for item in items {
                check_value_matches(item, elem_sig)?;
            }
            Ok(())
        }
        (Value::Struct(fields), Some(b'(')) => {
            let inner = &expected[1..expected.len() - 1];
            let mut cursor = 0;
            for field in fields {
                let field_type = next_complete_type(inner, &mut cursor)?;
                check_value_matches(field, &field_type)?;
            }
            if cursor != inner.len() {
                return Err(format_error(format!(
                    "struct {actual:?} does not match signature {expected:?}"
                )));
            }
            Ok(())
        }
        (Value::DictEntry { key, value }, Some(b'{')) => {
            let inner = &expected[1..expected.len() - 1];
            let mut cursor = 0;
            let key_type = next_complete_type(inner, &mut cursor)?;
            check_value_matches(key, &key_type)?;
            let value_type = next_complete_type(inner, &mut cursor)?;
            check_value_matches(value, &value_type)?;
            if cursor != inner.len() {
                return Err(format_error(format!(
                    "dict entry {actual:?} does not match signature {expected:?}"
                )));
            }
            Ok(())
        }
        (Value::Variant(_), Some(b'v')) => {
            // The variant boxes its own typed value; the wire already carries
            // the inner signature, so nothing further to check here.
            Ok(())
        }
        _ => {
            if actual == expected {
                Ok(())
            } else {
                Err(format_error(format!(
                    "value {value:?} does not match signature type {expected:?}"
                )))
            }
        }
    }
}

struct MarshalBuffer {
    bytes: Vec<u8>,
}

impl MarshalBuffer {
    fn new() -> Self {
        Self { bytes: Vec::new() }
    }

    fn pad(&mut self, alignment: usize) {
        while !self.bytes.len().is_multiple_of(alignment) {
            self.bytes.push(0);
        }
    }

    fn push_u8(&mut self, value: u8) {
        self.bytes.push(value);
    }

    fn push_u32(&mut self, value: u32) {
        self.bytes.extend_from_slice(&value.to_le_bytes());
    }

    fn push_u64(&mut self, value: u64) {
        self.bytes.extend_from_slice(&value.to_le_bytes());
    }

    fn write_string(&mut self, value: &str) {
        self.pad(4);
        self.push_u32(value.len() as u32);
        self.bytes.extend_from_slice(value.as_bytes());
        self.bytes.push(0);
    }

    fn write_signature(&mut self, value: &str) {
        self.push_u8(value.len() as u8);
        self.bytes.extend_from_slice(value.as_bytes());
        self.bytes.push(0);
    }

    fn write_value(&mut self, value: &Value) {
        match value {
            Value::Byte(value) => self.push_u8(*value),
            Value::Bool(value) => {
                self.pad(4);
                self.push_u32(u32::from(*value));
            }
            Value::U16(value) => {
                self.pad(2);
                self.bytes.extend_from_slice(&value.to_le_bytes());
            }
            Value::I16(value) => {
                self.pad(2);
                self.bytes.extend_from_slice(&value.to_le_bytes());
            }
            Value::U32(value) => {
                self.pad(4);
                self.push_u32(*value);
            }
            Value::I32(value) => {
                self.pad(4);
                self.push_u32(*value as u32);
            }
            Value::U64(value) => {
                self.pad(8);
                self.push_u64(*value);
            }
            Value::I64(value) => {
                self.pad(8);
                self.push_u64(*value as u64);
            }
            Value::Str(value) | Value::Path(value) => self.write_string(value),
            Value::Signature(value) => self.write_signature(value),
            Value::Array { elem_sig, items } => {
                let alignment = type_alignment(elem_sig).max(1);
                // The length prefix is a UINT32 (4-aligned); the first
                // element is re-aligned to the element type afterwards.
                self.pad(4);
                // Placeholder length, patched after the elements are written.
                let length_position = self.bytes.len();
                self.push_u32(0);
                self.pad(alignment);
                let data_start = self.bytes.len();
                for item in items {
                    self.write_value(item);
                }
                let length = (self.bytes.len() - data_start) as u32;
                self.bytes[length_position..length_position + 4]
                    .copy_from_slice(&length.to_le_bytes());
            }
            Value::Struct(fields) => {
                self.pad(8);
                for field in fields {
                    self.write_value(field);
                }
            }
            Value::DictEntry { key, value } => {
                self.pad(8);
                self.write_value(key);
                self.write_value(value);
            }
            Value::Variant(inner) => {
                self.write_signature(&signature_of(inner));
                self.write_value(inner);
            }
        }
    }
}

/// Serializes a complete message (header + body), little-endian.
pub fn marshal(message: &Message) -> Result<Vec<u8>, FormatError> {
    check_signature(&message.signature, &message.values)?;

    let mut body = MarshalBuffer::new();
    for value in &message.values {
        body.write_value(value);
    }

    let mut fields: Vec<(u8, Value)> = Vec::new();
    let mut push_field = |code: u8, value: Value| fields.push((code, value));
    if let Some(path) = &message.path {
        push_field(FIELD_PATH, Value::Path(path.clone()));
    }
    if let Some(interface) = &message.interface {
        push_field(FIELD_INTERFACE, Value::str(interface.clone()));
    }
    if let Some(member) = &message.member {
        push_field(FIELD_MEMBER, Value::str(member.clone()));
    }
    if let Some(error_name) = &message.error_name {
        push_field(FIELD_ERROR_NAME, Value::str(error_name.clone()));
    }
    if let Some(reply_serial) = message.reply_serial {
        push_field(FIELD_REPLY_SERIAL, Value::U32(reply_serial));
    }
    if let Some(destination) = &message.destination {
        push_field(FIELD_DESTINATION, Value::str(destination.clone()));
    }
    if let Some(sender) = &message.sender {
        push_field(FIELD_SENDER, Value::str(sender.clone()));
    }
    if !message.signature.is_empty() {
        push_field(FIELD_SIGNATURE, Value::Signature(message.signature.clone()));
    }

    let mut fields_buf = MarshalBuffer::new();
    for (code, value) in &fields {
        // Each header field is a (BYTE, VARIANT) STRUCT: structs always
        // start on an 8-byte boundary, even here inside the header.
        fields_buf.pad(8);
        fields_buf.push_u8(*code);
        fields_buf.write_value(&Value::Variant(Box::new(value.clone())));
    }

    let mut out = MarshalBuffer::new();
    out.push_u8(b'l');
    out.push_u8(message.kind);
    out.push_u8(message.flags);
    out.push_u8(1); // protocol version
    out.push_u32(body.bytes.len() as u32);
    out.push_u32(message.serial);
    out.push_u32(fields_buf.bytes.len() as u32);
    out.bytes.extend_from_slice(&fields_buf.bytes);
    while !out.bytes.len().is_multiple_of(8) {
        out.bytes.push(0);
    }
    out.bytes.extend_from_slice(&body.bytes);
    if out.bytes.len() > MAX_MESSAGE_SIZE {
        return Err(format_error("marshaled message exceeds size cap"));
    }
    Ok(out.bytes)
}

/// Incremental parser over a byte buffer.
struct Parser<'a> {
    bytes: &'a [u8],
    pos: usize,
    big_endian: bool,
}

impl<'a> Parser<'a> {
    fn pad(&mut self, alignment: usize) -> Result<(), FormatError> {
        while !self.pos.is_multiple_of(alignment) {
            let Some(&byte) = self.bytes.get(self.pos) else {
                return Err(format_error("truncated padding"));
            };
            if byte != 0 {
                return Err(format_error("non-zero alignment padding"));
            }
            self.pos += 1;
        }
        Ok(())
    }

    fn take(&mut self, count: usize) -> Result<&'a [u8], FormatError> {
        let end = self
            .pos
            .checked_add(count)
            .ok_or_else(|| format_error("length overflow"))?;
        let slice = self
            .bytes
            .get(self.pos..end)
            .ok_or_else(|| format_error("truncated message body"))?;
        self.pos = end;
        Ok(slice)
    }

    fn read_u8(&mut self) -> Result<u8, FormatError> {
        Ok(self.take(1)?[0])
    }

    fn read_u16(&mut self) -> Result<u16, FormatError> {
        let raw = self.take(2)?;
        let array = [raw[0], raw[1]];
        Ok(if self.big_endian {
            u16::from_be_bytes(array)
        } else {
            u16::from_le_bytes(array)
        })
    }

    fn read_u32(&mut self) -> Result<u32, FormatError> {
        let raw = self.take(4)?;
        let array = [raw[0], raw[1], raw[2], raw[3]];
        Ok(if self.big_endian {
            u32::from_be_bytes(array)
        } else {
            u32::from_le_bytes(array)
        })
    }

    fn read_u64(&mut self) -> Result<u64, FormatError> {
        let raw = self.take(8)?;
        let mut array = [0_u8; 8];
        array.copy_from_slice(raw);
        Ok(if self.big_endian {
            u64::from_be_bytes(array)
        } else {
            u64::from_le_bytes(array)
        })
    }

    fn read_string(&mut self) -> Result<String, FormatError> {
        self.pad(4)?;
        let length = self.read_u32()? as usize;
        let bytes = self.take(length)?;
        let terminator = self.read_u8()?;
        if terminator != 0 {
            return Err(format_error("string missing NUL terminator"));
        }
        String::from_utf8(bytes.to_vec()).map_err(|_| format_error("invalid UTF-8 in string"))
    }

    fn read_signature(&mut self) -> Result<String, FormatError> {
        let length = self.read_u8()? as usize;
        let bytes = self.take(length)?;
        let terminator = self.read_u8()?;
        if terminator != 0 {
            return Err(format_error("signature missing NUL terminator"));
        }
        String::from_utf8(bytes.to_vec()).map_err(|_| format_error("invalid UTF-8 in signature"))
    }

    /// Reads one value of the complete type at the current signature cursor.
    fn read_value(&mut self, sig: &str, cursor: &mut usize) -> Result<Value, FormatError> {
        let complete = next_complete_type(sig, cursor)?;
        Ok(match complete.as_bytes()[0] {
            b'y' => Value::Byte(self.read_u8()?),
            b'b' => {
                self.pad(4)?;
                let raw = self.read_u32()?;
                match raw {
                    0 => Value::Bool(false),
                    1 => Value::Bool(true),
                    _ => return Err(format_error("boolean out of range")),
                }
            }
            b'n' => {
                self.pad(2)?;
                Value::I16(self.read_u16()? as i16)
            }
            b'q' => {
                self.pad(2)?;
                Value::U16(self.read_u16()?)
            }
            b'i' => {
                self.pad(4)?;
                Value::I32(self.read_u32()? as i32)
            }
            b'u' => {
                self.pad(4)?;
                Value::U32(self.read_u32()?)
            }
            b'x' => {
                self.pad(8)?;
                Value::I64(self.read_u64()? as i64)
            }
            b't' => {
                self.pad(8)?;
                Value::U64(self.read_u64()?)
            }
            b'd' => {
                self.pad(8)?;
                let bits = self.read_u64()?;
                Value::I64(bits as i64) // doubles unused by the broker; decoded losslessly as bits
            }
            b's' => Value::Str(self.read_string()?),
            b'o' => Value::Path(self.read_string()?),
            b'g' => Value::Signature(self.read_signature()?),
            b'v' => {
                let inner_sig = self.read_signature()?;
                let mut inner_cursor = 0;
                Value::Variant(Box::new(self.read_value(&inner_sig, &mut inner_cursor)?))
            }
            b'a' => {
                let elem_sig = complete[1..].to_owned();
                let alignment = type_alignment(&elem_sig).max(1);
                self.pad(4)?;
                let length = self.read_u32()? as usize;
                self.pad(alignment)?;
                let end = self
                    .pos
                    .checked_add(length)
                    .ok_or_else(|| format_error("array length overflow"))?;
                let array_end = end;
                let mut items = Vec::new();
                while self.pos < array_end {
                    let mut elem_cursor = 0;
                    items.push(self.read_value(&elem_sig, &mut elem_cursor)?);
                    if elem_cursor != elem_sig.len() {
                        return Err(format_error("array element signature mismatch"));
                    }
                }
                if self.pos != array_end {
                    return Err(format_error("array element overruns its length"));
                }
                Value::Array { elem_sig, items }
            }
            b'(' => {
                self.pad(8)?;
                let inner = &complete[1..complete.len() - 1];
                let mut inner_cursor = 0;
                let mut fields = Vec::new();
                while inner_cursor < inner.len() {
                    fields.push(self.read_value(inner, &mut inner_cursor)?);
                }
                Value::Struct(fields)
            }
            b'{' => {
                self.pad(8)?;
                let inner = &complete[1..complete.len() - 1];
                let mut inner_cursor = 0;
                let key = self.read_value(inner, &mut inner_cursor)?;
                let value = self.read_value(inner, &mut inner_cursor)?;
                Value::DictEntry {
                    key: Box::new(key),
                    value: Box::new(value),
                }
            }
            other => {
                return Err(format_error(format!(
                    "unsupported signature byte {other:?} while parsing"
                )));
            }
        })
    }
}

/// Parses one message from `bytes`. Returns the message and the total number
/// of bytes consumed (header + padding + body).
pub fn parse(bytes: &[u8]) -> Result<(Message, usize), FormatError> {
    if bytes.len() < 16 {
        return Err(format_error("message shorter than fixed header"));
    }
    let big_endian = match bytes[0] {
        b'l' => false,
        b'B' => true,
        other => return Err(format_error(format!("bad endianness byte {other:?}"))),
    };
    let kind = bytes[1];
    let flags = bytes[2];
    if bytes[3] != 1 {
        return Err(format_error(format!(
            "unsupported protocol version {}",
            bytes[3]
        )));
    }
    let mut parser = Parser {
        bytes,
        pos: 0,
        big_endian,
    };
    parser.pos = 4;
    let body_len = parser.read_u32()? as usize;
    let serial = parser.read_u32()?;
    let fields_len = parser.read_u32()? as usize;
    if !(METHOD_CALL..=SIGNAL).contains(&kind) {
        return Err(format_error(format!("unknown message type {kind}")));
    }
    let fields_end = 16 + fields_len;
    let body_start = align(fields_end, 8);
    let total = body_start + body_len;
    if bytes.len() < total {
        return Err(format_error(format!(
            "incomplete message: need {total} bytes, have {}",
            bytes.len()
        )));
    }

    // Header fields.
    let mut path = None;
    let mut interface = None;
    let mut member = None;
    let mut error_name = None;
    let mut reply_serial = None;
    let mut destination = None;
    let mut sender = None;
    let mut signature = String::new();
    parser.pos = 16;
    let fields_parser_end = fields_end;
    while parser.pos < fields_parser_end {
        // (BYTE, VARIANT) struct elements always start 8-aligned.
        parser.pad(8)?;
        let code = parser.read_u8()?;
        let value = {
            let mut cursor = 0;
            parser.read_value("v", &mut cursor)?
        };
        match code {
            FIELD_PATH => path = variant_inner(&value).as_str().map(str::to_owned),
            FIELD_INTERFACE => interface = variant_inner(&value).as_str().map(str::to_owned),
            FIELD_MEMBER => member = variant_inner(&value).as_str().map(str::to_owned),
            FIELD_ERROR_NAME => error_name = variant_inner(&value).as_str().map(str::to_owned),
            FIELD_REPLY_SERIAL => reply_serial = variant_inner(&value).as_u32(),
            FIELD_DESTINATION => destination = variant_inner(&value).as_str().map(str::to_owned),
            FIELD_SENDER => sender = variant_inner(&value).as_str().map(str::to_owned),
            FIELD_SIGNATURE => {
                signature = variant_inner(&value)
                    .as_str()
                    .unwrap_or_default()
                    .to_owned()
            }
            _ => {} // unknown fields are skipped per spec
        }
    }

    parser.pos = body_start;
    let mut values = Vec::new();
    let mut signature_cursor = 0;
    while signature_cursor < signature.len() {
        values.push(parser.read_value(&signature, &mut signature_cursor)?);
    }
    if parser.pos != total {
        return Err(format_error(format!(
            "body consumed {} of {total} bytes",
            parser.pos
        )));
    }

    let message = Message {
        kind,
        flags,
        serial,
        path,
        interface,
        member,
        error_name,
        reply_serial,
        destination,
        sender,
        signature,
        values,
    };
    Ok((message, total))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn roundtrip(message: &Message) -> Message {
        let bytes = marshal(message).expect("marshal");
        let (decoded, consumed) = parse(&bytes).expect("parse");
        assert_eq!(consumed, bytes.len());
        decoded
    }

    #[test]
    fn method_call_roundtrip_with_all_header_fields() {
        let message = Message::method_call(
            "org.freedesktop.login1",
            "/org/freedesktop/login1",
            "org.freedesktop.login1.Manager",
            "ListSessions",
            vec![],
            "",
        )
        .clone();
        // The sender is normally set by the daemon; roundtrip it too.
        let mut message = message;
        message.sender = Some(":1.42".to_owned());
        let decoded = roundtrip(&message);
        assert_eq!(decoded.kind, METHOD_CALL);
        assert_eq!(
            decoded.destination.as_deref(),
            Some("org.freedesktop.login1")
        );
        assert_eq!(decoded.sender.as_deref(), Some(":1.42"));
        assert_eq!(decoded.signature, "");
        assert_eq!(decoded.values.len(), 0);
    }

    #[test]
    fn body_roundtrip_covers_all_value_kinds() {
        let values = vec![
            Value::Byte(7),
            Value::Bool(true),
            Value::U16(65500),
            Value::I16(-3),
            Value::U32(4_000_000_000),
            Value::I32(-2),
            Value::U64(u64::from(u32::MAX) + 7),
            Value::I64(-9),
            Value::str("hello wörld"),
            Value::Path("/org/freedesktop/login1/session/_33".to_owned()),
            Value::Signature("a(susso)".to_owned()),
            Value::Variant(Box::new(Value::U32(5))),
            Value::Struct(vec![
                Value::str("id"),
                Value::U32(1000),
                Value::Path("/p".to_owned()),
            ]),
            Value::DictEntry {
                key: Box::new(Value::str("Active")),
                value: Box::new(Value::Variant(Box::new(Value::Bool(true)))),
            },
        ];
        let signature: String = values.iter().map(signature_of).collect();
        let message = Message::method_call("d", "/p", "i.f", "M", values, &signature);
        let decoded = roundtrip(&message);
        assert_eq!(decoded.values, message.values);
    }

    #[test]
    fn empty_dict_marshals_as_zero_length_array() {
        let message = Message::method_call(
            "d",
            "/p",
            "i.f",
            "M",
            vec![Value::empty_dict("{sv}"), Value::empty_dict("{ss}")],
            "a{sv}a{ss}",
        );
        let decoded = roundtrip(&message);
        assert_eq!(decoded.values, message.values);
    }

    #[test]
    fn array_of_structs_alignment_roundtrip() {
        // a(susso) — the real logind ListSessions reply type:
        // (id, uid, name, seat, session object path).
        let value = Value::Array {
            elem_sig: "(susso)".to_owned(),
            items: vec![
                Value::Struct(vec![
                    Value::str("3"),
                    Value::U32(1000),
                    Value::str("bing"),
                    Value::str("seat0"),
                    Value::Path("/org/freedesktop/login1/session/_33".to_owned()),
                ]),
                Value::Struct(vec![
                    Value::str("1"),
                    Value::U32(1000),
                    Value::str("bing"),
                    Value::str(""),
                    Value::Path("/org/freedesktop/login1/session/_31".to_owned()),
                ]),
            ],
        };
        let message = Message::method_reply(9, None, vec![value], "a(susso)");
        let decoded = roundtrip(&message);
        assert_eq!(decoded.values, message.values);
    }

    #[test]
    fn nested_variant_array_alignment_roundtrip() {
        // a{sv} with an array-valued variant exercises deep alignment.
        let value = Value::Array {
            elem_sig: "{sv}".to_owned(),
            items: vec![
                Value::DictEntry {
                    key: Box::new(Value::str("list")),
                    value: Box::new(Value::Variant(Box::new(Value::Array {
                        elem_sig: "s".to_owned(),
                        items: vec![Value::str("a"), Value::str("bb"), Value::str("ccc")],
                    }))),
                },
                Value::DictEntry {
                    key: Box::new(Value::str("count")),
                    value: Box::new(Value::Variant(Box::new(Value::U32(3)))),
                },
            ],
        };
        let message = Message::method_reply(3, None, vec![value], "a{sv}");
        let decoded = roundtrip(&message);
        assert_eq!(decoded.values, message.values);
    }

    #[test]
    fn parses_big_endian_messages() {
        // Hand-build a BE message: a signal with a single string body.
        let body: Vec<u8> = {
            let mut body = Vec::new();
            body.extend_from_slice(&5_u32.to_be_bytes());
            body.extend_from_slice(b"hello");
            body.push(0);
            body
        };
        // a(yv) with MEMBER and SIGNATURE fields; each (yv) struct element
        // starts 8-aligned.
        let mut fields: Vec<u8> = Vec::new();
        let push_field = |bytes: &mut Vec<u8>, code: u8, mut entry: Vec<u8>| {
            while !bytes.len().is_multiple_of(8) {
                bytes.push(0);
            }
            bytes.push(code);
            bytes.append(&mut entry);
        };
        // MEMBER: variant of "s" holding "Tick".
        let mut member = Vec::new();
        member.push(1);
        member.extend_from_slice(b"s\0");
        member.extend_from_slice(&4_u32.to_be_bytes());
        member.extend_from_slice(b"Tick");
        member.push(0);
        push_field(&mut fields, 3, member);
        // SIGNATURE: variant of "g" holding "s" (signature = one-byte length).
        let mut signature = Vec::new();
        signature.push(1);
        signature.extend_from_slice(b"g\0");
        signature.push(1);
        signature.extend_from_slice(b"s\0");
        push_field(&mut fields, 8, signature);
        let mut bytes: Vec<u8> = vec![b'B', SIGNAL, 0, 1];
        bytes.extend_from_slice(&(body.len() as u32).to_be_bytes());
        bytes.extend_from_slice(&77_u32.to_be_bytes());
        bytes.extend_from_slice(&(fields.len() as u32).to_be_bytes());
        bytes.extend_from_slice(&fields);
        while !bytes.len().is_multiple_of(8) {
            bytes.push(0); // pad the header so the body starts 8-aligned
        }
        bytes.extend_from_slice(&body);

        let (message, consumed) = parse(&bytes).expect("parse BE");
        assert_eq!(consumed, bytes.len());
        assert_eq!(message.serial, 77);
        assert_eq!(message.member.as_deref(), Some("Tick"));
        assert_eq!(message.signature, "s");
        assert_eq!(message.values, vec![Value::str("hello".to_owned())]);
    }

    #[test]
    fn marshal_rejects_signature_mismatch() {
        let message = Message::method_call("d", "/p", "i.f", "M", vec![Value::U32(1)], "s");
        assert!(marshal(&message).is_err());
    }

    #[test]
    fn parse_rejects_truncated_messages() {
        let message = Message::method_call("d", "/p", "i.f", "M", vec![Value::str("x")], "s");
        let bytes = marshal(&message).unwrap();
        assert!(parse(&bytes[..bytes.len() - 1]).is_err());
        assert!(parse(&bytes[..10]).is_err());
    }

    #[test]
    fn signature_of_matches_wire_types() {
        assert_eq!(signature_of(&Value::str("x")), "s");
        let array = Value::Array {
            elem_sig: "(su)".to_owned(),
            items: vec![],
        };
        assert_eq!(signature_of(&array), "a(su)");
        let dict = Value::DictEntry {
            key: Box::new(Value::str("k")),
            value: Box::new(Value::Variant(Box::new(Value::U32(1)))),
        };
        assert_eq!(signature_of(&dict), "{sv}");
    }

    #[test]
    fn next_complete_type_covers_containers() {
        let mut cursor = 0;
        assert_eq!(
            next_complete_type("a(susso)s", &mut cursor).unwrap(),
            "a(susso)"
        );
        assert_eq!(cursor, 8);
        assert_eq!(next_complete_type("a(susso)s", &mut cursor).unwrap(), "s");
        assert!(next_complete_type("(su", &mut cursor).is_err());
    }
}
