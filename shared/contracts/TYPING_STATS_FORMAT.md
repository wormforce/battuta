<!-- markdownlint-disable MD013 -->

# Battuta typing stats SQLite format v1

Status: frozen contract (v1). Linux reuses the existing BTTA schema; no second
statistics database is created on any platform.

- Canonical implementation: `TypingStatsSqliteStore` (currently
  `BattutaWindows/src/Battuta.Windows/Stats/Persistence/TypingStatsSqliteStore.cs`,
  scheduled to move to `shared/dotnet/Battuta.Engine/`).
- Locking tests: `TypingStatsSqliteStoreTests` and
  `TypingStatsSchemaContractTests` (Windows test suite).

## 1. File identity

- Single SQLite database file, e.g. `typing-stats.sqlite3`.
  - Windows: `%LOCALAPPDATA%\Battuta\typing-stats.sqlite3`
  - Linux (planned): `$XDG_DATA_HOME/battuta/typing-stats.sqlite3`
- `PRAGMA application_id = 0x42545441` (ASCII `BTTA`, decimal `1112822849`).
- `PRAGMA user_version = 1`.
- Opening a file with a different `application_id`/`user_version`, or missing
  required columns, fails with an incompatible-schema error and **never
  rewrites the database**. Persistent pragmas are applied only after the
  identity check passes.

## 2. Connection pragmas

| Pragma | When | Value |
| --- | --- | --- |
| `foreign_keys` | every connection | `ON` |
| `busy_timeout` | every connection | `1000` ms |
| `journal_mode` | after identity check, persistent | `WAL` |
| `synchronous` | after identity check, persistent | `NORMAL` |
| pooling | connection string | disabled (`Pooling=false`) |

## 3. Tables

All timestamps are Unix seconds (UTC). `LocalDate` is `yyyy-MM-dd` in the
user's local time zone; `LocalHour` is `0`–`23` local.

### AppProfile

| Column | Type | Constraints |
| --- | --- | --- |
| `Id` | INTEGER | PRIMARY KEY |
| `ProcessKey` | TEXT | NOT NULL, UNIQUE — stable per-process identity |
| `ProcessName` | TEXT | NOT NULL |
| `DisplayName` | TEXT | NOT NULL |
| `ApplicationUserModelId` | TEXT | nullable |
| `UpdatedAtUtc` | INTEGER | NOT NULL |

Application identity is limited to process/app identity and display name.
Window titles are never stored on any platform.

### CharacterSecondStat (detailed, `WITHOUT ROWID`)

PK `(SecondStartUtc, AppId)`. Columns: `SecondStartUtc INTEGER`,
`LocalDate TEXT` (length 10), `LocalHour INTEGER` (0–23),
`AppId INTEGER` -> AppProfile, `CharacterCount INTEGER` (>= 0),
`UpdatedAtUtc INTEGER`. This is the only retained raw layer.

Indices: `IX_CharacterSecondStat_Date (LocalDate, SecondStartUtc)`,
`IX_CharacterSecondStat_DateApp (LocalDate, AppId)`.

### KeyDailyStat (`WITHOUT ROWID`)

PK `(LocalDate, PhysicalKeyId)`. `PhysicalKeyId TEXT` (1–128 chars,
`COLLATE BINARY`), `PressCount INTEGER` (>= 0), `UpdatedAtUtc INTEGER`.

### KeyTotalStat (`WITHOUT ROWID`)

PK `PhysicalKeyId` (same constraints), all-time `PressCount`, `UpdatedAtUtc`.

### DayStat (`WITHOUT ROWID`)

PK `LocalDate`. Aggregates: `CharacterCount`, `ActiveMinuteBuckets`,
`ActiveSeconds`, `PeakCPS` (all >= 0), `LastInputUtc`, `UpdatedAtUtc`.

### AppDayStat (`WITHOUT ROWID`)

PK `(LocalDate, AppId)` -> AppProfile. Same aggregate columns as `DayStat`
plus index `IX_AppDayStat_AppDate (AppId, LocalDate)`.

### HourDayStat (`WITHOUT ROWID`)

PK `(LocalDate, LocalHour)`. Same aggregate columns as `DayStat`.

## 4. Aggregate triggers

- `TR_CharacterSecondStat_AggregateInsert` — AFTER INSERT on
  `CharacterSecondStat`, maintains `DayStat`, `AppDayStat`, and `HourDayStat`
  (active-minute/active-second counting, `PeakCPS` as the second-level maximum,
  `LastInputUtc` as the maximum second start).
