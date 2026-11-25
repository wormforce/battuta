//! Minimal D-Bus client connection: unix-socket transport, SASL EXTERNAL
//! auth, Hello, method calls with reply routing, match rules, and a signal
//! inbox.
//!
//! Hand-rolled on purpose: the broker's D-Bus surface is a fixed handful of
//! logind/polkit/daemon calls, and pulling a full D-Bus runtime into a
//! hardened system daemon buys nothing the 300 lines below do not. The
//! implementation is validated against real system services (logind, polkit)
//! in the integration tests, and against GIO — an independent D-Bus stack —
//! in the mock-bus tests.

use super::message::{
    FormatError, MAX_MESSAGE_SIZE, METHOD_ERROR, METHOD_RETURN, Message, Value, marshal, parse,
};
use std::collections::{HashMap, VecDeque};
use std::os::fd::{AsFd, AsRawFd, BorrowedFd, FromRawFd, OwnedFd, RawFd};
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::sync::{Arc, Condvar, Mutex, mpsc};
use std::time::Duration;

/// Default system bus socket, used when `DBUS_SYSTEM_BUS_ADDRESS` is unset.
pub const DEFAULT_SYSTEM_BUS_ADDRESS: &str = "unix:path=/var/run/dbus/system_bus_socket";

#[derive(Debug)]
pub enum CallError {
    /// No reply within the deadline.
    Timeout,
    /// The connection dropped while waiting.
    Closed,
    /// The peer answered with a D-Bus error.
    DBus { name: String, text: String },
    /// The reply could not be decoded.
    Format(String),
}

impl std::fmt::Display for CallError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            CallError::Timeout => write!(f, "dbus call timed out"),
            CallError::Closed => write!(f, "dbus connection closed"),
            CallError::DBus { name, text } => write!(f, "dbus error {name}: {text}"),
            CallError::Format(text) => write!(f, "dbus reply malformed: {text}"),
        }
    }
}

/// The bus-assigned reply channel for one in-flight method call.
type ReplySender = mpsc::SyncSender<Result<Vec<Value>, CallError>>;

struct Inner {
    fd: OwnedFd,
    /// Serializes writes (auth bytes, marshaled frames) on the socket.
    write_lock: Mutex<()>,
    serial: AtomicU32,
    closed: AtomicBool,
    unique_name: Mutex<Option<String>>,
    /// reply_serial → one-shot sender for the waiting `call`.
    pending: Mutex<HashMap<u32, ReplySender>>,
    inbox: Mutex<VecDeque<Message>>,
    inbox_signal: Condvar,
}

/// A client connection to a D-Bus bus. Cloneable; the clone shares the
/// socket, reader thread, and routing state.
#[derive(Clone)]
pub struct Connection {
    inner: Arc<Inner>,
}

#[derive(Debug)]
pub enum TransportAddress {
    Path(std::path::PathBuf),
    Abstract(Vec<u8>),
}

/// Picks the first usable address from a D-Bus address list and resolves
/// `unix:runtime=yes` against `XDG_RUNTIME_DIR`.
pub fn parse_address_list(list: &str) -> Result<TransportAddress, String> {
    let entries = list.split(';').filter(|entry| !entry.trim().is_empty());
    let mut last_error = "empty address list".to_owned();
    for entry in entries {
        match parse_one_address(entry) {
            Ok(address) => return Ok(address),
            Err(error) => last_error = error,
        }
    }
    Err(last_error)
}

fn parse_one_address(entry: &str) -> Result<TransportAddress, String> {
    let rest = entry
        .strip_prefix("unix:")
        .ok_or_else(|| format!("unsupported address family in {entry:?}"))?;
    for pair in rest.split(',') {
        let Some((key, value)) = pair.split_once('=') else {
            continue;
        };
        match key {
            "path" => return Ok(TransportAddress::Path(std::path::PathBuf::from(value))),
            "abstract" => return Ok(TransportAddress::Abstract(value.as_bytes().to_vec())),
            "runtime" if value == "yes" => {
                let dir = std::env::var("XDG_RUNTIME_DIR")
                    .map_err(|_| "unix:runtime=yes but XDG_RUNTIME_DIR is unset".to_owned())?;
                return Ok(TransportAddress::Path(
                    std::path::PathBuf::from(dir).join("bus"),
                ));
            }
            _ => {}
        }
    }
    Err(format!("no usable transport in {entry:?}"))
}

