# Third-party notices

## Sparkle update framework

The macOS application embeds [Sparkle 2.9.6](https://github.com/sparkle-project/Sparkle)
under its MIT license. The complete Sparkle license and notices for its bundled
components are distributed with the application in
[`SPARKLE_LICENSE.txt`](SimuBoardMac/SimuBoardMac/Resources/SPARKLE_LICENSE.txt).

## Audio used with permission

**BCP (Suit80)** uses 28 derived press/release samples from the recording
`【打字声音】Suit80｜BCP轴｜GMK Ursa 大熊 - Original.mp4`, whose visible uploader
is **J_Eason001**. Redistribution of these derived assets in Battuta's public
application bundle, DMG, source repository, appcast release set, and GitHub
releases is authorized. The permission record is retained privately by the
Battuta maintainer. Battuta downmixed and denoised the recording, then produced
audited 48 kHz mono PCM cuts for keyboard rows, alternate small keys, Shift,
Backspace, Enter, and Space. No endorsement is implied.

## Windows application dependencies

The Windows application uses the following MIT-licensed NuGet packages. Exact
versions are pinned in `BattutaWindows/Directory.Packages.props`.

- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)
- [H.NotifyIcon.Wpf](https://github.com/HavenDV/H.NotifyIcon)
- [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore), including its
  SQLitePCLRaw native SQLite dependencies
- [NAudio](https://github.com/naudio/NAudio)

Their copyright and permission notices are included in the corresponding NuGet
packages. Battuta redistributes these libraries under their MIT terms and does
not remove their notices.

## GPUI on Linux

Battuta's Linux client uses a modified copy of
[GPUI 0.2.2](https://crates.io/crates/gpui/0.2.2) from Zed Industries under
Apache-2.0. Linux releases include the license as `GPUI-LICENSE-APACHE`.
The source and local changes are documented in
[`BattutaLinux/gpui/vendor/gpui/BATTUTA.md`](BattutaLinux/gpui/vendor/gpui/BATTUTA.md).

## MIT-licensed recordings

The following bundled recordings are redistributed under the MIT License:

- **kbsim audio profiles** — Source: https://github.com/tplai/kbsim
  Copyright (c) Thomas Lai
- **Kailh BOX White** — Source: https://github.com/Mange/clicketyclack
  Copyright (c) 2021 Magnus Bergmark
- **Logitech G915 TKL Brown** — Source:
  https://github.com/keyboard-sounds/keyboardsounds-pro/tree/main/desktop/bundled-profiles/logitech-g915-tkl-brown
  Copyright (c) 2025 Nathan Fiscaletti

Battuta resampled the G915 recordings to 48 kHz mono PCM, trimmed leading
room tone while retaining a short pre-roll, applied profile gain compensation
(with extra gain for the quieter alternate large-key release), and added a 4 ms
tail fade.

MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notices and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## CC0 1.0 recordings

These recordings were released under the
[CC0 1.0 Universal dedication](https://creativecommons.org/publicdomain/zero/1.0/).
Attribution is not required by CC0, but the sources and modifications are
recorded here for provenance.

- **Studio Tactile / Studio Clicky** — Ten single-key recordings by StavSounds,
  Freesound pack [Mechanical Keyboards](https://freesound.org/people/StavSounds/packs/42151/),
  sound IDs 766625, 766632–766635, 766605–766606, and 766622–766624.
  Battuta uses the public HQ preview renditions, trims leading room tone while
  retaining a short pre-roll, downmixes to mono, resamples to 48 kHz PCM, and
  separates each complete keystroke into press and release at an audited energy
  valley, with short boundary fades.
- **Kailh Low-profile Blue** — “Fast Typing on Mechanical Keyboard” by
  [HeinzBBQ](https://freesound.org/people/HeinzBBQ/sounds/502653/), Freesound
  sound 502653. Battuta selects five excerpts from the public HQ preview,
  downmixes them to mono, resamples them to 48 kHz PCM, separates the press and
  release events, and excludes neighboring keystrokes from the release samples.
- **Cherry MX Clear** — “Mechanical keyboard clicking. Different keys (4)” by
  [humi74](https://freesound.org/people/humi74/sounds/412926/), Freesound sound
  412926. Battuta selects five excerpts from the public HQ preview, downmixes
  them to mono, resamples them to 48 kHz PCM, and separates the press and
  release events. One excerpt without a usable release reuses the closest clean
  release variation from the same source recording.
- **Pointer Classic, Silent, Crisp, Heavy, and Glass** —
  [Kenney UI Audio](https://kenney.nl/assets/ui-audio) by Kenney Vleugels. The
  source archive was downloaded on 2026-08-22. Battuta derives all five
  generic simulated styles from the matched `mouseclick1.ogg` press recording
  and `mouserelease1.ogg` release recording. Each phase is pitch-lowered with
  compensation for the associated tempo change, then receives profile-specific
  low-pass filtering, restrained midrange EQ and level adjustment; Crisp and
  Glass retain a gentle
  low cut for definition. The process removes leading signal below −45 dBFS
  while retaining up to 2 ms of pre-roll, then adds a 4 ms tail fade and
  renders as 48 kHz mono 16-bit PCM WAV. These style names do not identify or
  claim to reproduce a particular mouse brand or switch. Attribution is not
  required by CC0; it is provided for provenance.

## CC BY 4.0 recording

**Keychron Red Linear** uses excerpts from
[Typing on Keychron V1 Ultra (Red Linear Switch).wav](https://commons.wikimedia.org/wiki/File:Typing_on_Keychron_V1_Ultra_(Red_Linear_Switch).wav)
by Wikimedia Commons user **C40115**, licensed under
[Creative Commons Attribution 4.0 International](https://creativecommons.org/licenses/by/4.0/).

Battuta selected five 180 ms excerpts, downmixed the stereo recording to
mono, retained the original 48 kHz rate, and separated each press/release event
while excluding neighboring keystrokes. No endorsement by the original author
is implied.
