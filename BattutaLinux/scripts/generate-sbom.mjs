// Generates an SPDX 2.3 JSON SBOM for the Linux release (Linux port PR 10).
//
// Covers the two dependency surfaces a Battuta Linux package ships or
// links:
//   - managed: PackageVersion entries from the three Directory.Packages.props
//     (shared, BattutaWindows for comparison, BattutaLinux) — the Linux
//     publish output resolves against shared + BattutaLinux.
//   - native: crates from both Linux Cargo.lock files, plus the
//     system libraries the packaged binaries require at runtime
//     (glibc/libstdc++/fontconfig/pipewire), recorded as the deployment's
//     external references.
//
// Usage: node scripts/generate-sbom.mjs <version> <output.spdx.json>

import { readFile, writeFile } from "node:fs/promises";
import { createHash } from "node:crypto";
import { resolve, dirname } from "node:path";

const [version, outputPath] = process.argv.slice(2);
if (!version || !outputPath) {
  console.error("usage: generate-sbom.mjs <version> <output.spdx.json>");
  process.exit(2);
}

const scriptDir = dirname(new URL(import.meta.url).pathname);
const repoRoot = resolve(scriptDir, "../..");
const linuxRoot = resolve(repoRoot, "BattutaLinux");

function sha256(text) {
  return createHash("sha256").update(text).digest("hex");
}

function spdxId(kind, index) {
  return `SPDXRef-${kind}-${index}`;
}

async function readManagedPackages() {
  // The Linux build resolves central versions from these two files only.
  const files = [
    resolve(repoRoot, "shared/dotnet/Directory.Packages.props"),
    resolve(linuxRoot, "Directory.Packages.props"),
  ];
  const packages = [];
  for (const file of files) {
    const text = await readFile(file, "utf8");
    for (const match of text.matchAll(/<PackageVersion Include="([^"]+)" Version="([^"]+)"/g)) {
      const [, name, packageVersion] = match;
      if (!packages.some((p) => p.name === name)) {
        packages.push({ name, version: packageVersion });
      }
    }
  }
  return packages.sort((a, b) => a.name.localeCompare(b.name));
}

async function readNativePackages() {
  // Cargo.lock is TOML; the only shapes we need are the flat [[package]]
  // blocks with name/version/source fields, parsed line by line.
  const locks = await Promise.all(["native", "gpui"].map((dir) =>
    readFile(resolve(linuxRoot, dir, "Cargo.lock"), "utf8")));
  const seen = new Map();
  for (const lock of locks) {
    let current = null;
    for (const line of lock.split("\n")) {
      if (line === "[[package]]") {
        if (current?.version) seen.set(`${current.name}@${current.version}`, current);
        current = { source: "local" };
        continue;
      }
      const match = line.match(/^(name|version|source) = "(.*)"$/);
      if (current && match) current[match[1]] = match[2];
    }
    if (current?.version) seen.set(`${current.name}@${current.version}`, current);
  }
  return [...seen.values()].sort((a, b) => a.name.localeCompare(b.name));
}

// System libraries the self-contained publish still links at runtime. They
// are not vendored; they are recorded so the SBOM describes the deployment.
const systemLibraries = [
  { name: "glibc", version: ">= 2.31", url: "https://www.gnu.org/software/libc/" },
  { name: "libstdc++6", version: ">= 11", url: "https://gcc.gnu.org/onlinedocs/libstdc++/" },
  { name: "fontconfig", version: ">= 2.13", url: "https://www.freedesktop.org/wiki/Software/fontconfig/" },
  { name: "libxcb", version: "NOASSERTION", url: "https://xcb.freedesktop.org/" },
  { name: "xkbcommon", version: "NOASSERTION", url: "https://xkbcommon.org/" },
  { name: "Vulkan loader", version: "NOASSERTION", url: "https://github.com/KhronosGroup/Vulkan-Loader" },
  { name: "libicu", version: ">= 70", url: "https://icu.unicode.org/" },
  { name: "pipewire", version: ">= 0.3.49", url: "https://pipewire.org/" },
  { name: "polkit", version: ">= 121", url: "https://github.com/polkit-org/polkit/" },
  { name: "systemd", version: ">= 249", url: "https://systemd.io/" },
];

