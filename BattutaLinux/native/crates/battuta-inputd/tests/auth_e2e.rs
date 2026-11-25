//! End-to-end authorization validation over a private D-Bus bus.
//!
//! The mock logind/polkit services are written against GIO — an independent,
//! strict D-Bus implementation — so the broker's hand-rolled client is
//! validated in both directions: our marshaling must be parseable by GIO and
//! our parser must understand GIO's bytes. The config file is re-read on
//! every D-Bus call, so tests can flip authorization decisions without
//! restarting anything.
//!
//! Scenarios:
//! 1. authorized session + polkit `yes` → handshake succeeds;
//! 2. polkit `no` → `Error(Unauthorized)` then clean EOF;
//! 3. uid without a graphical logind session → rejected with that reason;
//! 4. Lock/Unlock and PrepareForSleep signals drive the session gate.

use battuta_input_protocol as proto;
use battuta_inputd::dbus::{Connection, Endpoint, Value};
use battuta_inputd::seqpacket::SeqPacketStream;
use std::io::ErrorKind;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

struct ChildProcess(Child);

impl Drop for ChildProcess {
    fn drop(&mut self) {
        let _ = self.0.kill();
        let _ = self.0.wait();
    }
}

fn tool_available(name: &str) -> bool {
    std::env::var("PATH")
        .map(|paths| {
            paths
                .split(':')
                .any(|dir| std::fs::metadata(Path::new(dir).join(name)).is_ok())
        })
        .unwrap_or(false)
}

fn python_has_gi() -> bool {
    Command::new("python3")
        .arg("-c")
        .arg("import gi.repository.Gio")
        .output()
        .map(|output| output.status.success())
        .unwrap_or(false)
}

fn temp_dir(tag: &str) -> PathBuf {
    std::env::temp_dir().join(format!("battuta-auth-e2e-{}-{tag}", std::process::id()))
}

const MOCK_SERVICE_PY: &str = r#"
import json, sys
import gi
gi.require_version("Gio", "2.0")
from gi.repository import Gio, GLib

CONFIG_PATH = sys.argv[1]
BUS_ADDRESS = sys.argv[2]

def load_config():
    with open(CONFIG_PATH) as handle:
        return json.load(handle)

NODE_XML = """<node>
 <interface name="org.freedesktop.login1.Manager">
  <method name="ListSessions"><arg direction="out" type="a(susso)"/></method>
  <signal name="PrepareForSleep"><arg type="b"/></signal>
 </interface>
 <interface name="org.freedesktop.login1.Session">
  <signal name="Lock"/>
  <signal name="Unlock"/>
 </interface>
 <interface name="org.freedesktop.DBus.Properties">
  <method name="GetAll"><arg direction="in" type="s"/><arg direction="out" type="a{sv}"/></method>
 </interface>
 <interface name="org.freedesktop.PolicyKit1.Authority">
  <method name="CheckAuthorization">
   <arg direction="in" type="(sa{sv})"/>
   <arg direction="in" type="s"/>
   <arg direction="in" type="a{ss}"/>
   <arg direction="in" type="u"/>
   <arg direction="in" type="s"/>
   <arg direction="out" type="(bba{ss})"/>
  </method>
 </interface>
 <interface name="battuta.test.Control">
  <method name="Ping"/>
  <method name="Emit"><arg direction="in" type="s"/><arg direction="in" type="b"/></method>
 </interface>
</node>"""

