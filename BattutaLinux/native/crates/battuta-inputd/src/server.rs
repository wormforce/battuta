//! Broker server: accept loop, authorization gate, hello/hello_ack handshake,
//! single-active-subscriber enforcement, and two event source modes — the
//! synthetic verification script and the real evdev monitor (PR 5). PR 5b
//! adds the real logind/polkit authorizer, the lock/sleep gate, and the
//! socket permission model.

use crate::auth::{AuthError, Authorizer, DevAuthorizer, RejectAll, SystemAuthorizer};
use crate::dbus::Connection;
use crate::evdev;
use crate::gate::{GateContributor, GateCoordinator};
use crate::hub::{Hub, SourceEvent};
use crate::logind;
use crate::perms;
use crate::seqpacket::{SeqPacketListener, SeqPacketStream};
use crate::session::{self, VtGateConfig};
use crate::synthetic::{SyntheticConfig, script};
use battuta_input_protocol as proto;
use std::io::ErrorKind;
use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

#[derive(Clone, Debug)]
pub struct ServerConfig {
    pub socket_path: PathBuf,
    /// File mode applied to the socket node after bind (default 0660).
    pub socket_mode: u32,
    /// Group (name or GID) the socket node is chowned to. None keeps the
    /// broker process's primary group.
    pub socket_group: Option<String>,
    /// When set, the broker serves a deterministic synthetic script instead
    /// of real input. This is the PR 4 validation mode.
    pub synthetic: Option<SyntheticConfig>,
    /// Dev/test authorization bypass. Never enable in packaged builds.
    pub dev_auth: bool,
    /// System bus address override (tests, nonstandard deployments).
    pub system_bus_address: Option<String>,
    /// Lock-screen/sleep gate via logind signals (default on).
    pub lock_gate: bool,
    /// Heartbeat interval offered in hello_ack; 0 disables heartbeats.
    pub heartbeat_interval_ms: u32,
    /// Seat name reported in hello_ack.
    pub seat: String,
    /// Handshake deadline for clients.
    pub handshake_timeout: Duration,
    /// Directory scanned for evdev devices (real mode). Defaults to
    /// `/dev/input`; injectable for tests.
    pub devices_dir: PathBuf,
    /// Session gate configuration (real mode).
    pub vt_gate: VtGateConfig,
    /// Claim `io.github.wormforce.Battuta.Input1` on the system bus at
    /// startup (default on). Test/dev runs may disable it.
    pub claim_bus_name: bool,
}

impl Default for ServerConfig {
    fn default() -> Self {
        Self {
            socket_path: PathBuf::from("/run/battuta-inputd/input.sock"),
            socket_mode: 0o660,
            socket_group: None,
            synthetic: None,
            dev_auth: false,
            system_bus_address: None,
            lock_gate: true,
            heartbeat_interval_ms: 0,
            seat: "seat0".to_owned(),
            handshake_timeout: Duration::from_secs(5),
            devices_dir: PathBuf::from("/dev/input"),
            vt_gate: VtGateConfig {
                enabled: true,
                active_vt_path: PathBuf::from(session::ACTIVE_VT_PATH),
                graphical_vtnr: 1,
                sessions_dir: PathBuf::from(session::SESSIONS_DIR),
            },
            claim_bus_name: true,
        }
    }
}

/// Monotonic nanoseconds from `CLOCK_MONOTONIC`.
pub fn monotonic_ns() -> u64 {
    let mut ts = libc::timespec {
        tv_sec: 0,
        tv_nsec: 0,
    };
    // SAFETY: ts is a valid out-pointer for a single timespec.
    unsafe {
        libc::clock_gettime(libc::CLOCK_MONOTONIC, &mut ts);
    }
    (ts.tv_sec as u64) * 1_000_000_000 + (ts.tv_nsec as u64)
}

