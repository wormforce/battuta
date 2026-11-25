# battuta RPM spec (Linux port PR 10).
#
# scripts/build-linux-package.sh substitutes @RELEASE_VERSION@, stages the
# FHS payload under %_builddir/battuta-root, and builds with rpmbuild -bb.
# The broker is an on-demand systemd socket service: %post enables the
# socket unit, not the service.

Name:           battuta
Version:        @RPM_VERSION@
Release:        @RPM_RELEASE@%{?dist}
Summary:        Keyboard and pointer sound effects with typing statistics
License:        MIT
URL:            https://github.com/wormforce/battuta
ExclusiveArch:  x86_64
# The pre-assembled FHS payload produced by scripts/build-linux-package.sh.
Source0:        battuta-payload.tar.gz

Requires:       glibc >= 2.31, libstdc++, fontconfig, libxcb, libxkbcommon, libxkbcommon-x11, vulkan-loader
Recommends:     systemd, polkit, pipewire, shared-mime-info, xdg-desktop-portal
Suggests:       ffmpeg

%description
Battuta plays realistic mechanical keyboard and pointer sounds for every
physical keypress, entirely on the local machine, and records optional
typing statistics (daily trends, per-key heatmaps, per-app identity).

Ships the battuta-inputd system broker as an on-demand systemd socket
service: the per-connection polkit action
io.github.wormforce.Battuta.Input1.subscribe authorizes the desktop
session (the broker runs as root so it is a trusted polkit caller for the
client's subject), and only physical key identities cross the socket — no
characters, no pointer coordinates, no window contents.

The UI runs natively on Wayland (X11 fallback) with PipeWire audio.

%install
rm -rf %{buildroot}
mkdir -p %{buildroot}
tar xzf %{SOURCE0} -C %{buildroot}

%pre
getent passwd battuta-input >/dev/null 2>&1 || \
    useradd -r -d /nonexistent -s /sbin/nologin \
        -c "Battuta input broker" battuta-input || true

%post
if [ -d /run/systemd/system ]; then
    systemctl daemon-reload >/dev/null 2>&1 || true
    systemctl enable --now battuta-inputd.socket >/dev/null 2>&1 || true
fi
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database -q /usr/share/applications || true
fi
if command -v update-mime-database >/dev/null 2>&1; then
    update-mime-database /usr/share/mime >/dev/null 2>&1 || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -qf /usr/share/icons/hicolor >/dev/null 2>&1 || true
fi

%preun
if [ "$1" -eq 0 ] && [ -d /run/systemd/system ]; then
    systemctl --no-reload disable --now battuta-inputd.socket battuta-inputd.service >/dev/null 2>&1 || true
fi

%postun
if [ -d /run/systemd/system ]; then
    systemctl daemon-reload >/dev/null 2>&1 || true
fi
# $1 == 0 means final removal (not upgrade): drop the broker identity so no
# input-read capable account lingers. User data is intentionally untouched.
if [ "$1" -eq 0 ]; then
    getent passwd battuta-input >/dev/null 2>&1 && userdel battuta-input || true
fi

%files
%dir /usr/share/doc/battuta
%license /usr/share/doc/battuta/LICENSE
%doc /usr/share/doc/battuta/THIRD_PARTY_NOTICES.md
%doc /usr/share/doc/battuta/PRIVACY.md
%doc /usr/share/doc/battuta/AUDIO_SOURCES.md
%doc /usr/share/doc/battuta/SOUND_PACK_FORMAT.md
%dir /usr/lib/battuta
/usr/lib/battuta/bin/*
/usr/bin/battuta
/usr/lib/systemd/system/battuta-inputd.service
/usr/lib/systemd/system/battuta-inputd.socket
/usr/lib/sysusers.d/battuta-inputd.conf
/usr/lib/tmpfiles.d/battuta-inputd.conf
/usr/share/polkit-1/actions/io.github.wormforce.Battuta.Input1.policy
/usr/share/dbus-1/system.d/io.github.wormforce.Battuta.Input1.conf
/usr/share/applications/io.github.wormforce.Battuta.desktop
/usr/share/metainfo/io.github.wormforce.Battuta.metainfo.xml
/usr/share/mime/packages/io.github.wormforce.Battuta.xml
/usr/share/icons/hicolor/*/apps/io.github.wormforce.Battuta.png

%changelog
* Sat Aug 30 2026 Battuta contributors <battuta@users.noreply.github.com> - @RPM_VERSION@-@RPM_RELEASE@
- Linux v1 release candidate: Wayland UI, on-demand input broker, PipeWire audio.
