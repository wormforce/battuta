//! Virtual uinput device helper.
//!
//! Used by integration tests to synthesize a real evdev device node that the
//! broker reads through the normal EV_KEY path, and available for future
//! self-verification tooling. Requires write access to `/dev/uinput`.

use std::fs::{File, OpenOptions};
use std::io::{self, Write};
use std::os::fd::AsRawFd;
use std::path::Path;

/// `EV_SYN`.
pub const EV_SYN: u16 = 0x00;
/// `EV_KEY`.
pub const EV_KEY: u16 = 0x01;
/// `BUS_USB` for the virtual device identity.
const BUS_USB: u16 = 0x03;
/// `ABS_CNT`: size of the per-axis arrays in `uinput_user_dev`.
const ABS_CNT: usize = 64;
/// `UINPUT_IOCTL_BASE` (`'U'`).
const UINPUT_IOCTL_BASE: u16 = 0x55;

const fn ioc(dir: u32, ty: u16, nr: u16, size: u16) -> u64 {
    ((dir as u64) << 30) | ((size as u64) << 16) | ((ty as u64) << 8) | (nr as u64)
}

/// `_IOW('U', 100, int)`.
const UI_SET_EVBIT: u64 = ioc(1, UINPUT_IOCTL_BASE, 100, 4);
/// `_IOW('U', 101, int)`.
const UI_SET_KEYBIT: u64 = ioc(1, UINPUT_IOCTL_BASE, 101, 4);
/// `_IO('U', 1)`.
const UI_DEV_CREATE: u64 = ioc(0, UINPUT_IOCTL_BASE, 1, 0);
/// `_IO('U', 2)`.
const UI_DEV_DESTROY: u64 = ioc(0, UINPUT_IOCTL_BASE, 2, 0);

/// Mirror of `struct uinput_user_dev` (the legacy write-based setup payload).
#[repr(C)]
struct UinputUserDev {
    name: [u8; 80],
    bustype: u16,
    vendor: u16,
    product: u16,
    version: u16,
    ff_effects_max: u32,
    absmax: [u32; ABS_CNT],
    absmin: [u32; ABS_CNT],
    absfuzz: [u32; ABS_CNT],
    absflat: [u32; ABS_CNT],
}

impl UinputUserDev {
    fn new(name: &[u8]) -> Self {
        let mut name_bytes = [0_u8; 80];
        let copied = name.len().min(name_bytes.len() - 1);
        name_bytes[..copied].copy_from_slice(&name[..copied]);
        Self {
            name: name_bytes,
            bustype: BUS_USB,
            vendor: 0x1F00,
            product: 0x0001,
            version: 1,
            ff_effects_max: 0,
            absmax: [0; ABS_CNT],
            absmin: [0; ABS_CNT],
            absfuzz: [0; ABS_CNT],
            absflat: [0; ABS_CNT],
        }
    }
}

/// A live virtual input device. Destroying it (drop) issues `UI_DEV_DESTROY`
/// and closes the node, which the broker observes as device removal.
pub struct UinputDevice {
    file: File,
    destroyed: bool,
}

impl UinputDevice {
    /// Creates a virtual device that reports the given key/button codes.
    /// Buttons (`BTN_*`, 0x110..=0x114) are part of the same EV_KEY bitmap.
    pub fn create_keyboard(keys: &[u16]) -> io::Result<Self> {
        Self::create_named("battuta-uinput", keys)
    }

    /// Same as [`Self::create_keyboard`] with an explicit device name.
    pub fn create_named(name: &str, keys: &[u16]) -> io::Result<Self> {
        Self::create_at(Path::new("/dev/uinput"), name, keys)
    }