/// Runs the broker until the process is terminated. Blocks the caller.
pub fn run(config: ServerConfig) -> std::io::Result<()> {
    // Authorization: dev bypass, or the real logind+polkit authorizer on the
    // system bus. When the bus is unreachable the broker still serves —
    // through a reject-all authorizer, so failure closes rather than opens.
    let (authorizer, bus): (Arc<dyn Authorizer>, Option<Connection>) = if config.dev_auth {
        let bus = match &config.system_bus_address {
            Some(address) => match Connection::connect(address) {
                Ok(connection) => Some(connection),
                Err(error) => {
                    eprintln!(
                        "battuta-inputd: dev-mode bus connect failed, \
                         lock/sleep gate disabled: {error}"
                    );
                    None
                }
            },
            None => None,
        };
        (Arc::new(DevAuthorizer), bus)
    } else {
        match SystemAuthorizer::connect_with_connection(config.system_bus_address.as_deref()) {
            Ok((authorizer, connection)) => (Arc::new(authorizer), Some(connection)),
            Err(error) => {
                eprintln!(
                    "battuta-inputd: authorization infrastructure unavailable, \
                     rejecting all peers: {error}"
                );
                let reason = format!("system authorization unavailable: {error}");
                (Arc::new(RejectAll { reason }), None)
            }
        }
    };

    // Plan §5.2: claim the well-known bus name so clients and tooling can
    // detect the running broker. A name held by another connection is fatal:
    // two brokers on one bus must never race for subscribers.
    if config.claim_bus_name {
        if let Some(connection) = &bus {
            match connection.request_name(crate::dbus::BROKER_BUS_NAME, Duration::from_secs(5)) {
                Ok(flags @ (1 | 4)) => {
                    eprintln!(
                        "battuta-inputd: claimed {0} on the system bus (flags {flags})",
                        crate::dbus::BROKER_BUS_NAME
                    );
                }
                Ok(flags) => {
                    return Err(std::io::Error::other(format!(
                        "the D-Bus name {} is owned by another process (RequestName flags {flags}); \
                         refusing to start a second broker",
                        crate::dbus::BROKER_BUS_NAME
                    )));
                }
                Err(error) => {
                    let message = format!(
                        "RequestName failed for {0}: {error:?}",
                        crate::dbus::BROKER_BUS_NAME
                    );
                    if config.dev_auth {
                        eprintln!("battuta-inputd: {message} (continuing in dev mode)");
                    } else {
                        return Err(std::io::Error::other(message));
                    }
                }
            }
        } else {
            eprintln!(
                "battuta-inputd: no system bus; {0} not claimed",
                crate::dbus::BROKER_BUS_NAME
            );
        }
    }

    // Real mode: one hub shared by the evdev monitor and the subscriber pump.
    // The hub starts open; the VT gate thread (when enabled) makes its first
    // synchronous decision below and closes the gate if needed, which queues
    // a Reset(SessionPause) for the first subscriber.
    let hub: Option<Arc<Hub>> = if config.synthetic.is_some() {
        None
    } else {
        let hub = Arc::new(Hub::new(true));
        let coordinator = Arc::new(GateCoordinator::new(Arc::clone(&hub)));
        if config.vt_gate.enabled {
            let initial_open = session::read_active_vtnr(&config.vt_gate.active_vt_path)
                == Some(config.vt_gate.graphical_vtnr);
            coordinator.set(GateContributor::Vt, initial_open);
        }
        evdev::spawn(Arc::clone(&hub), config.devices_dir.clone())?;
        session::spawn_monitor(Arc::clone(&coordinator), config.vt_gate.clone())?;
        if config.lock_gate {
            match &bus {
                Some(connection) => {
                    logind::spawn_lifecycle_monitor(connection.clone(), coordinator)
                        .map_err(std::io::Error::other)?
                }
                None if config.dev_auth => {
                    eprintln!("battuta-inputd: lock/sleep gate disabled (dev-auth without a bus)");
                }
                None => {
                    eprintln!(
                        "battuta-inputd: lock/sleep gate disabled (authorization unavailable)"
                    );
                }
            }
        }
        Some(hub)
    };

    // Packaged deployments start the broker through systemd socket
    // activation: the pre-bound listener arrives as fd 3 and the socket
    // node's mode/owner are systemd's business (SocketMode=), so the bind
    // and permission steps are skipped. Manual runs bind as before.
    let listener = match crate::activation::acquire()? {
        crate::activation::Activation::Listener(fd) => {
            let listener = SeqPacketListener::from_owned_fd(fd)?;
            eprintln!("battuta-inputd: using systemd socket activation listener");
            listener
        }
        crate::activation::Activation::None => {
            let listener = SeqPacketListener::bind(&config.socket_path)?;
            perms::apply(
                &config.socket_path,
                config.socket_mode,
                config.socket_group.as_deref(),
            )?;
            listener
        }
    };
    eprintln!(
        "battuta-inputd: listening on {} (auth: {}, source: {})",
        config.socket_path.display(),
        authorizer.describe(),
        match (&config.synthetic, &hub) {
            (Some(_), _) => "synthetic".to_owned(),
            (None, Some(_)) => format!(
                "real evdev ({}, vt-gate: {})",
                config.devices_dir.display(),
                if config.vt_gate.enabled { "on" } else { "off" }
            ),
            (None, None) => "real evdev (hub setup failed)".to_owned(),
        }
    );

    let slot = Arc::new(AtomicBool::new(false));
    let config = Arc::new(config);

    loop {
        let stream = listener.accept()?;
        let authorizer = Arc::clone(&authorizer);
        let slot = Arc::clone(&slot);
        let config = Arc::clone(&config);
        let hub = hub.clone();
        std::thread::spawn(move || {
            if let Err(error) = serve_connection(stream, authorizer, slot, config, hub) {
                eprintln!("battuta-inputd: connection ended: {error}");
            }
        });
    }
}