/// The effective system bus address: env override first, then the default
/// socket path.
pub fn system_bus_address() -> String {
    std::env::var("DBUS_SYSTEM_BUS_ADDRESS")
        .unwrap_or_else(|_| DEFAULT_SYSTEM_BUS_ADDRESS.to_owned())
}

/// Where a method call is aimed.
#[derive(Clone, Copy, Debug)]
pub struct Endpoint<'a> {
    pub destination: &'a str,
    pub path: &'a str,
    pub interface: &'a str,
    pub member: &'a str,
}

impl Connection {
    /// Connects, authenticates (SASL EXTERNAL), and says Hello.
    pub fn connect(address: &str) -> Result<Connection, String> {
        let transport = parse_address_list(address)?;
        let fd = connect_transport(&transport)
            .map_err(|error| format!("connect to {address}: {error}"))?;
        let connection = Self {
            inner: Arc::new(Inner {
                fd,
                write_lock: Mutex::new(()),
                serial: AtomicU32::new(1),
                closed: AtomicBool::new(false),
                unique_name: Mutex::new(None),
                pending: Mutex::new(HashMap::new()),
                inbox: Mutex::new(VecDeque::new()),
                inbox_signal: Condvar::new(),
            }),
        };
        connection.authenticate()?;
        // The reader thread must be live before the first call: it is what
        // routes the Hello reply (and every later reply/signal) to waiters.
        connection.spawn_reader();
        let name = connection
            .call(
                Endpoint {
                    destination: "org.freedesktop.DBus",
                    path: "/org/freedesktop/DBus",
                    interface: "org.freedesktop.DBus",
                    member: "Hello",
                },
                Vec::new(),
                "",
                Duration::from_secs(15),
            )
            .map_err(|error| format!("Hello failed: {error}"))?;
        let unique = name
            .first()
            .and_then(Value::as_str)
            .ok_or_else(|| "Hello returned no unique name".to_owned())?
            .to_owned();
        *connection
            .inner
            .unique_name
            .lock()
            .expect("unique name mutex poisoned") = Some(unique);
        Ok(connection)
    }

    /// Connects to the system bus, honoring `DBUS_SYSTEM_BUS_ADDRESS`.
    pub fn system() -> Result<Connection, String> {
        Self::connect(&system_bus_address())
    }

    /// The bus-assigned unique name (Hello must have completed).
    pub fn unique_name(&self) -> Option<String> {
        self.inner
            .unique_name
            .lock()
            .expect("unique name mutex poisoned")
            .clone()
    }

    pub fn is_closed(&self) -> bool {
        self.inner.closed.load(Ordering::SeqCst)
    }

    /// Suggests the socket be torn down; pending calls fail with
    /// [`CallError::Closed`].
    pub fn close(&self) {
        self.inner.closed.store(true, Ordering::SeqCst);
        let fd = self.inner.fd.as_raw_fd();
        // SAFETY: fd is a valid, open socket owned by this connection.
        unsafe {
            libc::shutdown(fd, libc::SHUT_RDWR);
        }
        self.inner.inbox_signal.notify_all();
    }

    fn authenticate(&self) -> Result<(), String> {
        let uid = unsafe { libc::geteuid() };
        let uid_hex: String = uid
            .to_string()
            .as_bytes()
            .iter()
            .map(|byte| format!("{byte:02X}"))
            .collect();
        let _guard = self.inner.write_lock.lock().expect("write lock poisoned");
        write_all(self.inner.fd.as_raw_fd(), b"\0").map_err(|error| format!("auth: {error}"))?;
        write_all(
            self.inner.fd.as_raw_fd(),
            format!("AUTH EXTERNAL {uid_hex}\r\n").as_bytes(),
        )
        .map_err(|error| format!("auth: {error}"))?;
        let line =
            read_line(self.inner.fd.as_raw_fd()).map_err(|error| format!("auth: {error}"))?;
        if !line.starts_with("OK ") {
            return Err(format!("auth rejected: {line:?}"));
        }
        write_all(self.inner.fd.as_raw_fd(), b"BEGIN\r\n")
            .map_err(|error| format!("auth: {error}"))?;
        Ok(())
    }

    fn spawn_reader(&self) {
        let inner = Arc::clone(&self.inner);
        std::thread::Builder::new()
            .name("dbus-reader".to_owned())
            .spawn(move || reader_loop(inner))
            .expect("spawn dbus reader thread");
    }

    fn next_serial(&self) -> u32 {
        self.inner.serial.fetch_add(1, Ordering::SeqCst)
    }

