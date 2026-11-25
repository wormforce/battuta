//! Minimal AF_UNIX `SOCK_SEQPACKET` wrappers. Each `recv` returns exactly one
//! protocol frame, which is the transport the wire contract assumes.

use std::io;
use std::os::fd::{AsRawFd, FromRawFd, OwnedFd};
use std::os::unix::fs::FileTypeExt;
use std::path::Path;
use std::time::Duration;

fn sockaddr_un(path: &Path) -> io::Result<(libc::sockaddr_un, libc::socklen_t)> {
    let mut address = libc::sockaddr_un {
        sun_family: libc::AF_UNIX as libc::sa_family_t,
        sun_path: [0; 108],
    };

    let bytes = path.as_os_str().as_encoded_bytes();
    if bytes.len() >= address.sun_path.len() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "socket path exceeds sockaddr_un capacity",
        ));
    }
    for (index, byte) in bytes.iter().enumerate() {
        address.sun_path[index] = *byte as libc::c_char;
    }

    let length = (std::mem::size_of::<libc::sa_family_t>() + bytes.len()) as libc::socklen_t;
    Ok((address, length))
}

fn unix_socket() -> io::Result<OwnedFd> {
    let fd = unsafe { libc::socket(libc::AF_UNIX, libc::SOCK_SEQPACKET | libc::SOCK_CLOEXEC, 0) };
    if fd < 0 {
        return Err(io::Error::last_os_error());
    }

    // SAFETY: fd is a freshly created, owned socket descriptor.
    Ok(unsafe { OwnedFd::from_raw_fd(fd) })
}

/// A listening SEQPACKET socket. Only one broker binds a given path.
pub struct SeqPacketListener {
    fd: OwnedFd,
}

impl SeqPacketListener {
    /// Binds and listens. A stale socket file at `path` is removed first.
    pub fn bind(path: &Path) -> io::Result<Self> {
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }

        // A leftover socket file from a crashed broker would make bind fail.
        match std::fs::symlink_metadata(path) {
            Ok(metadata) if metadata.file_type().is_socket() => {
                std::fs::remove_file(path)?;
            }
            Ok(_) => {
                return Err(io::Error::new(
                    io::ErrorKind::AlreadyExists,
                    format!("{} exists and is not a socket", path.display()),
                ));
            }
            Err(error) if error.kind() == io::ErrorKind::NotFound => {}
            Err(error) => return Err(error),
        }

        let fd = unix_socket()?;
        let (address, length) = sockaddr_un(path)?;
        let result = unsafe {
            libc::bind(
                fd.as_raw_fd(),
                &address as *const libc::sockaddr_un as *const libc::sockaddr,
                length,
            )
        };
        if result < 0 {
            return Err(io::Error::last_os_error());
        }

        if unsafe { libc::listen(fd.as_raw_fd(), 8) } < 0 {
            return Err(io::Error::last_os_error());
        }

        Ok(Self { fd })
    }

    /// Adopts a pre-bound listening socket, typically the systemd socket
    /// activation descriptor. Validates the socket type so a misconfigured
    /// unit (e.g. plain `ListenStream=` instead of `ListenSequentialPacket=`)
    /// fails loudly at startup instead of corrupting the frame protocol.
    pub fn from_owned_fd(fd: OwnedFd) -> io::Result<Self> {
        let mut socket_type: libc::c_int = 0;
        let mut length = std::mem::size_of::<libc::c_int>() as libc::socklen_t;
        if unsafe {
            libc::getsockopt(
                fd.as_raw_fd(),
                libc::SOL_SOCKET,
                libc::SO_TYPE,
                &mut socket_type as *mut libc::c_int as *mut libc::c_void,
                &mut length,
            )
        } != 0
        {
            return Err(io::Error::last_os_error());
        }

        if socket_type != libc::SOCK_SEQPACKET {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                format!("activated socket has type {socket_type:#x}, expected SOCK_SEQPACKET"),
            ));
        }

        Ok(Self { fd })
    }

    /// Blocks until a client connects.
    pub fn accept(&self) -> io::Result<SeqPacketStream> {
        let fd = unsafe {
            libc::accept4(
                self.fd.as_raw_fd(),
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                libc::SOCK_CLOEXEC,
            )
        };
        if fd < 0 {
            return Err(io::Error::last_os_error());
        }

        // SAFETY: fd is an owned connection descriptor on success.
        Ok(SeqPacketStream {
            fd: unsafe { OwnedFd::from_raw_fd(fd) },
        })
    }
}

