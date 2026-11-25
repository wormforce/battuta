<!-- markdownlint-disable MD013 -->

# Battuta Linux input broker wire protocol v1

Status: frozen contract (v1). This document defines the only wire format
spoken between `battuta-inputd` (system input broker, Rust) and the Battuta
client (C#, `Battuta.Engine.Input.Broker`). Rust and C# each implement one
codec; both must produce and accept byte-identical frames. Binary golden
fixtures live in `fixtures/broker/` and are shared by both test suites.

- Reference encoders/decoders: `BattutaLinux/native/crates/battuta-input-protocol`
  (Rust) and `shared/dotnet/Battuta.Engine/Input/Broker/` (C#).
- Reference broker: `BattutaLinux/native/crates/battuta-inputd`.
- Locking tests: `Battuta.Engine.Tests/Input/Broker/*` and the Rust crate tests.

## 1. Transport and session model

- Transport is a Unix-domain `SOCK_SEQPACKET` socket. Each `recv` returns
  exactly one frame; frame boundaries are therefore kernel-guaranteed. The
  streaming decoder exists for tests and for `SOCK_STREAM` fallbacks only.
- Default socket path: `/run/battuta-inputd/input.sock`, owned by the
  dedicated broker user `battuta-input`, mode `0660` (configurable at broker
  start with `--socket-mode` / `--socket-group`). The path is
  configurable; it is not part of this contract.
- One connection is one session. There is at most one active subscriber; a
  second concurrent subscriber is rejected with an `error` message
  (`code = 5`, busy) and closed. A new connection after the previous one is
  gone starts a fresh session (fresh sequence counter, fresh handshake).
- D-Bus name (authorization phase, not the event stream):
  `io.github.wormforce.Battuta.Input1` on the system bus. Reserved for the
  broker; the headless PR 5b broker does not claim it yet (planned for the
  PR 7 GUI work).
- The broker only sends events while the calling user has an active, local,
  graphical logind session. On lock screen, VT switch, sleep, or user switch
  the broker sends `reset` (`reason = 3`, session pause) and stops sending
  events until the session becomes active again (`reason = 4`, resume).
  See section 8 for how connections are authorized.

## 2. Byte-level framing

All multibyte integers are **little-endian**. A frame is:

```text
offset  size  field
0       4     magic: ASCII bytes "BTTI" (0x42 0x54 0x54 0x49)
4       1     message_type (see section 3)
5       1     flags: writers send 0x00; readers must ignore unknown flag bits
6       2     payload_length: uint16, bytes of payload that follow
8       n     payload
```

- Header is 8 bytes; maximum payload is 4096 bytes (maximum frame 4104).
- A payload length greater than the maximum is a fatal framing error: the
  peer that observes it sends `error` (`code = 2`) and closes the connection.
  Unknown message types with a legal length are skipped, not errors.
- Zero-length payloads are only valid for future message types; v1 messages
  all have fixed or bounded payloads (section 3).

## 3. Messages

| Type | Name | Direction | Payload |
| --- | --- | --- | --- |
| `0x01` | `hello` | client → broker | 9–73 bytes, see 3.1 |
| `0x02` | `hello_ack` | broker → client | 11–43 bytes, see 3.2 |
| `0x03` | `keyboard` | broker → client | 24 bytes, see 3.3 |
| `0x04` | `pointer_button` | broker → client | 24 bytes, see 3.3 |
| `0x05` | `reset` | broker → client | 24 bytes, see 3.4 |
| `0x06` | `heartbeat` | broker → client | 16 bytes, see 3.5 |
| `0x07` | `error` | either | 5–133 bytes, see 3.6 |

Message types `0x08`–`0xff` are unknown to v1 and must be skipped by length.
After receiving `error` the sender of the error closes the connection unless
the message documents otherwise.

### 3.1 `hello` (client → broker)

```text
offset  size  field
0       2     protocol_major: uint16, must equal 1
2       2     protocol_minor: uint16, highest minor the client supports
4       4     caps: uint32, reserved, must be 0 in v1
8       1     name_length: uint8, 0–64
9       n     client_name: UTF-8, informational only (e.g. "battuta")
```

### 3.2 `hello_ack` (broker → client)

```text
offset  size  field
0       2     protocol_major: uint16, must equal 1
2       2     protocol_minor: uint16, agreed minor = min(client, broker)
4       4     caps: uint32, reserved, must be 0 in v1
8       4     heartbeat_interval_ms: uint32, 0 = heartbeats disabled
12      1     seat_length: uint8, 0–32
13      n     seat: UTF-8 logind seat name (e.g. "seat0")
```

Version rules: if `protocol_major` differs, the peer replies `error`
(`code = 1`) and closes. A larger minor is allowed and means "unknown
optional fields may appear"; v1 readers ignore payload bytes beyond the
documented length of each known message.

### 3.3 `keyboard` and `pointer_button` (broker → client)

Both use the same 24-byte payload:

```text
offset  size  field
0       8     sequence: uint64, per-session, starts at 1, strictly +1
8       8     monotonic_ns: uint64, broker's CLOCK_MONOTONIC nanoseconds
16      2     code: uint16, raw evdev code (KEY_* or BTN_*)
18      2     value: uint16, evdev value: 0 = release, 1 = press, 2 = repeat
20      1     origin: uint8, 0 = hardware, 1 = synthetic, 2 = unknown
21      1     reserved: uint8, must be 0 on write, ignored on read
22      2     reserved: uint16, must be 0 on write, ignored on read
```

- `code` for keyboards is the Linux `KEY_*` code; for pointer messages it is
  restricted to `BTN_LEFT` (0x110), `BTN_RIGHT` (0x111), `BTN_MIDDLE`
  (0x112), `BTN_SIDE` (0x113), `BTN_EXTRA` (0x114). Any other `BTN_*` or
  `KEY_*` code outside these rules must not be sent; clients treat unknown
  codes as "unmapped key" (stable neutral id `linux.evdev.<code>`), never as
  an error.
- `pointer_button` messages never carry `value = 2` (no button repeat).
- Key-to-id mapping, modifier aggregation, and repeat semantics are client
  side (the planned `LinuxEvdevKeyMapper`) and are not part of this contract.

### 3.4 `reset` (broker → client)

```text
offset  size  field
0       8     sequence: uint64 (follows the global counter)
8       8     monotonic_ns: uint64
16      1     reason: uint8, see below
17      7     reserved: must be 0 on write, ignored on read
```

Reset reasons: `1` sequence gap, `2` device change/removal, `3` session
pause (lock screen, VT switch), `4` session resume, `5` broker backlog drop,
`6` reconnect, `7` other/unknown. Values `1`–`5` are broker-sent; `6` is
reserved for client-side synthesis on re-handshake. After any `reset` the
client must clear all pressed-key and pressed-button state.

### 3.5 `heartbeat` (broker → client)

```text
offset  size  field
0       8     sequence: uint64 (follows the global counter)
8       8     monotonic_ns: uint64
```

Sent only when `hello_ack.heartbeat_interval_ms > 0`. A client that receives
no frame (event or heartbeat) for 3 × the negotiated interval may treat the
connection as dead and reconnect.

### 3.6 `error` (either direction)

```text
offset  size  field
0       4     code: uint32, see below
4       1     message_length: uint8, 0–128
5       n     message: UTF-8, diagnostic only, not to be shown verbatim to users
```

Error codes: `1` protocol version mismatch, `2` frame too large, `3` bad
magic, `4` unauthorized, `5` busy (another active subscriber), `6` invalid
payload, `7` sequence regression, `8` internal error.

## 4. Sequencing, backlog, and drop policy

- The broker assigns `sequence` from a per-session counter starting at 1 and
  incremented by exactly 1 for every event, heartbeat, and reset message.
- Clients track the last seen sequence. A forward jump (gap) means the broker
  dropped events; the client synthesizes `reset` (`reason = 1`) before
  processing further events. A backwards or repeated sequence is a protocol
  error (`code = 7`) and closes the connection.
- On broker-side backlog overflow the broker keeps the newest events and
  sends `reset` (`reason = 5`) before the next fresh event. Stale backlogs
  must never be replayed as sound-triggering input.
- Sequence state is per connection. After any reconnect the client resets its
  counter and synthesizes `reset` (`reason = 6`) so pressed state is cleared.

## 5. Privacy boundary

The protocol must not carry and peers must not add: device names, USB
VID/PID, key characters or layout-dependent text, pointer coordinates or
scroll data, LED state, HID reports, window titles, or application
information. The broker reads `EV_KEY` only. This limits, but does not
eliminate, the ability to infer typed text from physical key sequences; the
product privacy statement must say exactly that (physical key sequences,
nothing more, nothing less).

## 6. Golden fixtures

All fixtures are little-endian byte streams as specified above.

| Fixture | Contents |
| --- | --- |
| `broker.v1.hello.bin` | one `hello` frame |
| `broker.v1.hello-ack.bin` | one `hello_ack` frame |
| `broker.v1.keyboard-press.bin` | one `keyboard` frame, value 1 |
| `broker.v1.keyboard-repeat.bin` | one `keyboard` frame, value 2 |
| `broker.v1.pointer-button.bin` | one `pointer_button` frame |
| `broker.v1.reset.bin` | one `reset` frame |
| `broker.v1.heartbeat.bin` | one `heartbeat` frame |
| `broker.v1.error.bin` | one `error` frame |
| `broker.v1.unknown-skip.bin` | one unknown-type frame (`0xc8`) that readers skip |
| `broker.v1.session-sample.bin` | full session transcript, see below |

`session-sample` is a concatenation of frames in this order: `hello`,
`hello_ack`, `keyboard` (press), `keyboard` (repeat), `pointer_button`
(press), `heartbeat`, `reset`, `keyboard` (release). Decoders must accept the
whole stream and yield exactly these messages in order; unknown-skip behavior
is exercised separately.

Fixture rules: every fixture is canonical — encoding the decoded message
back must reproduce the exact bytes. Both implementations must pass the same
assertions; fixtures are the cross-implementation compatibility gate.

## 7. Compatibility rules

- Frozen in v1: magic, header layout, message type values, payload layouts,
  little-endian byte order, the 4096-byte payload cap, and the meaning of
  every documented field.
- Minor revisions may: append payload bytes after the documented length of a
  known message (readers must ignore them), define new flag bits, define new
  message types, or define new enum values (origin, reset reason, error code)
  that older readers map to their unknown counterparts (origin `2`, reset
  reason `7`, error code `8`) instead of failing the frame.
- Major revision 2 requires a new contract document and a handshake gate.

## 8. Authorization (non-wire, v1)

How the broker decides whether a connecting peer may subscribe. This section
defines no frames; it constrains when `error` (`code = 4`, unauthorized) is
sent and what its diagnostic text contains.

- On every connection the broker reads the peer's uid/pid (`SO_PEERCRED`)
  and runs, in order:
  1. **Session check** via `org.freedesktop.login1`: the uid must own a
     logind session that is active, non-remote, of class `user`, and of a
     graphical type (`x11`, `wayland`, `mir`). A lock-screen session never
     qualifies. The check re-runs on every (re)connection — there is no
     authorization cache.
  2. **Polkit check** via `org.freedesktop.PolicyKit1.Authority`
     `CheckAuthorization` with a `unix-process` subject (pid, start-time,
     uid) against action `io.github.wormforce.Battuta.Input1.subscribe`
     (shipped in `BattutaLinux/packaging/polkit/`). The headless broker
     never requests interactive authorization: an `is_challenge` decision is
     reported as a rejection.
- Rejections send `error` (`code = 4`) with a diagnostic reason and close.
  The reason text mentions the failing stage, e.g. "uid 1000 owns no active
  local graphical session", "polkit denied …", "polkit requires interactive
  authorization …; authorize from the Battuta app". Text is diagnostic only
  (section 3.6) and capped at 128 bytes; the broker truncates rather than
  dropping the frame.
- If the system bus is unreachable the broker **fails closed**: it rejects
  all connections ("broker authorization unavailable: …") but keeps serving
  already-authorized subscribers. Development builds may bypass all of this
  with `--dev-auth` (never used by packaged installs).
- While a subscriber is connected, the broker additionally watches logind
  `PrepareForSleep` and per-session `Lock`/`Unlock` and brackets the event
  stream with `reset` reason `3`/`4` exactly as for VT switches (section
  3.4). Sleep and lock never close the connection.
- Wire impact: none. No frame, code, or length change; v1 remains frozen.

### 8.1 Broker well-known name and GUI-side interactive authorization (PR 7, non-wire)

Two non-wire additions implemented in PR 7; still no frame or code change
(the wire rules above remain frozen).

- **Well-known bus name.** The packaged broker claims
  `io.github.wormforce.Battuta.Input1` on the system bus at startup
  (`RequestName`, do-not-queue). Losing the race is fatal by design: a
  second broker must not run against the same socket directory, and clients
  discover the service by name, not by PID. Deviation escape hatches:
  `--no-bus-name` (development), and private test buses in the e2e suite.
  If the system bus is unavailable the broker logs a warning and continues
  (authorization already fails closed per this section; the name only
  affects discoverability).
- **GUI-side interactive authorization.** The headless broker never
  requests interaction; instead the **Battuta app** runs its own polkit
  `CheckAuthorization` for **its own process** (same action id, flags bit 0
  set) against the system bus and then reconnects. This turns a challenge
  into an in-session polkit agent prompt. The packaged policy requires
  authentication in an active local session and temporarily retains the
  decision for the same process so the retry can succeed. The app maps `(is_authorized, is_challenge)` exactly like the
  broker: authorized → reconnect; challenge → prompt, then reconnect on
  success; denied → surface the broker's diagnostic text (this section's
  rejection reason) and offer retry. The broker keeps treating challenges
  as rejections — clients that cannot run the interactive flow keep
  failing closed.
