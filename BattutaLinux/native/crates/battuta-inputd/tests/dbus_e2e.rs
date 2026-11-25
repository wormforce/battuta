//! Integration tests against the REAL system bus and its real services
//! (systemd-logind, polkit). They validate the hand-rolled D-Bus client
//! against independent, strict implementations of the wire format.
//!
//! The tests skip gracefully when no system bus is reachable (containers,
//! CI runners without systemd).

use battuta_inputd::dbus::{Connection, Endpoint, Value};
use std::time::{Duration, Instant};

fn system_bus_or_skip() -> Option<Connection> {
    match Connection::system() {
        Ok(connection) => Some(connection),
        Err(error) => {
            eprintln!("skipping: no system bus: {error}");
            None
        }
    }
}

fn logind_endpoint(member: &'static str) -> Endpoint<'static> {
    Endpoint {
        destination: "org.freedesktop.login1",
        path: "/org/freedesktop/login1",
        interface: "org.freedesktop.login1.Manager",
        member,
    }
}

#[test]
fn hello_and_list_sessions_round_trip() {
    let Some(connection) = system_bus_or_skip() else {
        return;
    };
    let unique = connection.unique_name().expect("unique name after Hello");
    assert!(unique.starts_with(':'), "unique names start with ':'");

    let reply = connection
        .call(
            logind_endpoint("ListSessions"),
            Vec::new(),
            "",
            Duration::from_secs(10),
        )
        .expect("ListSessions");
    assert_eq!(reply.len(), 1, "ListSessions returns one array");
    let (elem_sig, items) = reply[0].as_array().expect("a(susso)");
    assert_eq!(elem_sig, "(susso)");
    for item in items {
        let fields = item.as_struct().expect("session struct");
        assert_eq!(fields.len(), 5, "id, uid, name, seat, path");
        assert!(fields[0].as_str().is_some(), "session id is a string");
        assert!(fields[1].as_u32().is_some(), "uid is u32");
        assert!(fields[4].as_str().is_some(), "path is an object path");
    }
    eprintln!("ListSessions returned {} sessions", items.len());
}

#[test]
fn session_properties_get_all_round_trip() {
    let Some(connection) = system_bus_or_skip() else {
        return;
    };
    let reply = connection
        .call(
            logind_endpoint("ListSessions"),
            Vec::new(),
            "",
            Duration::from_secs(10),
        )
        .expect("ListSessions");
    let (_, items) = reply[0].as_array().expect("a(susso)");
    let Some(session) = items
        .iter()
        .filter_map(|item| item.as_struct().map(<[Value]>::to_vec))
        .find(|fields| fields[1].as_u32() == Some(unsafe { libc::geteuid() }))
    else {
        eprintln!("skipping: current uid owns no logind session");
        return;
    };
    let path = session[4].as_str().expect("session path").to_owned();

    // GetAll exercises a{sv} parsing against real GVariant marshaling.
    let path_owned = path.clone();
    let props = connection
        .call(
            Endpoint {
                destination: "org.freedesktop.login1",
                path: &path_owned,
                interface: "org.freedesktop.DBus.Properties",
                member: "GetAll",
            },
            vec![Value::str("org.freedesktop.login1.Session")],
            "s",
            Duration::from_secs(10),
        )
        .expect("GetAll");
    let (_, entries) = props[0].as_array().expect("a{sv}");
    let mut seen = 0;
    for entry in entries {
        let Some((key, value)) = entry.as_dict_entry() else {
            panic!("dict entry shape");
        };
        let Some(key) = key.as_str() else {
            panic!("dict entry key is a string");
        };
        let value = match value {
            Value::Variant(inner) => inner,
            other => other,
        };
        match key {
            "Active" | "Remote" => {
                assert!(value.as_bool().is_some(), "{key} is a bool");
                seen += 1;
            }
            "Class" | "Type" | "State" => {
                assert!(value.as_str().is_some(), "{key} is a string");
                seen += 1;
            }
            "VTNr" => {
                assert!(value.as_u32().is_some(), "VTNr is u32");
                seen += 1;
            }
            _ => {}
        }
    }
    assert!(seen >= 3, "expected core session properties, saw {seen}");
}

#[test]
fn add_match_and_signal_reception_path() {
    // Registers a match for a signal no one will emit during the test; the
    // point is that AddMatch round-trips and next_message times out cleanly
    // (after draining the bus's housekeeping signals like NameAcquired).
    let Some(connection) = system_bus_or_skip() else {
        return;
    };
    connection
        .add_match(
            "type='signal',sender='org.freedesktop.login1',\
             interface='org.freedesktop.login1.Manager',member='PrepareForSleep'",
        )
        .expect("AddMatch");
    let deadline = Instant::now() + Duration::from_secs(2);
    while Instant::now() < deadline {
        match connection.next_message(Duration::from_millis(150)) {
            None => break,
            Some(message) => {
                // Bus housekeeping (NameAcquired, NameLost) may arrive;
                // anything on the matched interface would be wrong.
                let interface = message.interface.as_deref().unwrap_or("");
                assert!(
                    interface.starts_with("org.freedesktop.DBus"),
                    "unexpected signal {message:?}"
                );
            }
        }
    }
}