/// Releases the single-subscriber slot on drop.
struct SubscriberGuard(Arc<AtomicBool>);

impl Drop for SubscriberGuard {
    fn drop(&mut self) {
        self.0.store(false, Ordering::SeqCst);
    }
}

fn send_message(stream: &SeqPacketStream, message: &proto::Message) -> std::io::Result<()> {
    let frame = proto::encode_frame(message)
        .map_err(|error| std::io::Error::new(ErrorKind::InvalidData, error.to_string()))?;
    stream.send(&frame)?;
    Ok(())
}

fn send_error_and_close(stream: &SeqPacketStream, code: proto::ErrorCode, message: &str) {
    // The protocol caps diagnostic strings; truncate on a char boundary so a
    // long reason can never silently drop the whole error frame.
    let mut message = message;
    while message.len() > proto::MAX_DIAGNOSTIC_MESSAGE_LENGTH {
        message = &message[..message.floor_char_boundary(message.len() - 1)];
    }
    let frame = proto::encode_frame(&proto::Message::Error {
        code,
        message: message.to_owned(),
    });
    match frame {
        Ok(frame) => {
            if let Err(error) = stream.send(&frame) {
                eprintln!("battuta-inputd: failed to send error frame: {error}");
            }
        }
        Err(error) => {
            eprintln!("battuta-inputd: failed to encode error frame: {error}");
        }
    }
    drain_then_close(stream);
}

/// Closing a socket that still holds unread frames turns the peer's queued
/// reads into ECONNRESET, swallowing the error frame we just sent. Drain our
/// receive queue first so the peer sees the error and then a clean EOF.
fn drain_then_close(stream: &SeqPacketStream) {
    let _ = stream.set_recv_timeout(Some(Duration::from_millis(50)));
    let mut buffer = [0_u8; proto::MAX_FRAME_LENGTH];
    loop {
        match stream.recv(&mut buffer) {
            Ok(count) if count > 0 => continue,
            _ => return,
        }
    }
}