    /// Sends a message, assigning a serial. Used by mock services for
    /// replies and signals.
    pub fn send(&self, message: &mut Message) -> Result<(), String> {
        if message.serial == 0 {
            message.serial = self.next_serial();
        }
        let bytes = marshal(message).map_err(|error: FormatError| error.to_string())?;
        self.write_frame(&bytes)
    }

    fn write_frame(&self, bytes: &[u8]) -> Result<(), String> {
        let _guard = self.inner.write_lock.lock().expect("write lock poisoned");
        write_all(self.inner.fd.as_raw_fd(), bytes).map_err(|error| error.to_string())
    }

    /// Performs a method call and waits for the matching reply.
    pub fn call(
        &self,
        endpoint: Endpoint<'_>,
        values: Vec<Value>,
        signature: &str,
        timeout: Duration,
    ) -> Result<Vec<Value>, CallError> {
        let mut message = Message::method_call(
            endpoint.destination,
            endpoint.path,
            endpoint.interface,
            endpoint.member,
            values,
            signature,
        );
        let serial = self.next_serial();
        message.serial = serial;
        let bytes = marshal(&message).map_err(|error| CallError::Format(error.to_string()))?;

        let (tx, rx) = mpsc::sync_channel(1);
        self.inner
            .pending
            .lock()
            .expect("pending mutex poisoned")
            .insert(serial, tx);

        if self.write_frame(&bytes).is_err() {
            self.inner
                .pending
                .lock()
                .expect("pending mutex poisoned")
                .remove(&serial);
            return Err(CallError::Closed);
        }

        match rx.recv_timeout(timeout) {
            Ok(result) => result,
            Err(mpsc::RecvTimeoutError::Timeout) => {
                self.inner
                    .pending
                    .lock()
                    .expect("pending mutex poisoned")
                    .remove(&serial);
                Err(CallError::Timeout)
            }
            Err(mpsc::RecvTimeoutError::Disconnected) => Err(CallError::Closed),
        }
    }

    /// Registers a match rule (`org.freedesktop.DBus.AddMatch`).
    pub fn add_match(&self, rule: &str) -> Result<(), CallError> {
        self.call(
            Endpoint {
                destination: "org.freedesktop.DBus",
                path: "/org/freedesktop/DBus",
                interface: "org.freedesktop.DBus",
                member: "AddMatch",
            },
            vec![Value::str(rule)],
            "s",
            Duration::from_secs(15),
        )
        .map(|_| ())
    }

    /// Requests a well-known bus name with `DO_NOT_QUEUE` (flag 4): the call
    /// never queues behind an existing owner. Returns the raw reply flags —
    /// 1 (`PRIMARY_OWNER`) and 4 (`ALREADY_OWNER`) mean success, 3
    /// (`EXISTS`) means another connection owns the name (plan §5.2: the
    /// broker claims `io.github.wormforce.Battuta.Input1`).
    pub fn request_name(&self, name: &str, timeout: Duration) -> Result<u32, CallError> {
        let reply = self.call(
            Endpoint {
                destination: "org.freedesktop.DBus",
                path: "/org/freedesktop/DBus",
                interface: "org.freedesktop.DBus",
                member: "RequestName",
            },
            vec![Value::str(name), Value::U32(4)],
            "su",
            timeout,
        )?;
        match reply.into_iter().next() {
            Some(Value::U32(flags)) => Ok(flags),
            Some(other) => Err(CallError::Format(format!(
                "unexpected RequestName reply: {other:?}"
            ))),
            None => Err(CallError::Format(
                "RequestName reply carried no value".to_owned(),
            )),
        }
    }

    /// Pops the next non-reply message (signals, method calls aimed at us,
    /// unrouteable errors), waiting up to `timeout`.
    pub fn next_message(&self, timeout: Duration) -> Option<Message> {
        let mut inbox = self.inner.inbox.lock().expect("inbox mutex poisoned");
        if let Some(message) = inbox.pop_front() {
            return Some(message);
        }
        if self.inner.closed.load(Ordering::SeqCst) {
            return None;
        }
        let (mut inbox, _wait) = self
            .inner
            .inbox_signal
            .wait_timeout_while(inbox, timeout, |queue: &mut VecDeque<Message>| {
                queue.is_empty() && !self.inner.closed.load(Ordering::SeqCst)
            })
            .expect("inbox condvar poisoned");
        inbox.pop_front()
    }
}

