using System.IO;
using System.Xml.Linq;
using Battuta.Linux.Runtime;
using Battuta.TestSupport;
using Xunit;

namespace Battuta.Linux.Tests.Packaging;

/// <summary>
/// Packaging acceptance checks (Linux port PR 10): the files that ship
/// inside the deb/rpm must stay consistent with the code that consumes
/// them — socket paths, bus names, app ids, and the plan §5.2 hardening
/// baseline. These checks run against the repository sources, so a
/// packaging edit that breaks the broker or the app fails in tests, not
/// on a user's machine after install.
/// </summary>
public sealed class PackagingTests
{
    private static string RepoRoot => RepositoryTestFiles.FindRepositoryRoot();

    private static string Packaging(params string[] parts) =>
        Path.Combine([RepoRoot, "BattutaLinux", "packaging", .. parts]);

    private static string ReadPackaging(params string[] parts) => File.ReadAllText(Packaging(parts));

    [Fact]
    public void ServiceUnitUsesPackagedPathsAndHardening()
    {
        var unit = ReadPackaging("systemd", "battuta-inputd.service");

        // The broker must run as root: polkit only lets trusted callers
        // (uid 0, or the subject itself) run CheckAuthorization() for a
        // subject belonging to another identity, and the per-connection
        // gate checks the CLIENT's subject. A dedicated low-privilege
        // account cannot perform the gate at all.
        Assert.Contains("User=root", unit);
        Assert.Contains("Group=root", unit);
        Assert.DoesNotContain(unit, "SupplementaryGroups=input");
        Assert.Contains("ExecStart=/usr/lib/battuta/bin/battuta-inputd", unit);
        Assert.Contains("--socket-path /run/battuta-inputd/input.sock", unit);
        // Packaged fallback mode: the polkit gate, not filesystem bits,
        // decides who may connect (see the socket unit).
        Assert.Contains("--socket-mode 0666", unit);

        // Plan §5.2 hardening baseline.
        Assert.Contains("NoNewPrivileges=yes", unit);
        Assert.Contains("ProtectSystem=strict", unit);
        Assert.Contains("ProtectHome=yes", unit);
        Assert.Contains("PrivateTmp=yes", unit);
        Assert.Contains("DevicePolicy=closed", unit);
        Assert.Contains("DeviceAllow=char-input rw", unit);
        Assert.Contains("RestrictAddressFamilies=AF_UNIX AF_NETLINK", unit);
        Assert.Contains("RestrictNamespaces=yes", unit);
        Assert.Contains("LockPersonality=yes", unit);
        Assert.Contains("MemoryDenyWriteExecute=yes", unit);
        Assert.Contains("SystemCallFilter=@system-service", unit);
        Assert.Contains("RuntimeDirectory=battuta-inputd", unit);

        // On-demand activation: the service must not be independently
        // enableable; the socket unit carries the [Install] section.
        var directiveLines = unit.Split('\n')
            .Where(line => !line.TrimStart().StartsWith('#'))
            .ToArray();
        Assert.DoesNotContain(directiveLines, line => line.StartsWith("[Install]", StringComparison.Ordinal));
    }

    [Fact]
    public void SocketUnitMatchesBrokerAndClientContract()
    {
        var unit = ReadPackaging("systemd", "battuta-inputd.socket");

        // The broker validates SOCK_SEQPACKET before adopting fd 3; a plain
        // ListenStream= unit would abort every service start.
        Assert.Contains("ListenSequentialPacket=/run/battuta-inputd/input.sock", unit);
        Assert.Contains("SocketMode=0666", unit);
        Assert.Contains("RemoveOnStop=true", unit);
        Assert.Contains("WantedBy=sockets.target", unit);

        // The client defaults to exactly this path (packaged deployment).
        Assert.Equal(
            "/run/battuta-inputd/input.sock",
            BrokerInputSource.DefaultSocketPath);
    }

