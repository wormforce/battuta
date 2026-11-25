//! Socket permission model. The broker listens on a Unix seqpacket socket
//! whose reachability is the first (coarse) layer of access control; the
//! SO_PEERCRED + logind/polkit gate is the authoritative layer. Tight
//! defaults here keep the socket out of reach of unrelated users and make
//! the packaged deployment (dedicated group) a matter of configuration, not
//! code.

use std::ffi::CString;
use std::io;
use std::path::Path;

/// Parses an octal permission string ("0660", "660", "0o660").
pub fn parse_mode(text: &str) -> Result<u32, String> {
    let trimmed = text.trim().trim_start_matches("0o").trim_start_matches('0');
    let mode = if trimmed.is_empty() {
        0
    } else {
        u32::from_str_radix(trimmed, 8).map_err(|_| format!("invalid octal mode {text:?}"))?
    };
    if mode > 0o777 {
        return Err(format!("mode {text:?} exceeds 0o777"));
    }
    Ok(mode)
}

/// Resolves a group spec: numeric GID or `/etc/group` name.
pub fn resolve_group(spec: &str) -> Result<u32, String> {
    if let Ok(gid) = spec.parse::<u32>() {
        return Ok(gid);
    }
    let name = CString::new(spec).map_err(|_| format!("invalid group name {spec:?}"))?;
    let mut buffer_length = 1024_usize;
    loop {
        let mut buffer = vec![0_u8; buffer_length];
        let mut group_entry: libc::group = unsafe { std::mem::zeroed() };
        let mut result: *mut libc::group = std::ptr::null_mut();
        let status = unsafe {
            libc::getgrnam_r(
                name.as_ptr(),
                &mut group_entry,
                buffer.as_mut_ptr().cast(),
                buffer_length,
                &mut result,
            )
        };
        if status == 0 {
            if result.is_null() {
                return Err(format!("group {spec:?} not found"));
            }
            // SAFETY: result points into group_entry, which is still alive.
            return Ok(unsafe { (*result).gr_gid });
        }
        if status == libc::ERANGE {
            buffer_length *= 2;
            continue;
        }
        return Err(format!("getgrnam_r({spec}) failed with status {status}"));
    }
}

/// Applies file mode and (optionally) group ownership to the socket node
/// right after bind, before any client can connect to it.
pub fn apply(socket_path: &Path, mode: u32, group: Option<&str>) -> io::Result<()> {
    use std::os::unix::fs::PermissionsExt;
    std::fs::set_permissions(socket_path, std::fs::Permissions::from_mode(mode))?;
    if let Some(group) = group {
        let gid = resolve_group(group).map_err(io::Error::other)?;
        // SAFETY: path is a valid C string; uid = -1 leaves the owner
        // unchanged.
        let c_path = CString::new(socket_path.as_os_str().as_encoded_bytes().to_vec())
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidInput, "socket path contains NUL"))?;
        if unsafe { libc::chown(c_path.as_ptr(), u32::MAX as libc::uid_t, gid as libc::gid_t) } != 0
        {
            return Err(io::Error::last_os_error());
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_octal_modes() {
        assert_eq!(parse_mode("0660").unwrap(), 0o660);
        assert_eq!(parse_mode("660").unwrap(), 0o660);
        assert_eq!(parse_mode("0o660").unwrap(), 0o660);
        assert_eq!(parse_mode("600").unwrap(), 0o600);
        assert_eq!(parse_mode("0").unwrap(), 0);
        assert!(parse_mode("07777").is_err(), "beyond file permissions");
        assert!(parse_mode("0880").is_err(), "not octal");
        assert!(parse_mode("abc").is_err());
    }

    #[test]
    fn resolves_numeric_and_named_groups() {
        assert_eq!(resolve_group("0").unwrap(), 0);
        assert_eq!(resolve_group("root").unwrap(), 0);
        assert!(resolve_group("no-such-group-battuta").is_err());
    }
}