fn connect_transport(address: &TransportAddress) -> std::io::Result<OwnedFd> {
    let fd = unsafe { libc::socket(libc::AF_UNIX, libc::SOCK_STREAM | libc::SOCK_CLOEXEC, 0) };
    if fd < 0 {
        return Err(std::io::Error::last_os_error());
    }
    let owned = unsafe { OwnedFd::from_raw_fd(fd) };
    let (sockaddr, length) = match address {
        TransportAddress::Path(path) => {
            let mut sockaddr = libc::sockaddr_un {
                sun_family: libc::AF_UNIX as u16,
                sun_path: [0; 108],
            };
            let path_bytes = path
                .as_os_str()
                .as_encoded_bytes()
                .iter()
                .copied()
                .chain(std::iter::once(0))
                .collect::<Vec<u8>>();
            if path_bytes.len() > sockaddr.sun_path.len() {
                return Err(std::io::Error::new(
                    std::io::ErrorKind::InvalidInput,
                    "socket path too long",
                ));
            }
            for (index, byte) in path_bytes.iter().enumerate() {
                sockaddr.sun_path[index] = *byte as libc::c_char;
            }
            let length = std::mem::offset_of!(libc::sockaddr_un, sun_path) + path_bytes.len() - 1;
            (sockaddr, length)
        }
        TransportAddress::Abstract(name) => {
            let mut sockaddr = libc::sockaddr_un {
                sun_family: libc::AF_UNIX as u16,
                sun_path: [0; 108],
            };
            if name.len() + 1 > sockaddr.sun_path.len() {
                return Err(std::io::Error::new(
                    std::io::ErrorKind::InvalidInput,
                    "abstract address too long",
                ));
            }
            for (index, byte) in name.iter().enumerate() {
                sockaddr.sun_path[index + 1] = *byte as libc::c_char;
            }
            let length = std::mem::offset_of!(libc::sockaddr_un, sun_path) + 1 + name.len();
            (sockaddr, length)
        }
    };
    let result = unsafe {
        libc::connect(
            owned.as_raw_fd(),
            &sockaddr as *const libc::sockaddr_un as *const libc::sockaddr,
            length as libc::socklen_t,
        )
    };
    if result < 0 {
        return Err(std::io::Error::last_os_error());
    }
    Ok(owned)
}

fn write_all(fd: RawFd, mut bytes: &[u8]) -> std::io::Result<()> {
    while !bytes.is_empty() {
        let written = unsafe { libc::write(fd, bytes.as_ptr().cast(), bytes.len()) };
        if written < 0 {
            let error = std::io::Error::last_os_error();
            if error.kind() == std::io::ErrorKind::Interrupted {
                continue;
            }
            return Err(error);
        }
        bytes = &bytes[written as usize..];
    }
    Ok(())
}

/// Reads a SASL command line (terminated by `\r\n` or `\n`), byte by byte.
fn read_line(fd: RawFd) -> std::io::Result<String> {
    let mut line = Vec::new();
    loop {
        let mut byte = [0_u8; 1];
        let count = unsafe { libc::read(fd, byte.as_mut_ptr().cast(), 1) };
        if count < 0 {
            let error = std::io::Error::last_os_error();
            if error.kind() == std::io::ErrorKind::Interrupted {
                continue;
            }
            return Err(error);
        }
        if count == 0 {
            break;
        }
        if byte[0] == b'\n' {
            break;
        }
        if byte[0] != b'\r' {
            line.push(byte[0]);
        }
        if line.len() > 512 {
            return Err(std::io::Error::new(
                std::io::ErrorKind::InvalidData,
                "auth line too long",
            ));
        }
    }
    String::from_utf8(line)
        .map_err(|_| std::io::Error::new(std::io::ErrorKind::InvalidData, "auth line not UTF-8"))
}

/// Blocks on the socket until EOF/error or `closed` is set.
fn read_exact(fd: BorrowedFd<'_>, buffer: &mut [u8]) -> std::io::Result<()> {
    let mut filled = 0;
    while filled < buffer.len() {
        let count = unsafe {
            libc::read(
                fd.as_raw_fd(),
                buffer[filled..].as_mut_ptr().cast(),
                buffer[filled..].len(),
            )
        };
        if count < 0 {
            let error = std::io::Error::last_os_error();
            if error.kind() == std::io::ErrorKind::Interrupted {
                continue;
            }
            return Err(error);
        }
        if count == 0 {
            return Err(std::io::Error::new(
                std::io::ErrorKind::UnexpectedEof,
                "bus closed connection",
            ));
        }
        filled += count as usize;
    }
    Ok(())
}