def on_method_call(connection, sender, path, interface, method, params, invocation):
    try:
        if interface == "org.freedesktop.login1.Manager" and method == "ListSessions":
            sessions = [(s["id"], s["uid"], s["name"], s["seat"], s["path"])
                        for s in load_config()["sessions"]]
            invocation.return_value(GLib.Variant("(a(susso))", (sessions,)))
        elif interface == "org.freedesktop.DBus.Properties" and method == "GetAll":
            props = {k: GLib.Variant(v[0], v[1])
                     for k, v in load_config()["session_properties"].items()}
            invocation.return_value(GLib.Variant("(a{sv})", (props,)))
        elif interface == "org.freedesktop.PolicyKit1.Authority" and method == "CheckAuthorization":
            cfg = load_config()
            decision = (cfg["polkit_authorized"], cfg.get("polkit_challenge", False), {})
            invocation.return_value(GLib.Variant("((bba{ss}))", (decision,)))
        elif interface == "battuta.test.Control" and method == "Ping":
            invocation.return_value(None)
        elif interface == "battuta.test.Control" and method == "Emit":
            name, value = params
            if name == "PrepareForSleep":
                connection.emit_signal(None, "/org/freedesktop/login1",
                    "org.freedesktop.login1.Manager", "PrepareForSleep",
                    GLib.Variant("(b)", (value,)))
            elif name == "Lock":
                connection.emit_signal(None, "/org/freedesktop/login1/session/_33",
                    "org.freedesktop.login1.Session", "Lock", None)
            elif name == "Unlock":
                connection.emit_signal(None, "/org/freedesktop/login1/session/_33",
                    "org.freedesktop.login1.Session", "Unlock", None)
            invocation.return_value(None)
        else:
            invocation.return_value(None)
    except Exception as error:
        sys.stderr.write(f"mock service error: {error}\n")
        sys.stderr.flush()
        invocation.return_error(Gio.DBusError, Gio.DBusError.FAILED, str(error))

connection = Gio.DBusConnection.new_for_address_sync(
    BUS_ADDRESS,
    Gio.DBusConnectionFlags.AUTHENTICATION_CLIENT | Gio.DBusConnectionFlags.MESSAGE_BUS_CONNECTION,
    None, None)

node = Gio.DBusNodeInfo.new_for_xml(NODE_XML)
interfaces = {i.name: i for i in node.interfaces}
for path, interface in [
    ("/org/freedesktop/login1", "org.freedesktop.login1.Manager"),
    ("/org/freedesktop/login1", "org.freedesktop.DBus.Properties"),
    ("/org/freedesktop/login1/session/_33", "org.freedesktop.login1.Session"),
    ("/org/freedesktop/login1/session/_33", "org.freedesktop.DBus.Properties"),
    ("/org/freedesktop/PolicyKit1/Authority", "org.freedesktop.PolicyKit1.Authority"),
    ("/battuta/test/Control", "battuta.test.Control"),
]:
    connection.register_object(path, interfaces[interface], on_method_call, None, None)

# 4 = DO_NOT_QUEUE
for name in ["org.freedesktop.login1", "org.freedesktop.PolicyKit1", "battuta.test.Control"]:
    connection.call_sync("org.freedesktop.DBus", "/org/freedesktop/DBus",
        "org.freedesktop.DBus", "RequestName",
        GLib.Variant("(su)", (name, 4)), GLib.VariantType("(u)"),
        Gio.DBusCallFlags.NONE, -1, None)

sys.stderr.write("mock services ready\n")
sys.stderr.flush()
GLib.MainLoop().run()
"#;

/// Everything the tests share: private bus + mock services + broker.
struct Environment {
    _daemon: ChildProcess,
    _service: ChildProcess,
    bus_address: String,
    _temp: PathBuf,
}

impl Drop for Environment {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self._temp);
    }
}

