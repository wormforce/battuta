//! Real evdev device discovery and `EV_KEY` reading.
//!
//! Scope (plan §5.2): only `EV_KEY` events with a forwarded code are read —
//! keyboard `KEY_*` codes below `BTN_MISC` plus the five mouse buttons
//! `BTN_LEFT..=BTN_EXTRA`. Everything else (motion, wheels, LEDs, HID reports)
//! is ignored at the read boundary. Devices are discovered by scanning
//! `/dev/input` and classified from their `EVIOCGBIT` capability bitmaps
//! (keyboard and/or pointer), which mirrors what udev computes for
//! `ID_INPUT_KEYBOARD` / `ID_INPUT_MOUSE` without linking libudev. Hotplug is
//! observed with inotify on the devices directory; device removal synthesizes
//! releases for every key the device held and a `Reset(DeviceChange)` so no
//! modifier can stay stuck (plan §5.4).

use crate::hub::{Hub, SourceEvent};
use battuta_input_protocol as proto;
use std::collections::HashMap;
use std::fs::{File, OpenOptions};
use std::io::{self, Read};
use std::os::fd::{AsRawFd, RawFd};
use std::os::unix::fs::OpenOptionsExt;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::{Duration, Instant};

/// `BTN_MISC`: keyboard `KEY_*` codes live below this, mouse buttons above.
pub const BTN_MISC: u16 = 0x100;
/// `BTN_LEFT`.
pub const BTN_LEFT: u16 = 0x110;
/// `BTN_EXTRA` — highest forwarded mouse button.
pub const BTN_EXTRA: u16 = 0x114;

const EV_KEY: u16 = 0x01;

/// How often the directory is re-synced even when inotify is healthy: a
/// safety net for missed or coalesced events, and the recovery path for
/// devices that were unreadable (udev permission race) at first sight.
const RESYNC_INTERVAL: Duration = Duration::from_secs(15);
const EV_MAX: usize = 0x1f;
const KEY_MAX: usize = 0x2ff;

/// Highest code treated as a keyboard key for classification. Anything in
/// `1..=KEY_FORWARD_MAX` is a keyboard key (`KEY_*` main block, function
/// keys, numpad, JIS, volume); the `BTN_*` and consumer blocks above it are
/// not.
const KEY_FORWARD_MAX: u16 = BTN_MISC - 1;
/// Byte size of `struct input_event` on this platform: timeval (2 longs) +
/// u16 type + u16 code + i32 value = 24 bytes on 64-bit Linux.
pub const RAW_EVENT_SIZE: usize = std::mem::size_of::<usize>() * 2 + 8;

const fn ioc(dir: u32, ty: u16, nr: u16, size: u16) -> u64 {
    ((dir as u64) << 30) | ((size as u64) << 16) | ((ty as u64) << 8) | (nr as u64)
}

/// `EV_IOCGBIT(type, len)`: `_IOC(_IOC_READ, 'E', 0x20 + type, len)`.
/// `_IOC_READ` is 2 on x86_64 and aarch64, the architectures this broker
/// targets.
const fn eviocgbit(ty: u16, len: usize) -> u64 {
    ioc(2, 0x45, 0x20 + ty, len as u16)
}

/// True when an `EV_KEY` code is forwarded to subscribers: keyboard keys
/// (`KEY_*`, below `BTN_MISC`) and exactly the five mouse buttons.
pub fn is_forwarded_key(code: u16) -> bool {
    code < BTN_MISC || (BTN_LEFT..=BTN_EXTRA).contains(&code)
}

/// One raw evdev event, stripped of the timestamp.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct RawEvent {
    pub event_type: u16,
    pub code: u16,
    pub value: i32,
}

impl RawEvent {
    /// Parses one `struct input_event` from exactly [`RAW_EVENT_SIZE`] bytes.
    /// The timestamp is discarded; the broker stamps monotonic time on
    /// delivery (evdev timestamps are `CLOCK_REALTIME`, not monotonic).
    pub fn parse(bytes: &[u8]) -> Option<Self> {
        if bytes.len() < RAW_EVENT_SIZE {
            return None;
        }
        let event_type = u16::from_ne_bytes([bytes[16], bytes[17]]);
        let code = u16::from_ne_bytes([bytes[18], bytes[19]]);
        let value = i32::from_ne_bytes([bytes[20], bytes[21], bytes[22], bytes[23]]);
        Some(Self {
            event_type,
            code,
            value,
        })
    }
}