- `TR_CharacterSecondStat_AggregateUpdate` — AFTER UPDATE OF `CharacterCount`,
  applies count deltas and re-derives `PeakCPS`/`LastInputUtc`.

Writers upsert into `CharacterSecondStat` only; the triggers keep every
aggregate consistent. No platform may bypass the triggers with direct
aggregate writes.

## 5. Retention

- `CharacterSecondStat`: detailed seconds older than **31 days** are deleted on
  the first write of each local day.
- `KeyDailyStat`, `KeyTotalStat`, `DayStat`, `AppDayStat`, `HourDayStat`:
  permanent aggregates, never expired.
- The in-app history view shows the last **14 days** (`HistoryDayCount`); this
  is a presentation window, not a retention limit.

## 6. Statistics semantics (cross-platform, locked)

- 字符数 counts keyDown presses of allow-listed physical character keys. It is
  not text length, includes auto-repeat, and excludes presses with shortcut
  modifiers held.
- 逐键热力图 counts non-repeat keyDown presses only, per `PhysicalKeyId`.
- `PhysicalKeyId` values are the neutral IDs from `Battuta.Core`; unknown keys
  are persisted as stable namespaced IDs (e.g. `linux.evdev.<code>` on Linux),
  never as layout characters. IDs are 1–128 characters.
- Clearing data runs `PRAGMA secure_delete = ON`, deletes all rows from
  `CharacterSecondStat`, `KeyDailyStat`, `KeyTotalStat`, `HourDayStat`,
  `AppDayStat`, `DayStat`, `AppProfile`, then best-effort
  `PRAGMA wal_checkpoint(TRUNCATE)` + `VACUUM`. Logical deletion is
  authoritative; compaction failures are tolerated.
- Writes are batched (recorder flushes at most every 750 ms) and failures back
  off instead of spamming retries.

## 7. Foreground application identity (cross-platform)

- Every recorded keyboard event carries a `TypingApplicationIdentity`
  (`ProcessKey`, `DisplayName`, `ProcessName`, optional
  `ApplicationUserModelId`). `AppProfile` stores exactly these fields keyed by
  `ProcessKey`; aggregates reference the stable `ProcessKey`, so renaming an
  application's display name does not split history.
- Platforms resolve the identity through a foreground-application **adapter
  chain**. The Linux port (PR 8) resolves, in priority order:
  1. **KWin** — session-bus scripting agent (`org.kde.KWin` →
     `org.kde.kwin.Scripting`); a KWin script pushes `resourceClass` + `pid`
     on `windowActivated` and on start, through the neutral
     `io.github.wormforce.Battuta.ForegroundAgent` D-Bus contract.
  2. **GNOME Shell** — reserved for a later extension; the same agent
     contract is already hosted, and until the extension exists the adapter
     reports `NeedsExtension` (application dimension records as unknown).
  3. **sway / i3** — IPC `GET_TREE` focused node: `app_id`, falling back to
     `window_properties.class` (XWayland).
  4. **Hyprland** — IPC `activewindow -j`: `class`, falling back to
     `initialClass`.
  5. **X11** — `_NET_ACTIVE_WINDOW` + `WM_CLASS` on the root window when the
     session is X11-only (no Wayland display markers).
  6. **None** — nothing resolvable; the UI must state this explicitly.
- **Unknown semantics**: when no adapter can resolve an identity, events
  record the `unknown` process key and the application dimension still works
  (top applications simply show 未知应用). Detection never guesses: a Wayland
  session without a resolvable compositor reports `Unavailable` rather than
  falling through to X11.
- **Window titles are never read, stored, or logged** — only the application
  class (or process key) and PID. This is a hard privacy rule for every
  platform adapter; IPC replies that carry titles parse them away at the
  boundary.
- **UI source status requirement**: any statistics UI must show which source
  produced the application identities (adapter name + state, e.g.
  `KWin(运行中)`, or "已禁用/未识别"), and must surface recorder write
  failures/staleness next to the numbers.

## 8. Compatibility rules

- `application_id`, `user_version`, table/column sets, and trigger semantics
  are frozen for v1. Any change requires a new `user_version` plus a migration
  path in `MigrateAndValidateAsync`.
- Schema errors must surface as a typed incompatible-schema error; the
  database file must remain byte-untouched in that case.