/// One connected client.
pub struct SeqPacketStream {
    fd: OwnedFd,
}

impl SeqPacketStream {
    /// Connects to a broker SEQPACKET socket.
    pub fn connect(path: &Path) -> io::Result<Self> {
        let fd = unix_socket()?;
        let (address, length) = sockaddr_un(path)?;
        let result = unsafe {
            libc::connect(
                fd.as_raw_fd(),
                &address as *const libc::sockaddr_un as *const libc::sockaddr,
                length,
            )
        };
        if result < 0 {
            return Err(io::Error::last_os_error());
        }

        Ok(Self { fd })
    }

    /// Receives one packet (one protocol frame) into `buffer`.
    pub fn recv(&self, buffer: &mut [u8]) -> io::Result<usize> {
        let count = unsafe {
            libc::recv(
                self.fd.as_raw_fd(),
                buffer.as_mut_ptr() as *mut libc::c_void,
                buffer.len(),
                0,
            )
        };
        if count < 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(count as usize)
    }

    /// Receives one packet without blocking. `Ok(None)` means nothing was
    /// queued at this instant. This is distinct from a zero `SO_RCVTIMEO`:
    /// a zero timeout *disables* the timeout (blocking forever), it does not
    /// make the socket non-blocking.
    pub fn try_recv(&self, buffer: &mut [u8]) -> io::Result<Option<usize>> {
        let count = unsafe {
            libc::recv(
                self.fd.as_raw_fd(),
                buffer.as_mut_ptr() as *mut libc::c_void,
                buffer.len(),
                libc::MSG_DONTWAIT,
            )
        };
        if count < 0 {
            let error = io::Error::last_os_error();
            if error.kind() == io::ErrorKind::WouldBlock {
                return Ok(None);
            }
            return Err(error);
        }
        Ok(Some(count as usize))
    }

    /// Sends one packet. `MSG_NOSIGNAL` surfaces peer disconnects as EPIPE
    /// instead of killing the process.
    pub fn send(&self, bytes: &[u8]) -> io::Result<usize> {
        let count = unsafe {
            libc::send(
                self.fd.as_raw_fd(),
                bytes.as_ptr() as *const libc::c_void,
                bytes.len(),
                libc::MSG_NOSIGNAL,
            )
        };
        if count < 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(count as usize)
    }

    pub fn set_recv_timeout(&self, timeout: Option<Duration>) -> io::Result<()> {
        let value = libc::timeval {
            tv_sec: timeout.map_or(0, |value| value.as_secs() as libc::time_t),
            tv_usec: timeout.map_or(0, |value| value.subsec_micros() as libc::suseconds_t),
        };
        let result = unsafe {
            libc::setsockopt(
                self.fd.as_raw_fd(),
                libc::SOL_SOCKET,
                libc::SO_RCVTIMEO,
                &value as *const libc::timeval as *const libc::c_void,
                std::mem::size_of::<libc::timeval>() as libc::socklen_t,
            )
        };
        if result < 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(())
    }

    /// Credentials of the connected peer via SO_PEERCRED (Linux).
    pub fn peer_credentials(&self) -> io::Result<crate::auth::PeerCredentials> {
        let mut credentials = libc::ucred {
            pid: 0,
            uid: 0,
            gid: 0,
        };
        let mut length = std::mem::size_of::<libc::ucred>() as libc::socklen_t;
        let result = unsafe {
            libc::getsockopt(
                self.fd.as_raw_fd(),
                libc::SOL_SOCKET,
                libc::SO_PEERCRED,
                &mut credentials as *mut libc::ucred as *mut libc::c_void,
                &mut length,
            )
        };
        if result < 0 {
            return Err(io::Error::last_os_error());
        }

        Ok(crate::auth::PeerCredentials {
            pid: credentials.pid as u32,
            uid: credentials.uid,
            gid: credentials.gid,
        })
    }
}