fn bit_test(bits: &[u8], bit: usize) -> bool {
    bits.get(bit / 8)
        .is_some_and(|byte| byte & (1 << (bit % 8)) != 0)
}

/// Read the EV type bitmap via `EVIOCGBIT(0, …)`.
fn read_event_types(fd: RawFd) -> io::Result<[u8; EV_MAX / 8 + 1]> {
    let mut bits = [0_u8; EV_MAX / 8 + 1];
    // SAFETY: fd is valid; bits is a writable buffer of the advertised length.
    if unsafe {
        libc::ioctl(
            fd,
            eviocgbit(0, bits.len()) as libc::c_ulong,
            bits.as_mut_ptr(),
        )
    } < 0
    {
        return Err(io::Error::last_os_error());
    }
    Ok(bits)
}

/// Read the EV_KEY capability bitmap via `EVIOCGBIT(EV_KEY, …)`.
fn read_key_bits(fd: RawFd) -> io::Result<[u8; KEY_MAX / 8 + 1]> {
    let mut bits = [0_u8; KEY_MAX / 8 + 1];
    // SAFETY: fd is valid; bits is a writable buffer of the advertised length.
    if unsafe {
        libc::ioctl(
            fd,
            eviocgbit(EV_KEY, bits.len()) as libc::c_ulong,
            bits.as_mut_ptr(),
        )
    } < 0
    {
        return Err(io::Error::last_os_error());
    }
    Ok(bits)
}

/// An opened evdev device and the keys it currently holds down.
struct Device {
    file: File,
    keyboard: bool,
    pointer: bool,
    pressed: Vec<u16>,
}

impl Device {
    fn open(path: &Path) -> io::Result<Self> {
        // Non-blocking: the monitor drains devices only after poll(2) reports
        // readability, and must never wedge on a stall.
        let file = OpenOptions::new()
            .read(true)
            .custom_flags(libc::O_NONBLOCK | libc::O_CLOEXEC)
            .open(path)?;
        let fd = file.as_raw_fd();
        let event_types = read_event_types(fd)?;
        if !bit_test(&event_types, EV_KEY as usize) {
            return Err(io::Error::new(
                io::ErrorKind::Unsupported,
                "device does not report EV_KEY",
            ));
        }
        let key_bits = read_key_bits(fd)?;
        // Keyboard: the device can produce at least one key we forward. This
        // is wider than udev's `ID_INPUT_KEYBOARD` heuristic on purpose — a
        // JIS-only or single-key virtual device is still a keyboard for the
        // broker. (Power/sleep buttons at 116+ fall outside and are ignored.)
        let keyboard = (1..=KEY_FORWARD_MAX).any(|code| bit_test(&key_bits, code as usize));
        let pointer = (BTN_LEFT..=BTN_EXTRA).any(|code| bit_test(&key_bits, code as usize));
        if !keyboard && !pointer {
            return Err(io::Error::new(
                io::ErrorKind::Unsupported,
                "device is neither keyboard nor pointer",
            ));
        }
        Ok(Self {
            file,
            keyboard,
            pointer,
            pressed: Vec::new(),
        })
    }