    [Fact]
    public void PolkitPolicyMatchesBrokerActionAndDefaults()
    {
        var policy = XDocument.Parse(ReadPackaging("polkit", "io.github.wormforce.Battuta.Input1.policy"));
        var action = policy.Descendants("action").Single();
        Assert.Equal("io.github.wormforce.Battuta.Input1.subscribe", (string?)action.Attribute("id"));

        var defaults = action.Element("defaults");
        Assert.NotNull(defaults);
        Assert.Equal("no", defaults.Element("allow_any")?.Value);
        Assert.Equal("no", defaults.Element("allow_inactive")?.Value);
        Assert.Equal("auth_self_keep", defaults.Element("allow_active")?.Value);

        // The message must keep disclosing that key sequences can reveal
        // typed text (plan §5.2 threat-model wording).
        Assert.Contains("physical key", action.Element("message")?.Value ?? "", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The D-Bus system policy must ship with the package: without it no
    /// identity may own io.github.wormforce.Battuta.Input1 and the broker
    /// dies on startup with RequestName AccessDenied (observed live on Arch).
    /// The packaged service runs as root, so root must be allowed to own the
    /// name; the sysusers account stays allowed for dev runs of the binary.
    /// </summary>
    [Fact]
    public void DbusPolicyLetsPackagedBrokerOwnItsBusName()
    {
        var policy = XDocument.Parse(ReadPackaging(
            "dbus-1", "system.d", "io.github.wormforce.Battuta.Input1.conf"));

        var ownNames = policy
            .Descendants("allow")
            .Where(element => (string?)element.Attribute("own") is not null)
            .Select(element => (string?)element.Attribute("own"))
            .ToArray();
        Assert.Contains("io.github.wormforce.Battuta.Input1", ownNames);

        var policyUsers = policy
            .Descendants("policy")
            .Where(element => element.Attribute("user") is not null)
            .Select(element => (string?)element.Attribute("user"))
            .ToArray();
        Assert.Contains("root", policyUsers);
        Assert.Contains("battuta-input", policyUsers);

        // Everyone else may talk to the broker (the polkit gate, not the
        // bus, decides authorization).
        var defaultSends = policy
            .Descendants("policy")
            .Single(element => (string?)element.Attribute("context") == "default")
            .Descendants("allow")
            .Select(element => (string?)element.Attribute("send_destination"))
            .ToArray();
        Assert.Contains("io.github.wormforce.Battuta.Input1", defaultSends);

        // And the build script must actually stage it into the payload.
        var script = File.ReadAllText(Path.Combine(
            RepoRoot, "BattutaLinux", "scripts", "build-linux-package.sh"));
        Assert.Contains("dbus-1/system.d/io.github.wormforce.Battuta.Input1.conf", script);
    }

    [Fact]
    public void DesktopEntryMatchesMetainfoAndWrapper()
    {
        var desktop = ReadPackaging("desktop", "io.github.wormforce.Battuta.desktop");
        Assert.Contains("Exec=/usr/bin/battuta", desktop);
        Assert.Contains("Icon=io.github.wormforce.Battuta", desktop);
        Assert.Contains("MimeType=application/x-simuboard-pack;", desktop);
        Assert.Contains("Name[zh_CN]=Battuta 键音", desktop);

        var metainfo = XDocument.Parse(ReadPackaging("appstream", "io.github.wormforce.Battuta.metainfo.xml"));
        var component = metainfo.Element("component");
        Assert.Equal("desktop-application", (string?)component?.Attribute("type"));
        Assert.Equal("io.github.wormforce.Battuta", component?.Element("id")?.Value);
        Assert.Equal(
            "io.github.wormforce.Battuta.desktop",
            component?.Element("launchable")?.Value);
    }

    [Fact]
    public void DebianScriptsDriveTheSocketUnit()
    {
        var control = ReadPackaging("debian", "control");
        Assert.Contains("Package: battuta", control);
        Assert.Contains("Architecture: amd64", control);
        Assert.Contains("libfontconfig1", control); // GPUI font discovery
        Assert.Contains("@RELEASE_VERSION@", control); // substituted at build time

        var postinst = ReadPackaging("debian", "postinst");
        Assert.Contains("systemd-sysusers", postinst);
        Assert.Contains("systemctl enable --now battuta-inputd.socket", postinst);

        var prerm = ReadPackaging("debian", "prerm");
        Assert.Contains("systemctl stop battuta-inputd.socket", prerm);

        var postrm = ReadPackaging("debian", "postrm");
        Assert.Contains("deluser", postrm);
        Assert.Contains("sockets.target.wants/battuta-inputd.socket", postrm);
    }

    [Fact]
    public void RpmSpecPackagesTheFullIntegrationSet()
    {
        var spec = ReadPackaging("rpm", "battuta.spec");
        Assert.Contains("Source0:        battuta-payload.tar.gz", spec);
        Assert.Contains("tar xzf %{SOURCE0} -C %{buildroot}", spec);
        Assert.Contains("/usr/lib/systemd/system/battuta-inputd.socket", spec);
        Assert.Contains("/usr/lib/systemd/system/battuta-inputd.service", spec);
        Assert.Contains("/usr/share/polkit-1/actions/io.github.wormforce.Battuta.Input1.policy", spec);
        Assert.Contains("/usr/share/dbus-1/system.d/io.github.wormforce.Battuta.Input1.conf", spec);
        Assert.Contains("/usr/share/applications/io.github.wormforce.Battuta.desktop", spec);
        Assert.Contains("/usr/share/metainfo/io.github.wormforce.Battuta.metainfo.xml", spec);
        Assert.Contains("%license /usr/share/doc/battuta/LICENSE", spec);
        // Prerelease versions keep their ordering via the release field.
        Assert.Contains("Release:        @RPM_RELEASE@%{?dist}", spec);
    }

    [Fact]
    public void SysusersCreatesDedicatedNonRootBrokerUser()
    {
        var sysusers = ReadPackaging("sysusers", "battuta-inputd.conf");
        var entry = sysusers.Split('\n').Single(line => line.StartsWith('u'));
        // "u name - "GECOS" home shell" with no fixed uid, home, or shell.
        var normalized = string.Join(' ', entry.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("u battuta-input -", normalized);
        Assert.EndsWith("- -", normalized);
        Assert.Contains("\"Battuta input broker\"", normalized);
    }

    [Fact]
    public void HicolorIconsExistForEveryStandardSize()
    {
        foreach (var size in new[] { 32, 48, 64, 128, 256 })
        {
            var icon = Packaging("icons", $"hicolor/{size}x{size}", "apps", "io.github.wormforce.Battuta.png");
            Assert.True(File.Exists(icon), $"missing hicolor icon: {size}x{size}");
            var bytes = File.ReadAllBytes(icon);
            // PNG magic + IHDR width big-endian must equal the folder size.
            Assert.Equal(0x89, bytes[0]);
            Assert.Equal('P', (char)bytes[1]);
            Assert.Equal(size, (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19]);
        }
    }

    [Fact]
    public void MimeEntryCoversThePackExtension()
    {
        var mime = XDocument.Parse(ReadPackaging("mime", "io.github.wormforce.Battuta.xml"));
        var mimeType = mime.Descendants().Single(e => e.Name.LocalName == "mime-type");
        Assert.Equal("application/x-simuboard-pack", (string?)mimeType.Attribute("type"));
        var glob = mimeType.Descendants().Single(e => e.Name.LocalName == "glob");
        Assert.Equal("*.simuboardpack", (string?)glob.Attribute("pattern"));
    }

    [Fact]
    public void AppProjectPublishesSharedAssetsAndPackOutput()
    {
        var csproj = File.ReadAllText(
            Path.Combine(RepoRoot, "BattutaLinux", "src", "Battuta.Linux", "Battuta.Linux.csproj"));

        // Publish output must carry the canonical audio (for .p48m products
        // to sit next to), the bundled BCP pack, and the brand icon.
        Assert.Contains("shared\\audio\\builtin\\**\\*.*", csproj);
        Assert.Contains("Assets\\Sounds\\%(RecursiveDir)%(Filename)%(Extension)", csproj);
        Assert.Contains("shared\\soundpacks\\bundled\\**\\*.*", csproj);
        Assert.Contains("BundledSoundPacks\\%(RecursiveDir)%(Filename)%(Extension)", csproj);
        var gpui = File.ReadAllText(Path.Combine(RepoRoot, "BattutaLinux", "gpui", "src", "main.rs"));
        Assert.Contains("shared/brand/source/AppIconSquare.png", gpui);
        var packageBuilder = File.ReadAllText(
            Path.Combine(RepoRoot, "BattutaLinux", "scripts", "build-linux-package.sh"));
        Assert.Contains("exec /usr/lib/battuta/bin/battuta-gpui", packageBuilder);
        Assert.Contains("CopyToPublishDirectory>PreserveNewest<", csproj);
    }
}