fn serve_connection(
    stream: SeqPacketStream,
    authorizer: Arc<dyn Authorizer>,
    slot: Arc<AtomicBool>,
    config: Arc<ServerConfig>,
    hub: Option<Arc<Hub>>,
) -> Result<(), String> {
    // 1. Authorization gate: SO_PEERCRED + logind + polkit, re-run on every
    //    (re)connection.
    let peer = stream
        .peer_credentials()
        .map_err(|error| format!("SO_PEERCRED failed: {error}"))?;
    if let Err(auth_error) = authorizer.authorize(&peer) {
        let (code, text) = match &auth_error {
            AuthError::Unavailable(reason) => (
                proto::ErrorCode::Unauthorized,
                format!("broker authorization unavailable: {reason}"),
            ),
            AuthError::Rejected(reason) => (proto::ErrorCode::Unauthorized, reason.clone()),
        };
        eprintln!(
            "battuta-inputd: rejecting pid {} (uid {}): {auth_error}",
            peer.pid, peer.uid
        );
        send_error_and_close(&stream, code, &text);
        return Ok(());
    }

    // 2. Single active subscriber. The slot is taken before the handshake so
    //    two concurrent connections cannot both negotiate successfully.
    if slot.swap(true, Ordering::SeqCst) {
        send_error_and_close(
            &stream,
            proto::ErrorCode::Busy,
            "another subscriber is active",
        );
        return Ok(());
    }
    let _guard = SubscriberGuard(slot);

    // 3. Handshake.
    stream
        .set_recv_timeout(Some(config.handshake_timeout))
        .map_err(|error| format!("set handshake timeout: {error}"))?;
    let hello = match recv_message(&stream) {
        Ok(Some(message)) => message,
        Ok(None) => return Ok(()), // client left before hello
        Err(error) => return Err(format!("handshake read failed: {error}")),
    };
    let _client_minor = match hello {
        proto::Message::Hello { major, minor, .. } => {
            if major != proto::PROTOCOL_MAJOR {
                send_error_and_close(
                    &stream,
                    proto::ErrorCode::VersionMismatch,
                    &format!("unsupported protocol major {major}"),
                );
                return Ok(());
            }
            minor
        }
        other => {
            send_error_and_close(
                &stream,
                proto::ErrorCode::InvalidPayload,
                &format!("expected hello, got type {}", other.message_type()),
            );
            return Ok(());
        }
    };

    // While the protocol minor is 0 the agreed minor is always 0; when a
    // minor revision lands this must become `client_minor.min(PROTOCOL_MINOR)`
    // using the minor negotiated in the hello above.
    let agreed_minor = proto::PROTOCOL_MINOR;
    let ack = proto::Message::HelloAck {
        major: proto::PROTOCOL_MAJOR,
        minor: agreed_minor,
        caps: 0,
        heartbeat_interval_ms: config.heartbeat_interval_ms,
        seat: config.seat.clone(),
    };
    if let Err(error) = send_message(&stream, &ack) {
        return Err(format!("hello_ack send failed: {error}"));
    }
    eprintln!(
        "battuta-inputd: subscriber pid {} (uid {}) connected, agreed protocol 1.{agreed_minor}",
        peer.pid, peer.uid
    );

    // 4. Event stream.
    match (&config.synthetic, hub) {
        (Some(synthetic_config), _) => {
            run_synthetic_session(&stream, *synthetic_config, config.heartbeat_interval_ms)
        }
        (None, Some(hub)) => run_hub_session(&stream, &hub, config.heartbeat_interval_ms),
        (None, None) => Err("internal error: real mode without event hub".to_owned()),
    }
}

/// Real-mode session: forwards hub events to the subscriber with per-session
/// sequencing, heartbeats, and disconnect detection.
fn run_hub_session(
    stream: &SeqPacketStream,
    hub: &Hub,
    heartbeat_interval_ms: u32,
) -> Result<(), String> {
    let heartbeat_interval = if heartbeat_interval_ms > 0 {
        Some(Duration::from_millis(u64::from(heartbeat_interval_ms)))
    } else {
        None
    };
    let mut sequence: u64 = 0;
    let mut last_frame = Instant::now();

    loop {
        let wait = heartbeat_interval.unwrap_or(Duration::from_millis(250));
        match hub.recv_timeout(wait) {
            Ok(Some(event)) => {
                sequence += 1;
                let message = source_message(event, sequence);
                send_message(stream, &message)
                    .map_err(|error| format!("event send failed: {error}"))?;
                last_frame = Instant::now();
            }
            Ok(None) => {}
            Err(crate::hub::HubClosed) => return Ok(()), // broker shutting down
        }

        // Subscriber traffic: clients stay silent after the handshake.
        match try_recv_message(stream) {
            Ok(None) => {}
            Ok(Some(message)) => {
                send_error_and_close(
                    stream,
                    proto::ErrorCode::InvalidPayload,
                    "clients must not send messages after the handshake",
                );
                return Err(format!(
                    "unexpected client message type {}",
                    message.message_type()
                ));
            }
            Err(error) => return Err(error),
        }

        if let Some(interval) = heartbeat_interval
            && last_frame.elapsed() >= interval
        {
            sequence += 1;
            send_message(
                stream,
                &proto::Message::Heartbeat {
                    sequence,
                    monotonic_ns: monotonic_ns(),
                },
            )
            .map_err(|error| format!("heartbeat send failed: {error}"))?;
            last_frame = Instant::now();
        }
    }
}

/// Converts a source event into a wire message. Monotonic time is stamped at
/// conversion: evdev timestamps are realtime, not monotonic, so the broker
/// reports receipt time (contract §3 documents the semantics).
fn source_message(event: SourceEvent, sequence: u64) -> proto::Message {
    let monotonic_ns = monotonic_ns();
    match event {
        SourceEvent::Key { code, value } => proto::Message::Keyboard {
            sequence,
            monotonic_ns,
            code,
            value,
            origin: proto::Origin::Hardware,
        },
        SourceEvent::Button { code, value } => proto::Message::PointerButton {
            sequence,
            monotonic_ns,
            code,
            value,
            origin: proto::Origin::Hardware,
        },
        SourceEvent::Reset { reason } => proto::Message::Reset {
            sequence,
            monotonic_ns,
            reason,
        },
    }
}