    /// Reads all currently available events (the fd is non-blocking).
    /// Returns the events to forward, or `None` when the device is gone.
    fn drain(&mut self, hub: &Hub) -> Option<()> {
        loop {
            let mut frame = [0_u8; RAW_EVENT_SIZE];
            match self.file.read(&mut frame) {
                Ok(0) => return Some(()), // no more data for now
                Ok(count) if count == RAW_EVENT_SIZE => {
                    let raw = RawEvent::parse(&frame)?;
                    if raw.event_type != EV_KEY || !is_forwarded_key(raw.code) {
                        continue;
                    }
                    // The five mouse buttons ride the EV_KEY stream but are a
                    // distinct wire message (contract §3); everything else is
                    // a keyboard key.
                    let event = |value: u16| {
                        if (BTN_LEFT..=BTN_EXTRA).contains(&raw.code) {
                            SourceEvent::Button {
                                code: raw.code,
                                value,
                            }
                        } else {
                            SourceEvent::Key {
                                code: raw.code,
                                value,
                            }
                        }
                    };
                    match raw.value {
                        0 => {
                            self.pressed.retain(|&code| code != raw.code);
                            hub.push(event(proto::EVDEV_VALUE_RELEASE));
                        }
                        1 => {
                            if !self.pressed.contains(&raw.code) {
                                self.pressed.push(raw.code);
                            }
                            hub.push(event(proto::EVDEV_VALUE_PRESS));
                        }
                        2 => {
                            hub.push(event(proto::EVDEV_VALUE_REPEAT));
                        }
                        other => {
                            // Tolerant: unknown evdev value (contract §3 keeps
                            // the value field open for repeat-like states).
                            hub.push(event(u16::try_from(other).ok().unwrap_or(0)));
                        }
                    }
                }
                Ok(count) => {
                    // Short read: kernel and userspace disagree on the event
                    // layout; that device is unusable for us.
                    eprintln!("battuta-inputd: short evdev read ({count} bytes), ignoring device");
                    return None;
                }
                Err(error) if error.kind() == io::ErrorKind::WouldBlock => return Some(()),
                Err(error)
                    if error.kind() == io::ErrorKind::NotFound
                        || error.raw_os_error() == Some(libc::ENODEV) =>
                {
                    return None; // unplugged
                }
                Err(error) => {
                    eprintln!("battuta-inputd: evdev read failed: {error}");
                    return None;
                }
            }
        }
    }
}

/// Discovers devices, forwards their events, and watches for hotplug. The
/// monitor runs for the broker's lifetime.
pub struct EvdevMonitor {
    /// Reserved for future shutdown signalling; the monitor currently runs
    /// for the broker's lifetime.
    _private: (),
}

/// Spawns the monitor thread. `devices_dir` defaults to `/dev/input` and is
/// injectable for tests.
pub fn spawn(hub: Arc<Hub>, devices_dir: PathBuf) -> std::io::Result<EvdevMonitor> {
    std::thread::Builder::new()
        .name("evdev-monitor".to_owned())
        .spawn(move || monitor_loop(hub, devices_dir))?;
    Ok(EvdevMonitor { _private: () })
}

fn scan_dir(dir: &Path) -> Vec<PathBuf> {
    let mut paths = Vec::new();
    let Ok(entries) = std::fs::read_dir(dir) else {
        return paths;
    };
    for entry in entries.flatten() {
        let name = entry.file_name();
        let name = name.to_string_lossy();
        if name.starts_with("event") && name[5..].bytes().all(|byte| byte.is_ascii_digit()) {
            paths.push(entry.path());
        }
    }
    paths.sort();
    paths
}

fn remove_device(hub: &Hub, devices: &mut HashMap<PathBuf, Device>, path: &Path) {
    if let Some(device) = devices.remove(path) {
        for code in device.pressed {
            hub.push(SourceEvent::Key {
                code,
                value: proto::EVDEV_VALUE_RELEASE,
            });
        }
        hub.push(SourceEvent::Reset {
            reason: proto::ResetReason::DeviceChange,
        });
        eprintln!("battuta-inputd: device removed: {}", path.display());
    }
}