fn reader_loop(inner: Arc<Inner>) {
    let close_result: Result<(), std::io::Error> = (|| {
        loop {
            if inner.closed.load(Ordering::SeqCst) {
                return Ok(());
            }
            let mut header = [0_u8; 16];
            read_exact(inner.fd.as_fd(), &mut header)?;
            let big_endian = match header[0] {
                b'l' => false,
                b'B' => true,
                other => {
                    return Err(std::io::Error::new(
                        std::io::ErrorKind::InvalidData,
                        format!("bad endianness byte {other:?}"),
                    ));
                }
            };
            let read_u32 = |offset: usize| -> u32 {
                let raw = [
                    header[offset],
                    header[offset + 1],
                    header[offset + 2],
                    header[offset + 3],
                ];
                if big_endian {
                    u32::from_be_bytes(raw)
                } else {
                    u32::from_le_bytes(raw)
                }
            };
            let body_len = read_u32(4) as usize;
            let fields_len = read_u32(12) as usize;
            if body_len > MAX_MESSAGE_SIZE || fields_len > MAX_MESSAGE_SIZE {
                return Err(std::io::Error::new(
                    std::io::ErrorKind::InvalidData,
                    "message exceeds size cap",
                ));
            }
            let total = ((16 + fields_len + 7) & !7) + body_len;
            let mut bytes = vec![0_u8; total];
            bytes[..16].copy_from_slice(&header);
            read_exact(inner.fd.as_fd(), &mut bytes[16..])?;

            let (message, consumed) = match parse(&bytes) {
                Ok(result) => result,
                Err(error) => {
                    return Err(std::io::Error::new(
                        std::io::ErrorKind::InvalidData,
                        error.to_string(),
                    ));
                }
            };
            debug_assert_eq!(consumed, total);

            // Route replies to waiting callers; everything else goes to the
            // inbox (signals, incoming method calls, stray errors).
            if matches!(message.kind, METHOD_RETURN | METHOD_ERROR)
                && let Some(reply_serial) = message.reply_serial
            {
                let waiter = inner
                    .pending
                    .lock()
                    .expect("pending mutex poisoned")
                    .remove(&reply_serial);
                if let Some(waiter) = waiter {
                    let outcome: Result<Vec<Value>, CallError> = if message.kind == METHOD_ERROR {
                        Err(CallError::DBus {
                            name: message.error_name.clone().unwrap_or_default(),
                            text: message
                                .values
                                .first()
                                .and_then(Value::as_str)
                                .unwrap_or_default()
                                .to_owned(),
                        })
                    } else {
                        // A malformed reply signature fails the *parser*, so
                        // reaching here means the values are present.
                        Ok(message.values.clone())
                    };
                    let _ = waiter.send(outcome);
                    continue;
                }
            }
            let mut inbox = inner.inbox.lock().expect("inbox mutex poisoned");
            inbox.push_back(message);
            drop(inbox);
            inner.inbox_signal.notify_all();
        }
    })();

    // Tear down: fail every pending call and wake the inbox waiters.
    inner.closed.store(true, Ordering::SeqCst);
    if let Err(error) = close_result {
        eprintln!("battuta-inputd: dbus reader stopped: {error}");
    }
    let pending: HashMap<u32, ReplySender> =
        std::mem::take(&mut *inner.pending.lock().expect("pending mutex poisoned"));
    for (_serial, waiter) in pending {
        let _ = waiter.send(Err(CallError::Closed));
    }
    inner.inbox_signal.notify_all();
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_address_lists() {
        let address =
            parse_address_list("unix:path=/run/dbus/system_bus_socket").expect("path address");
        assert!(matches!(address, TransportAddress::Path(_)));

        let address = parse_address_list("unix:abstract=/org/battuta/test").expect("abstract");
        assert!(matches!(address, TransportAddress::Abstract(_)));

        let address = parse_address_list("unix:garbage;unix:path=/tmp/x").expect("fallback");
        assert!(matches!(address, TransportAddress::Path(_)));

        assert!(parse_address_list("tcp:host=localhost,port=1").is_err());
        assert!(parse_address_list("unix:runtime=nope").is_err());
    }

    #[test]
    fn env_override_shapes_system_address() {
        // No env manipulation here (tests run in parallel); just check the
        // default fallback text.
        let address = system_bus_address();
        assert!(address.starts_with("unix:"));
    }
}
