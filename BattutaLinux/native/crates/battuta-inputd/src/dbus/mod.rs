//! Minimal hand-rolled D-Bus client used for logind and polkit integration.
//!
//! Scope (deliberately narrow): connect over a unix socket, authenticate with
//! SASL EXTERNAL, say Hello, make method calls, register match rules,
//! request a well-known name, and receive signals. No fd passing, no service
//! side (mock-bus tests drive services written against GIO instead).

pub mod connection;
pub mod message;

/// The well-known system-bus name the broker claims at startup (plan §5.2).
/// Clients and tooling use its presence to detect a running broker.
pub const BROKER_BUS_NAME: &str = "io.github.wormforce.Battuta.Input1";

pub use connection::{CallError, Connection, Endpoint, system_bus_address};
pub use message::{FormatError, Message, Value, marshal, parse};