fn monitor_loop(hub: Arc<Hub>, devices_dir: PathBuf) {
    let mut devices: HashMap<PathBuf, Device> = HashMap::new();
    let inotify = match Inotify::new(&devices_dir) {
        Ok(inotify) => Some(inotify),
        Err(error) => {
            eprintln!(
                "battuta-inputd: inotify unavailable ({error}); hotplug disabled, \
                 rescanning every 5 seconds"
            );
            None
        }
    };

    scan_into(&hub, &mut devices, &devices_dir);
    eprintln!(
        "battuta-inputd: monitoring {} evdev device(s) in {}",
        devices.len(),
        devices_dir.display()
    );

    let mut rescan_deadline: Option<Instant> = Some(Instant::now() + RESYNC_INTERVAL);
    loop {
        // Snapshot the poll order so revents can be matched back to paths.
        // Entries with a closed/failing fd are kept out of the pollset but
        // remembered, so removal handling stays position-stable.
        let order: Vec<PathBuf> = devices.keys().cloned().collect();
        let mut pollset: Vec<libc::pollfd> = order
            .iter()
            .filter_map(|path| devices.get(path))
            .map(|device| libc::pollfd {
                fd: device.file.as_raw_fd(),
                events: libc::POLLIN,
                revents: 0,
            })
            .collect();
        let inotify_index = pollset.len();
        if let Some(inotify) = &inotify {
            pollset.push(libc::pollfd {
                fd: inotify.fd,
                events: libc::POLLIN,
                revents: 0,
            });
        }

        let timeout_ms = if inotify.is_some() {
            // inotify drives discovery, but poll still wakes at the resync
            // deadline so the periodic safety-net resync runs.
            rescan_deadline.map_or(-1, |deadline| {
                deadline
                    .saturating_duration_since(Instant::now())
                    .as_millis()
                    .min(i32::MAX as u128) as i32
            })
        } else {
            match rescan_deadline {
                Some(deadline) => deadline
                    .saturating_duration_since(Instant::now())
                    .as_millis()
                    .min(i32::MAX as u128) as i32,
                None => 5_000,
            }
        };
        // SAFETY: pollset is a valid array of pollfd entries.
        let ready = unsafe { libc::poll(pollset.as_mut_ptr(), pollset.len() as u64, timeout_ms) };
        if ready < 0 {
            let error = io::Error::last_os_error();
            if error.kind() != io::ErrorKind::Interrupted {
                eprintln!("battuta-inputd: poll failed: {error}");
                std::thread::sleep(Duration::from_millis(200));
            }
            continue;
        }

        // Inotify first so removals are reconciled before draining devices.
        let inotify_ready = inotify_index < pollset.len()
            && pollset[inotify_index].revents & (libc::POLLIN | libc::POLLHUP | libc::POLLERR) != 0;
        if let (true, Some(inotify)) = (inotify_ready, inotify.as_ref()) {
            inotify.drain();
            sync_with_directory(&hub, &mut devices, &devices_dir);
        }

        for (index, path) in order.iter().enumerate() {
            let Some(entry) = pollset.get(index) else {
                continue;
            };
            if entry.revents == 0 {
                continue;
            }
            if entry.revents & (libc::POLLHUP | libc::POLLERR | libc::POLLNVAL) != 0 {
                remove_device(&hub, &mut devices, path);
                continue;
            }
            if entry.revents & libc::POLLIN == 0 {
                continue;
            }
            let Some(device) = devices.get_mut(path) else {
                continue;
            };
            if device.drain(&hub).is_none() {
                remove_device(&hub, &mut devices, path);
            }
        }

        // Periodic reconciliation: mandatory without inotify, safety net
        // with it (recovers devices skipped for unreadable permissions).
        let now = Instant::now();
        if rescan_deadline.is_none_or(|deadline| now >= deadline) {
            sync_with_directory(&hub, &mut devices, &devices_dir);
            rescan_deadline = Some(now + RESYNC_INTERVAL);
        }
    }
}

/// Opens devices found in `dir` that are not tracked yet; does not remove
/// stale entries.
fn scan_into(_hub: &Hub, devices: &mut HashMap<PathBuf, Device>, dir: &Path) {
    for path in scan_dir(dir) {
        if devices.contains_key(&path) {
            continue;
        }
        match Device::open(&path) {
            Ok(device) => {
                eprintln!(
                    "battuta-inputd: device added: {} (keyboard: {}, pointer: {})",
                    path.display(),
                    device.keyboard,
                    device.pointer
                );
                devices.insert(path, device);
            }
            Err(error) if error.kind() == io::ErrorKind::PermissionDenied => {
                // Expected for devices outside the broker user's permissions;
                // the periodic resync retries them after udev grants access.
            }
            Err(error) if error.kind() == io::ErrorKind::Unsupported => {
                // Non-key/pointer device (power button without EV_KEY keys we
                // forward, tablet, joystick, …): intentionally skipped.
            }
            Err(error) if error.kind() == io::ErrorKind::NotFound => {
                // Disappeared between readdir and open.
            }
            Err(error) => {
                eprintln!(
                    "battuta-inputd: skipping device {}: {error}",
                    path.display()
                );
            }
        }
    }
}