fn spawn_environment(tag: &str) -> Option<Environment> {
    if !tool_available("dbus-daemon") || !tool_available("python3") || !python_has_gi() {
        eprintln!("skipping: dbus-daemon or python3+Gio unavailable");
        return None;
    }
    let temp = temp_dir(tag);
    std::fs::create_dir_all(&temp).expect("create temp dir");

    let abstract_name = format!("battuta-auth-e2e-{}-{tag}", std::process::id());
    let bus_config = format!(
        "<!DOCTYPE busconfig PUBLIC \"-//freedesktop//DTD D-BUS Bus Configuration 2.0//EN\" \
         \"http://www.freedesktop.org/standards/dbus/1.0/busconfig.dtd\">\n\
         <busconfig>\n  <listen>unix:abstract={abstract_name}</listen>\n  \
         <auth>EXTERNAL</auth>\n  \
         <policy context=\"default\">\n    \
           <allow send_destination=\"*\" eavesdrop=\"true\"/>\n    \
           <allow eavesdrop=\"true\"/>\n    \
           <allow own=\"*\"/>\n    \
           <allow user=\"*\"/>\n  \
         </policy>\n\
         </busconfig>\n"
    );
    let config_path = temp.join("bus.xml");
    std::fs::write(&config_path, bus_config).expect("write bus config");

    let mut daemon = Command::new("dbus-daemon")
        .arg("--nofork")
        .arg("--nopidfile")
        .arg("--nosyslog")
        .arg("--config-file")
        .arg(&config_path)
        .arg("--print-address=1")
        .stdout(Stdio::piped())
        .stderr(Stdio::inherit())
        .spawn()
        .expect("spawn dbus-daemon");
    let mut address = String::new();
    use std::io::BufRead;
    let taken = std::io::BufReader::new(daemon.stdout.take().expect("daemon stdout"))
        .read_line(&mut address)
        .expect("read bus address");
    assert!(taken > 0, "dbus-daemon printed no address");
    let bus_address = address.trim().to_owned();

    let service_path = temp.join("mock_service.py");
    std::fs::write(&service_path, MOCK_SERVICE_PY).expect("write mock service");
    let service = Command::new("python3")
        .arg(&service_path)
        .arg(temp.join("config.json"))
        .arg(&bus_address)
        .stdout(Stdio::null())
        .stderr(Stdio::inherit())
        .spawn()
        .expect("spawn mock service");

    // Wait until the mock services answer on the private bus.
    let deadline = Instant::now() + Duration::from_secs(10);
    loop {
        match Connection::connect(&bus_address) {
            Ok(connection) => {
                let ping = connection.call(
                    Endpoint {
                        destination: "battuta.test.Control",
                        path: "/battuta/test/Control",
                        interface: "battuta.test.Control",
                        member: "Ping",
                    },
                    Vec::new(),
                    "",
                    Duration::from_secs(2),
                );
                match ping {
                    Ok(_) => break,
                    Err(error) if Instant::now() < deadline => {
                        eprintln!("waiting for mock services: {error}");
                        std::thread::sleep(Duration::from_millis(100));
                    }
                    Err(error) => panic!("mock services never became ready: {error}"),
                }
            }
            Err(error) if Instant::now() < deadline => {
                eprintln!("waiting for private bus: {error}");
                std::thread::sleep(Duration::from_millis(100));
            }
            Err(error) => panic!("private bus never came up: {error}"),
        }
    }

    Some(Environment {
        _daemon: ChildProcess(daemon),
        _service: ChildProcess(service),
        bus_address,
        _temp: temp,
    })
}

/// Writes the mock service config atomically (rename within the same
/// directory), so in-flight D-Bus calls never observe a partial file.
fn write_config(env: &Environment, sessions: &[u32], polkit_authorized: bool, challenge: bool) {
    let _uid = unsafe { libc::geteuid() };
    let session_entries: Vec<String> = sessions
        .iter()
        .map(|uid| {
            format!(
                "{{\"id\": \"33\", \"uid\": {uid}, \"name\": \"bing\", \
                 \"seat\": \"seat0\", \"path\": \"/org/freedesktop/login1/session/_33\"}}"
            )
        })
        .collect();
    let config = format!(
        "{{\n  \"sessions\": [{sessions}],\n  \"session_properties\": {{\
         \"Active\": [\"b\", true], \"Remote\": [\"b\", false], \
         \"Class\": [\"s\", \"user\"], \"Type\": [\"s\", \"wayland\"], \
         \"VTNr\": [\"u\", 1]}},\n  \"polkit_authorized\": {polkit_authorized},\n  \
         \"polkit_challenge\": {challenge}\n}}",
        sessions = session_entries.join(","),
    );
    let final_path = env._temp.join("config.json");
    let staging_path = env._temp.join("config.json.new");
    std::fs::write(&staging_path, config).expect("write staging config");
    std::fs::rename(&staging_path, &final_path).expect("install config");
}

fn control_emit(connection: &Connection, signal: &str, value: bool) {
    connection
        .call(
            Endpoint {
                destination: "battuta.test.Control",
                path: "/battuta/test/Control",
                interface: "battuta.test.Control",
                member: "Emit",
            },
            vec![Value::str(signal), Value::Bool(value)],
            "sb",
            Duration::from_secs(5),
        )
        .expect("control emit");
}