    /// Opens a specific uinput control node (tests may point at a mock).
    pub fn create_at(path: &Path, name: &str, keys: &[u16]) -> io::Result<Self> {
        let mut file = OpenOptions::new().read(true).write(true).open(path)?;
        let fd = file.as_raw_fd();
        let setup = UinputUserDev::new(name.as_bytes());
        let request = UI_SET_EVBIT as libc::c_ulong;
        for (evbit, name) in [(EV_SYN, "EV_SYN"), (EV_KEY, "EV_KEY")] {
            // SAFETY: fd is valid; the third argument is the requested bit.
            if unsafe { libc::ioctl(fd, request, evbit as libc::c_int) } < 0 {
                return Err(io::Error::last_os_error()
                    .into_io_error_with(format!("UI_SET_EVBIT({name}) failed")));
            }
        }
        for &code in keys {
            // SAFETY: fd is valid; the third argument is the requested key bit.
            if unsafe { libc::ioctl(fd, UI_SET_KEYBIT as libc::c_ulong, code as libc::c_int) } < 0 {
                return Err(io::Error::last_os_error()
                    .into_io_error_with(format!("UI_SET_KEYBIT({code:#x}) failed")));
            }
        }
        let payload = unsafe {
            // SAFETY: `UinputUserDev` is a plain-old-data `#[repr(C)]` mirror of
            // the kernel layout; we only read its bytes.
            std::slice::from_raw_parts(
                (&raw const setup) as *const u8,
                std::mem::size_of::<UinputUserDev>(),
            )
        };
        file.write_all(payload)?;
        // SAFETY: fd is valid and the device has been fully configured.
        if unsafe { libc::ioctl(fd, UI_DEV_CREATE as libc::c_ulong) } < 0 {
            return Err(
                io::Error::last_os_error().into_io_error_with("UI_DEV_CREATE failed".to_owned())
            );
        }
        Ok(Self {
            file,
            destroyed: false,
        })
    }

    /// Emits one event of the given type/code/value.
    pub fn emit(&self, event_type: u16, code: u16, value: i32) -> io::Result<()> {
        let mut frame = [0_u8; RAW_EVENT_SIZE];
        // struct input_event: timeval (2 x long), u16 type, u16 code, i32 value.
        frame[..8].copy_from_slice(&0_i64.to_ne_bytes());
        frame[8..16].copy_from_slice(&0_i64.to_ne_bytes());
        frame[16..18].copy_from_slice(&event_type.to_ne_bytes());
        frame[18..20].copy_from_slice(&code.to_ne_bytes());
        frame[20..24].copy_from_slice(&value.to_ne_bytes());
        // `&File` implements Write, so emission stays available on `&self`.
        (&self.file).write_all(&frame)?;
        // Pair every EV_KEY report with EV_SYN like real drivers do.
        if event_type == EV_KEY {
            let mut syn = [0_u8; RAW_EVENT_SIZE];
            syn[16..18].copy_from_slice(&EV_SYN.to_ne_bytes());
            (&self.file).write_all(&syn)?;
        }
        Ok(())
    }

    /// Emits a key press/release/repeat (`value` 1/0/2).
    pub fn emit_key(&self, code: u16, value: i32) -> io::Result<()> {
        self.emit(EV_KEY, code, value)
    }

    /// Explicitly destroys the device (`UI_DEV_DESTROY` + close). Dropping also
    /// destroys; this exists for tests that want ordered teardown.
    pub fn destroy(mut self) -> io::Result<()> {
        let result = self.destroy_internal();
        self.destroyed = true;
        result
    }

    fn destroy_internal(&self) -> io::Result<()> {
        // SAFETY: fd is valid and was used to create the device.
        if unsafe { libc::ioctl(self.file.as_raw_fd(), UI_DEV_DESTROY as libc::c_ulong) } < 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(())
    }
}

impl Drop for UinputDevice {
    fn drop(&mut self) {
        if !self.destroyed {
            let _ = self.destroy_internal();
        }
    }
}

/// Byte size of `struct input_event` on this platform (timeval + type + code +
/// value; 24 bytes on 64-bit Linux).
pub const RAW_EVENT_SIZE: usize = std::mem::size_of::<usize>() * 2 + 8;

trait IntoIoErrorWith {
    fn into_io_error_with(self, context: String) -> io::Error;
}

impl IntoIoErrorWith for io::Error {
    fn into_io_error_with(self, context: String) -> io::Error {
        io::Error::new(self.kind(), format!("{context}: {self}"))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn uinput_user_dev_layout_matches_kernel_expectations() {
        // name[80] + input_id (8) + ff_effects_max (4) + 4 * ABS_CNT * 4.
        assert_eq!(
            std::mem::size_of::<UinputUserDev>(),
            80 + 8 + 4 + 4 * ABS_CNT * 4
        );
        assert_eq!(RAW_EVENT_SIZE, 24);
    }

    #[test]
    fn creation_requires_uinput_node() {
        // On CI without /dev/uinput this must fail gracefully, not panic.
        let result = UinputDevice::create_keyboard(&[30]);
        if let Err(error) = result {
            assert!(
                matches!(
                    error.kind(),
                    io::ErrorKind::NotFound | io::ErrorKind::PermissionDenied
                ),
                "unexpected error: {error}"
            );
        }
    }
}