/// Full reconciliation with the directory: adds new devices and synthesizes
/// removals for vanished ones.
fn sync_with_directory(hub: &Hub, devices: &mut HashMap<PathBuf, Device>, dir: &Path) {
    let present: Vec<PathBuf> = scan_dir(dir);
    let vanished: Vec<PathBuf> = devices
        .keys()
        .filter(|path| !present.contains(path))
        .cloned()
        .collect();
    for path in vanished {
        remove_device(hub, devices, &path);
    }
    scan_into(hub, devices, dir);
}

/// Minimal inotify wrapper for the devices directory.
struct Inotify {
    fd: RawFd,
}

impl Inotify {
    fn new(dir: &Path) -> io::Result<Self> {
        // SAFETY: plain syscalls; the fd is owned by this wrapper.
        let fd = unsafe { libc::inotify_init1(libc::IN_CLOEXEC | libc::IN_NONBLOCK) };
        if fd < 0 {
            return Err(io::Error::last_os_error());
        }
        let dir = match std::ffi::CString::new(dir.as_os_str().as_encoded_bytes()) {
            Ok(dir) => dir,
            Err(_) => {
                // SAFETY (of unwind path): fd must be closed on failure.
                unsafe { libc::close(fd) };
                return Err(io::Error::new(
                    io::ErrorKind::InvalidInput,
                    "devices path is not valid UTF-8/CString",
                ));
            }
        };
        let watch = unsafe {
            libc::inotify_add_watch(
                fd,
                dir.as_ptr(),
                libc::IN_CREATE
                    | libc::IN_DELETE
                    | libc::IN_MOVED_TO
                    | libc::IN_MOVED_FROM
                    | libc::IN_ATTRIB,
            )
        };
        if watch < 0 {
            let error = io::Error::last_os_error();
            // SAFETY: we own fd and must not leak it.
            unsafe { libc::close(fd) };
            return Err(error);
        }
        Ok(Self { fd })
    }

    /// Reads and discards pending notifications; callers rescan the directory
    /// instead of interpreting individual events (simple and race-free).
    fn drain(&self) {
        let mut buffer = [0_u8; 4096];
        loop {
            // SAFETY: buffer is a valid writable region for read(2).
            let count = unsafe { libc::read(self.fd, buffer.as_mut_ptr().cast(), buffer.len()) };
            if count <= 0 {
                return;
            }
        }
    }
}

impl Drop for Inotify {
    fn drop(&mut self) {
        // SAFETY: we own the inotify fd.
        unsafe { libc::close(self.fd) };
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn forwarded_codes_are_exactly_keys_and_five_buttons() {
        assert!(is_forwarded_key(1)); // KEY_ESC
        assert!(is_forwarded_key(30)); // KEY_A
        assert!(is_forwarded_key(0xFF)); // last KEY_* range code
        assert!(is_forwarded_key(0x110)); // BTN_LEFT
        assert!(is_forwarded_key(0x114)); // BTN_EXTRA
        assert!(!is_forwarded_key(0x115)); // BTN_FORWARD: not one of the five
        assert!(!is_forwarded_key(0x117)); // BTN_TASK: not one of the five
        assert!(!is_forwarded_key(0x120)); // joystick block
        assert!(!is_forwarded_key(0x2ff)); // extended key block: not forwarded
    }

    #[test]
    fn raw_event_parses_kernel_layout() {
        let mut frame = [0_u8; RAW_EVENT_SIZE];
        frame[16..18].copy_from_slice(&EV_KEY.to_ne_bytes());
        frame[18..20].copy_from_slice(&30_u16.to_ne_bytes());
        frame[20..24].copy_from_slice(&1_i32.to_ne_bytes());
        let event = RawEvent::parse(&frame).expect("parse");
        assert_eq!(event.event_type, EV_KEY);
        assert_eq!(event.code, 30);
        assert_eq!(event.value, 1);
        assert!(RawEvent::parse(&frame[..4]).is_none());
    }

    #[test]
    fn short_read_is_detected() {
        let frame = [0_u8; RAW_EVENT_SIZE - 1];
        assert!(RawEvent::parse(&frame).is_none());
    }
}