fn spawn_broker(env: &Environment, socket_path: &Path, extra: &[&str]) -> ChildProcess {
    let child = Command::new(env!("CARGO_BIN_EXE_battuta-inputd"))
        .arg("--socket-path")
        .arg(socket_path)
        .arg("--no-vt-gate")
        .arg("--system-bus-address")
        .arg(&env.bus_address)
        .args(extra)
        .stdout(Stdio::null())
        .stderr(Stdio::inherit())
        .spawn()
        .expect("spawn battuta-inputd");
    ChildProcess(child)
}

fn connect_with_retry(socket_path: &Path) -> SeqPacketStream {
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

fn send_hello(stream: &SeqPacketStream) {
    let hello = proto::Message::Hello {
        major: proto::PROTOCOL_MAJOR,
        minor: proto::PROTOCOL_MINOR,
        caps: 0,
        client_name: "auth-e2e".to_owned(),
    };
    stream
        .send(&proto::encode_frame(&hello).expect("encode hello"))
        .expect("send hello");
}

fn recv_one(stream: &SeqPacketStream, wait: Duration) -> Option<proto::Message> {
    stream.set_recv_timeout(Some(wait)).expect("set timeout");
    let mut buffer = [0_u8; proto::MAX_FRAME_LENGTH];
    match stream.recv(&mut buffer) {
        Ok(0) => None, // clean EOF
        Ok(count) => Some(proto::decode(&buffer[..count]).expect("decode").0),
        Err(error) if error.kind() == ErrorKind::WouldBlock => None,
        Err(error) => panic!("recv failed: {error}"),
    }
}

#[test]
fn authorization_and_lifecycle_gate_end_to_end() {
    let Some(env) = spawn_environment("main") else {
        return;
    };
    let uid = unsafe { libc::geteuid() };

    // Phase A: the test uid owns an active graphical session and polkit
    // authorizes → the handshake must succeed.
    write_config(&env, &[uid], true, false);
    let socket_path = env._temp.join("broker.sock");
    let broker = spawn_broker(&env, &socket_path, &[]);
    let subscriber = {
        let stream = connect_with_retry(&socket_path);
        send_hello(&stream);
        match recv_one(&stream, Duration::from_secs(5)) {
            Some(proto::Message::HelloAck { .. }) => stream,
            other => panic!("expected hello_ack under full authorization, got {other:?}"),
        }
    };

    // Lock → pause, Unlock → resume, sleep → pause, wake → resume.
    let control = Connection::connect(&env.bus_address).expect("control connection");
    control_emit(&control, "Lock", false);
    assert!(
        matches!(
            recv_one(&subscriber, Duration::from_secs(5)),
            Some(proto::Message::Reset {
                reason: proto::ResetReason::SessionPause,
                ..
            })
        ),
        "Lock must pause delivery"
    );
    control_emit(&control, "Unlock", false);
    assert!(
        matches!(
            recv_one(&subscriber, Duration::from_secs(5)),
            Some(proto::Message::Reset {
                reason: proto::ResetReason::SessionResume,
                ..
            })
        ),
        "Unlock must resume delivery"
    );
    control_emit(&control, "PrepareForSleep", true);
    assert!(
        matches!(
            recv_one(&subscriber, Duration::from_secs(5)),
            Some(proto::Message::Reset {
                reason: proto::ResetReason::SessionPause,
                ..
            })
        ),
        "PrepareForSleep(true) must pause delivery"
    );
    control_emit(&control, "PrepareForSleep", false);
    assert!(
        matches!(
            recv_one(&subscriber, Duration::from_secs(5)),
            Some(proto::Message::Reset {
                reason: proto::ResetReason::SessionResume,
                ..
            })
        ),
        "PrepareForSleep(false) must resume delivery"
    );
    drop(subscriber);

    // Phase B: polkit denies a fresh connection → Error then clean EOF.
    write_config(&env, &[uid], false, false);
    {
        let stream = connect_with_retry(&socket_path);
        send_hello(&stream);
        match recv_one(&stream, Duration::from_secs(5)) {
            Some(proto::Message::Error { code, message }) => {
                assert_eq!(code, proto::ErrorCode::Unauthorized);
                assert!(
                    message.contains("polkit"),
                    "reason should mention polkit: {message}"
                );
            }
            other => panic!("expected Unauthorized error, got {other:?}"),
        }
        assert!(
            recv_one(&stream, Duration::from_secs(2)).is_none(),
            "broker must close cleanly after the error frame"
        );
    }

    // Phase C: polkit wants interaction the headless broker cannot provide.
    write_config(&env, &[uid], false, true);
    {
        let stream = connect_with_retry(&socket_path);
        send_hello(&stream);
        match recv_one(&stream, Duration::from_secs(5)) {
            Some(proto::Message::Error { code, message }) => {
                assert_eq!(code, proto::ErrorCode::Unauthorized);
                assert!(
                    message.contains("interactive"),
                    "challenge reason should tell the user to authorize interactively: {message}"
                );
            }
            other => panic!("expected Unauthorized error, got {other:?}"),
        }
    }

    // Phase D: no logind session for the uid → rejected before polkit.
    write_config(&env, &[], true, false);
    {
        let stream = connect_with_retry(&socket_path);
        send_hello(&stream);
        match recv_one(&stream, Duration::from_secs(5)) {
            Some(proto::Message::Error { code, message }) => {
                assert_eq!(code, proto::ErrorCode::Unauthorized);
                assert!(
                    message.contains("graphical session"),
                    "reason should mention the missing session: {message}"
                );
            }
            other => panic!("expected Unauthorized error, got {other:?}"),
        }
    }

    drop(broker);
    let _ = uid;
}

/// Plan §5.2: the broker claims `io.github.wormforce.Battuta.Input1` on the
/// system bus, and a second broker refuses to start while the name is held.
#[test]
fn broker_claims_the_well_known_bus_name_and_blocks_duplicates() {
    let Some(env) = spawn_environment("bus-name") else {
        return;
    };
    let socket_path = env._temp.join("input.sock");
    let broker = spawn_broker(&env, &socket_path, &["--dev-auth", "--synthetic"]);

    // The name must appear on the private bus.
    let deadline = Instant::now() + Duration::from_secs(5);
    let mut claimed = false;
    while Instant::now() < deadline {
        let connection = Connection::connect(&env.bus_address).expect("connect to private bus");
        let names = connection
            .call(
                Endpoint {
                    destination: "org.freedesktop.DBus",
                    path: "/org/freedesktop/DBus",
                    interface: "org.freedesktop.DBus",
                    member: "ListNames",
                },
                Vec::new(),
                "",
                Duration::from_secs(2),
            )
            .expect("ListNames");
        if let Some(Value::Array { items, .. }) = names.into_iter().next()
            && items.iter().any(|value| {
                matches!(value, Value::Str(name) if name == battuta_inputd::dbus::BROKER_BUS_NAME)
            })
        {
            claimed = true;
            break;
        }
        std::thread::sleep(Duration::from_millis(100));
    }
    assert!(claimed, "broker never claimed its bus name");

    // A second broker with the same bus must refuse to start.
    let second_socket = env._temp.join("input-second.sock");
    let status = Command::new(env!("CARGO_BIN_EXE_battuta-inputd"))
        .arg("--socket-path")
        .arg(&second_socket)
        .arg("--no-vt-gate")
        .arg("--system-bus-address")
        .arg(&env.bus_address)
        .arg("--dev-auth")
        .arg("--synthetic")
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .status()
        .expect("run second broker");
    assert!(
        !status.success(),
        "a second broker must refuse to start while the bus name is held"
    );

    drop(broker);
    let _ = status;
}

/// RequestName with DO_NOT_QUEUE on a private bus (the real system bus
/// refuses owning arbitrary names by policy). Returns 1 = PRIMARY_OWNER,
/// 4 = ALREADY_OWNER for the same connection, 3 = EXISTS for a second one.
#[test]
fn request_name_flags_round_trip() {
    let Some(env) = spawn_environment("request-name") else {
        return;
    };
    let name = "io.github.wormforce.Battuta.Input1.Test.pid";
    let first = Connection::connect(&env.bus_address).expect("first connection");
    assert_eq!(first.request_name(name, Duration::from_secs(5)).unwrap(), 1);
    assert_eq!(first.request_name(name, Duration::from_secs(5)).unwrap(), 4);
    let second = Connection::connect(&env.bus_address).expect("second connection");
    assert_eq!(
        second.request_name(name, Duration::from_secs(5)).unwrap(),
        3
    );
}