const [managed, native] = await Promise.all([readManagedPackages(), readNativePackages()]);

const packages = [];
let index = 0;
for (const item of managed) {
  index += 1;
  packages.push({
    name: `dotnet/${item.name}`,
    SPDXID: spdxId("Managed", index),
    versionInfo: item.version,
    downloadLocation: `https://www.nuget.org/packages/${item.name}/${item.version}`,
    filesAnalyzed: false,
    licenseConcluded: "NOASSERTION",
    copyrightText: "NOASSERTION",
    supplier: "NOASSERTION",
  });
}
for (const item of native) {
  index += 1;
  const isCratesIo = item.source.startsWith("registry+");
  packages.push({
    name: `rust/${item.name}`,
    SPDXID: spdxId("Native", index),
    versionInfo: item.version,
    downloadLocation: isCratesIo
      ? `https://crates.io/crates/${item.name}/${item.version}`
      : "NOASSERTION",
    filesAnalyzed: false,
    licenseConcluded: "NOASSERTION",
    copyrightText: "NOASSERTION",
    supplier: "NOASSERTION",
  });
}
for (const item of systemLibraries) {
  index += 1;
  packages.push({
    name: `system/${item.name}`,
    SPDXID: spdxId("System", index),
    versionInfo: item.version,
    downloadLocation: item.url,
    filesAnalyzed: false,
    licenseConcluded: "NOASSERTION",
    copyrightText: "NOASSERTION",
    supplier: "NOASSERTION",
  });
}

const document = {
  spdxVersion: "SPDX-2.3",
  dataLicense: "CC0-1.0",
  SPDXID: "SPDXRef-DOCUMENT",
  name: `Battuta-Linux-${version}`,
  documentNamespace: `https://github.com/wormforce/battuta/spdx/battuta-linux-${version}-${sha256(version).slice(0, 12)}`,
  creationInfo: {
    created: new Date().toISOString().replace(/\.\d{3}Z$/, "Z"),
    creators: ["Organization: Battuta contributors", "Tool: battuta-linux-packaging"],
    licenseListVersion: "3.25",
  },
  describes: ["SPDXRef-Package-Battuta"],
  packages: [
    {
      name: "Battuta",
      SPDXID: "SPDXRef-Package-Battuta",
      versionInfo: version,
      downloadLocation: `https://github.com/wormforce/battuta/releases/tag/v${version}`,
      filesAnalyzed: false,
      licenseConcluded: "MIT",
      licenseDeclared: "MIT",
      copyrightText: "2026 Battuta contributors",
      supplier: "Organization: Battuta contributors",
      externalRefs: [
        {
          referenceCategory: "PACKAGE-MANAGER",
          referenceType: "purl",
          referenceLocator: `pkg:github/wormforce/battuta@v${version}`,
        },
      ],
    },
    ...packages,
  ],
  relationships: [
    {
      spdxElementId: "SPDXRef-DOCUMENT",
      relationshipType: "DESCRIBES",
      relatedSpdxElement: "SPDXRef-Package-Battuta",
    },
    ...packages.map((p) => ({
      spdxElementId: "SPDXRef-Package-Battuta",
      relationshipType: "DEPENDS_ON",
      relatedSpdxElement: p.SPDXID,
    })),
  ],
};

await writeFile(outputPath, JSON.stringify(document, null, 2) + "\n");
console.log(
  `SBOM written: ${outputPath} (1 top-level + ${managed.length} managed + ${native.length} native + ${systemLibraries.length} system packages)`,
);
