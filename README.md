# XzoundWave

A sound toolkit for Marvel Rivals. Browse, preview, annotate and re-pack the game's
Wwise soundbanks.

Standalone: it reads your paks directly. Nothing has to be exported from FModel first,
and no audio tooling has to be installed to build a working mod — the Vorbis encoder is
built in.

## Build

```
dotnet build -c Release
```

That is the whole story. Dependencies come from NuGet; there are no submodules, no
sibling checkouts and no paths into another project.

## Setup — nothing here ships with the tool

| Field | What it is | Needed for |
| --- | --- | --- |
| Paks folder | `…\MarvelRivals\MarvelGame\Marvel\Content\Paks` | everything |
| AES key | per patch, from the modding community | everything |
| usmap | per patch mappings file | voice lines and categories |
| vgmstream-cli.exe | https://github.com/vgmstream/vgmstream | playback, durations |

The Paks folder, `vgmstream-cli.exe` and a `*.usmap` sitting beside the exe are found
automatically on first run. Settings live in `%AppData%\MRAudioKit\`; your work lives in
a `.mrak` project file wherever you choose to put it.

**The AES key is never bundled.** It is yours to supply.

## What it does

The window is one row of tabs — you are browsing, or testing, or building, or comparing
mods, and the screen shows that job's tools.

### Browse

- Character → skin → bank, plus **205 non-character banks** (announcers, bosses, NPCs,
  UI, music, maps) grouped by kind.
- Event name, media id, codec, category and the shipped **subtitle** for every sound.
- Categories are authored in Chinese and the game ships no English for them. A built-in
  translator handles 98–100% offline, deriving hero and ability names from the game's
  own English text; an optional CC-CEDICT download covers the remainder.
- Preview anything, or **autoplay** down the list to scan a character's lines.
- **Colour coding** at a glance: green staged, red muted, purple changed by a mod.
- **Tags** from a shared vocabulary (`[ULT]`, `[MVP]`, `[LOOP]`, `[3D]` …) picked from a
  menu and never typed, so two people's notes mean the same thing. Free-text notes sit
  alongside. Both survive a patch: they re-link by event name when media ids change.
- **Settings** for which rows and columns the list shows — hide everything already
  tagged, or everything a mod left alone.

### Test

Working out which sound is which is most of the job, so:

- **Numbered test bank** — every sound becomes a clip that speaks a number, with a
  legend, so triggering it in game tells you exactly which entry fired.
- **Test only the selection**, silencing everything else in the bank, for when several
  sounds fire at once.
- **Mute** the numbers you have already identified so the quieter ones underneath become
  audible, then rebuild. Numbering stays stable across rounds.
- **Silent banks**, for identifying a sound by its absence.

### Build

- Drop **wav, mp3, ogg, flac, m4a, aac, wma or wem** — anything that is not already a
  wem is encoded to Wwise Vorbis on the way in. No Wwise install, no ffmpeg.
- Name files `{MediaID}-{your note}.wem`, or drop a single file onto a selection to give
  every one of them the same audio from one encode.
- **Volume** per sound or in bulk, always applied to the original so setting it twice
  does not compound two lossy round trips. Hand-set levels survive a bulk change.
- Output mirrors the game's own paths. A sound that lives in a bank goes into the bank;
  one that does not ships loose at the exact path the game ships it, language folder and
  all.

### Mods

- **Open** someone else's `.bnk`, or a bank inside a mod `.pak`, and see it diffed
  against the shipped one — every entry marked `MODDED`, `NEW` or untouched.
- **Extract only the modded wems**, named so they drop straight back in.
- **Merge** two mods: each is diffed against the shipped bank so only what its author
  replaced carries over, and the first one you pick wins any collision.
- **Recompress** a PCM bank to Vorbis — around 8× smaller, and refused if it would come
  out larger.

## Headless verbs

Everything is verifiable without a window, driving the same code the UI does.

```
MRAudioKit.exe --selftest <skinId> [report.txt]        full pipeline check
MRAudioKit.exe --build <skinId> <wemDir> <outDir> [--prefetch]
MRAudioKit.exe --testbank <skinId> <clipDir> <outDir> [bankFilter]
MRAudioKit.exe --openbank <bnk|pak> [rows]             diff a mod against the game
MRAudioKit.exe --merge <modA> <modB> <outDir>          A wins collisions
MRAudioKit.exe --import <file> <outDir>                any supported format -> wem
MRAudioKit.exe --recompress <bnk> <outDir>             PCM bank -> Vorbis
MRAudioKit.exe --volume <skinId> <wemDir> <outDir> <gain>
MRAudioKit.exe --media <id> [<id>…] [--bank <name>]    where a media lives, and how
MRAudioKit.exe --medialayout                           loose-media layout and overlap
MRAudioKit.exe --banks                                 every bank, grouped
MRAudioKit.exe --tts <outDir> <first> <last>           generate spoken numbers
```

Self-tests, each printing `ALL CHECKS PASSED` or a numbered failure:

```
--selftest  --seltest  --bulktest  --tags  --rowfilters  --menutest  --mediapath
--vorbistest  --setuproundtrip
```

## XzoundCore

The encoder is its own assembly (`XzoundCore.dll` — no UI, no Windows API) so other
tools can use it without depending on this app. It writes Wwise Vorbis from scratch: no
reference file to copy fields from, no Wwise install.

The part that took longest to find, in case it saves someone else the trouble:
`uMaxPacketSize` at offset `0x1e` of the `AkVorbisInfo` block is **required**. The
runtime sizes its packet buffer from it, so a zero there means every packet overflows a
zero-length buffer and the sound plays as silence — with no error reported anywhere.

## Known limits

- **Prefetch mode** (Build tab, off by default) is structurally faithful to how the game
  stores streamed audio, but has not been tested in game. The default path has.
- The optional **live translation** providers (DeepL, LibreTranslate, Ollama) have been
  exercised against mocks only. The offline path is the tested one and the default.
- Sounds with no shipped event name cannot have notes or tags re-linked across patches;
  those stay keyed to the media id.
- A few default skins have no name in the localisation tables and fall back to the id.

## Licence

MIT — see `LICENSE`. Third-party components keep their own terms; see
`THIRD-PARTY-NOTICES.md`, which must travel with any copy you distribute.

Marvel Rivals and its audio belong to NetEase and Marvel. This tool ships none of it.
