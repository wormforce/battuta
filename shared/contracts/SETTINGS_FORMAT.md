<!-- markdownlint-disable MD013 -->

# Battuta settings JSON format v1

Status: frozen contract (v1). The JSON document that Windows currently writes is
the single source of truth. Linux (and any future platform) must read and write
this exact format; a platform must not invent a second settings schema.

- Canonical writer: `JsonAppSettingsStore` (currently
  `BattutaWindows/src/Battuta.Windows/Settings/JsonAppSettingsStore.cs`,
  scheduled to move to `shared/dotnet/Battuta.Engine/`).
- Canonical reader defaults and normalization live in `AppSettingsSnapshot`.
- Locking tests: `SettingsJsonContractTests` (Windows test suite) against the
  fixtures in this directory.

## 1. Storage locations

| Platform | Primary file | Backup | Corrupt-file quarantine |
| --- | --- | --- | --- |
| Windows | `%LOCALAPPDATA%\Battuta\settings.json` | `settings.json.bak` | `settings.corrupt-<yyyyMMdd-HHmmss-fff>.json` |
| Linux (planned) | `$XDG_CONFIG_HOME/battuta/settings.json` | `settings.json.bak` | same naming |

Files are UTF-8 without a BOM requirement. The backup file is written by the
atomic replacement step (below) and is only read when the primary file is
missing or unreadable.

## 2. Serialization rules

- One JSON object at the top level.
- Property names are lowerCamelCase, written in the exact order listed in
  section 3.
- Booleans and numbers are JSON natives; volumes are doubles in `[0, 1]`.
- Enumerations are serialized as lowerCamelCase strings
  (`"undecided"`, `"enabled"`, `"disabled"`).
- Output is pretty-printed with two-space indentation and no trailing newline.
- Readers are lenient: trailing commas are allowed and `//`-style comments are
  skipped.

## 3. Fields

| JSON name | Type | Default (absent) | Normalization on save |
| --- | --- | --- | --- |
| `schemaVersion` | int | `1` | forced to `1` on save |
| `isEnabled` | bool | `true` | none |
| `selectedProfileId` | string | `"holypanda"` | trimmed, lower-cased; must be a known keyboard profile or `custom:<uuid>`; otherwise `"holypanda"` |
| `volume` | double | `0.42` | `NaN`/infinite falls back to `0.42`; clamped to `[0, 1]` |
| `playsReleaseSound` | bool | `true` | none |
| `usesPitchVariation` | bool | `true` | none |
| `isPointerSoundEnabled` | bool | `false` | none |
| `selectedPointerProfileId` | string | `"classic"` | trimmed, lower-cased; must be a known pointer profile; otherwise `"classic"` |
| `pointerVolume` | double | `volume * 0.65` | `NaN`/infinite falls back to `volume * 0.65`; clamped to `[0, 1]` |
| `playsPointerReleaseSound` | bool | `true` | none |
| `isTypingStatsEnabled` | bool | `false` | none |
| `isLaunchAtLoginEnabled` | bool | `true` | none |
| `automaticUpdateCheckPreference` | string enum | `"undecided"` | unknown enum values make the document malformed (section 5) |
| `playsKeyRepeatSound` | bool | `false` | optional on read, triggers playback for keyboard repeat events |

Known keyboard profiles (v1): `holypanda`, `mxbrown`, `mxclear`, `g915brown`,
`studiotactile`, `mxblue`, `boxnavy`, `boxwhite`, `lowprofileblue`, `bluealps`,
`studioclicky`, `cream`, `alpaca`, `blackink`, `redink`, `mxblack`,
`turquoise`, `keychronred`, `topre`, `buckling`, plus `custom:<uuid>`.

Known pointer profiles (v1): `classic`, `silent`, `crisp`, `heavy`, `glass`.

## 4. Reader behavior

1. Missing field -> default from the table above. A missing `pointerVolume`
   derives `volume * 0.65` (legacy migration).
2. Unknown top-level fields are ignored on read and dropped on the next save.
   Readers must not fail on them, and writers must not preserve them.
3. Malformed JSON or an unknown enum value makes the whole document malformed:
   the primary file is preserved unchanged as
   `settings.corrupt-<timestamp>.json`, the backup file is then tried, and if
   that also fails the defaults apply. The corrupt file is never silently
   deleted or rewritten.
4. A successfully loaded document is normalized once (section 3) before use.

## 5. Writer behavior (atomic replacement)

1. Serialize the normalized snapshot to a sibling temporary file
   (`.settings.json.<guid>.tmp`), created with `FileMode.CreateNew`, flushed to
   disk.
2. `File.Replace(temp, primary, backup)` when the primary exists; otherwise
   `File.Move(temp, primary)`. The replaced primary becomes the new backup.
3. A failed temporary-file cleanup is tolerated; the previous primary file is
   never left truncated.
4. Saves are serialized through an in-process gate; concurrent writers on one
   store instance are ordered.

## 6. Compatibility rules

- Field names and the field set are frozen. Renaming or removing a field is a
  breaking change requiring a new `schemaVersion` and a migration story.
- New optional fields may be appended after
  `playsKeyRepeatSound` with a minor revision of this document;
  older readers must keep ignoring them (rule 4.2).
- The canonical byte shape (field order, indentation, enum casing) is part of
  the contract so that fixtures can diff across platforms.

## 7. Fixtures

| Fixture | Purpose |
| --- | --- |
| `settings.v1.full.json` | canonical complete document; save output must be byte-identical (modulo trailing newline) and load must yield the documented snapshot |
| `settings.v1.partial-legacy.json` | legacy minimal document with comments and a trailing comma; locks defaults and lenient reading |
| `settings.v1.unknown-fields.json` | unknown top-level fields; locks tolerance on read and dropping on save |
