# MRAudioKit

Browse, preview and re-pack Marvel Rivals soundbanks. Standalone: it reads the game's
paks directly and needs no folders exported from FModel first.

## Build

```
dotnet build -c Release
```

That is the whole story — the only dependency is the `CUE4Parse` NuGet package. No
submodules, no sibling checkouts, no paths into another project.

## Setup (nothing here ships with the tool)

| Field | What it is | Needed for |
| --- | --- | --- |
| Paks folder | `…\MarvelRivals\MarvelGame\Marvel\Content\Paks` | everything |
| AES key | per patch, from the modding community | everything |
| usmap | per patch mappings file | voice lines and categories |
| vgmstream-cli.exe | https://github.com/vgmstream/vgmstream | playback, durations |
| WwiseConsole.exe + a .wproj | an Audiokinetic Wwise install | *optional* — Vorbis test clips |

The Paks folder, `vgmstream-cli.exe` and a `*.usmap` sitting beside the exe are picked
up automatically on first run. Settings and notes live in `%AppData%\MRAudioKit\`.

**The AES key is never bundled.** It is yours to supply.

## What it does

- **Browse** character → skin → bank, with event names, media ids, codecs, categories
  and the shipped voice line for every sound.
- **Preview** any sound (loose streamed file first, bank copy as fallback — get that
  order wrong and voice lines play as a 26 ms click).
- **Notes / Extra notes** per sound, saved locally and re-linked by event name when a
  patch changes media ids.
- **Build a modded .bnk** — drop `{MediaID}-{your note}.wem` files on the right-hand
  pane, singly or by the folder. The whole clip goes into the bank; nothing ships
  under `Media/` unless a sound has no bank entry to replace.
- **Numbered test bank** — replace every sound in a bank with a spoken number and get
  a legend CSV, so triggering a sound in game tells you which entry it was. Clips can
  be supplied or generated on the spot with the Windows speech voice.
- **Export CSV** of a skin's sounds, in the shape of the community audio spreadsheet.

## Headless verbs

Used for verification; handy on their own.

```
MRAudioKit.exe --selftest <skinId> [report.txt]      full pipeline check
MRAudioKit.exe --media <id> [<id>…] [--bank <name>]  where a media lives, and how
MRAudioKit.exe --build <skinId> <wemDir> <outDir>    build a mod, no window
MRAudioKit.exe --testbank <skinId> <clipDir> <outDir> [bankFilter]
MRAudioKit.exe --tts <outDir> <first> <last>         generate spoken numbers
MRAudioKit.exe --bankmax                             largest bank in the game
```

## Known limits

- Generated speech clips are **PCM**. Every audio source in this game declares Vorbis,
  and whether the engine minds has not been tested — set WwiseConsole in Setup for real
  Vorbis, or use an existing Vorbis clip set.
- Sounds with no shipped event name cannot have their notes re-linked across patches;
  those stay keyed to the media id.
- A few default skins have no name in the localisation tables and fall back to the id.

## Third party

CUE4Parse (Apache-2.0). vgmstream and WwiseConsole are invoked if present and are not
redistributed here.