fn recv_message(stream: &SeqPacketStream) -> Result<Option<proto::Message>, String> {
    let mut buffer = [0_u8; proto::MAX_FRAME_LENGTH];
    match stream.recv(&mut buffer) {
        Ok(0) => Ok(None),
        Ok(count) => {
            let (message, consumed) = proto::decode(&buffer[..count])
                .map_err(|error| format!("invalid frame from client: {error}"))?;
            if consumed != count {
                return Err("client packed multiple frames into one packet".to_owned());
            }
            Ok(Some(message))
        }
        Err(error)
            if error.kind() == ErrorKind::WouldBlock || error.kind() == ErrorKind::TimedOut =>
        {
            Ok(None) // timeout: caller decides what "no data" means
        }
        Err(error) => Err(error.to_string()),
    }
}

/// Non-blocking variant used by the real-mode pump: `Ok(None)` when the
/// client has not sent anything right now.
fn try_recv_message(stream: &SeqPacketStream) -> Result<Option<proto::Message>, String> {
    let mut buffer = [0_u8; proto::MAX_FRAME_LENGTH];
    match stream.try_recv(&mut buffer) {
        Ok(Some(0)) | Ok(None) => Ok(None),
        Ok(Some(count)) => {
            let (message, consumed) = proto::decode(&buffer[..count])
                .map_err(|error| format!("invalid frame from client: {error}"))?;
            if consumed != count {
                return Err("client packed multiple frames into one packet".to_owned());
            }
            Ok(Some(message))
        }
        Err(error) => Err(error.to_string()),
    }
}

/// Replays the synthetic script on the connection, one step per interval tick,
/// looping until the client goes away. Heartbeats interleave when negotiated.
fn run_synthetic_session(
    stream: &SeqPacketStream,
    synthetic_config: SyntheticConfig,
    heartbeat_interval_ms: u32,
) -> Result<(), String> {
    stream
        .set_recv_timeout(Some(synthetic_config.interval))
        .map_err(|error| format!("set session timeout: {error}"))?;

    let steps = script();
    let mut next_step = 0;
    let mut sequence: u64 = 0;
    let mut last_frame = Instant::now();
    let heartbeat_interval = if heartbeat_interval_ms > 0 {
        Some(Duration::from_millis(u64::from(heartbeat_interval_ms)))
    } else {
        None
    };

    loop {
        // Between steps, detect client disconnects and unexpected traffic.
        match recv_message(stream) {
            Ok(None) => {}
            Ok(Some(message)) => {
                send_error_and_close(
                    stream,
                    proto::ErrorCode::InvalidPayload,
                    "clients must not send messages after the handshake",
                );
                return Err(format!(
                    "unexpected client message type {}",
                    message.message_type()
                ));
            }
            Err(error) => return Err(error),
        }

        if let Some(interval) = heartbeat_interval
            && last_frame.elapsed() >= interval
        {
            sequence += 1;
            send_message(
                stream,
                &proto::Message::Heartbeat {
                    sequence,
                    monotonic_ns: monotonic_ns(),
                },
            )
            .map_err(|error| format!("heartbeat send failed: {error}"))?;
            last_frame = Instant::now();
            continue;
        }

        let step = steps[next_step];
        next_step += 1;
        if next_step >= steps.len() {
            if !synthetic_config.loop_forever {
                return Ok(());
            }
            next_step = 0;
        }

        sequence += 1;
        let message = match step {
            crate::synthetic::ScriptStep::Key { code, value } => proto::Message::Keyboard {
                sequence,
                monotonic_ns: monotonic_ns(),
                code,
                value,
                origin: proto::Origin::Synthetic,
            },
            crate::synthetic::ScriptStep::Button { code, value } => proto::Message::PointerButton {
                sequence,
                monotonic_ns: monotonic_ns(),
                code,
                value,
                origin: proto::Origin::Synthetic,
            },
            crate::synthetic::ScriptStep::Reset { reason } => proto::Message::Reset {
                sequence,
                monotonic_ns: monotonic_ns(),
                reason,
            },
        };
        send_message(stream, &message).map_err(|error| format!("event send failed: {error}"))?;
        last_frame = Instant::now();
    }
}
