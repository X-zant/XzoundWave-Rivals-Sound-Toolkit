# XzoundWave

A sound toolkit for Marvel Rivals. Browse, preview, annotate and re-pack the game's
Wwise soundbanks.

Standalone in the literal sense: the release is **one `XzoundWave.exe`**. Nothing to
install, nothing to unzip beside it, no folders exported from FModel first. The Vorbis
encoder and vgmstream both ship inside the binary.

## Running it

Download `XzoundWave.exe` and run it.

The one thing it does not carry is the **[.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0)**.
Most machines already have it; bundling it would have quadrupled the download for
something Windows Update hands out. If it is missing, Windows says so when you double
click and points you at the installer. Run `XzoundWave.exe --doctor` to see what your
machine provides and whether anything is wrong.

If audio will not play, open **Settings > Diagnostics** — it reports the same thing and
has a Copy button, with no command line involved. `XzoundWave.exe --doctor` does it from
a console, and when there is no console (a double click) it shows the report in a window
and saves it to `%AppData%/XzoundWave/diagnostics.txt`.

`XzoundWave.exe --playtest` goes further and decodes a real sound and plays it, which
separates a decoding problem from a playback one.

Either way it says whether vgmstream unpacked **and whether it actually starts**, which
is the usual culprit: vgmstream is a native program, so it needs the
[Microsoft Visual C++ Redistributable (x64)](https://aka.ms/vs/17/release/vc_redist.x64.exe)
and it will not run if antivirus has removed one of the codec DLLs beside it.

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

That is the whole list, and the usmap half of it looks after itself: **Fetch latest** on
the Setup tab pulls the newest mappings from the community depot
([SpaceDepot/rivals-depot](https://github.com/SpaceDepot/rivals-depot)), picking the
highest game build rather than the most recent commit, and the Setup tab shows which
build is loaded. A usmap you picked yourself is never replaced without asking.
`XzoundWave.exe --usmap [--force]` does the same from a console.

vgmstream used to be on this list and no longer is: it unpacks itself out of the exe.
Settings › Tools can still point at your own build of it.

The Paks folder and a `*.usmap` sitting beside the exe are found automatically on first
run. Settings, the unpacked tools and the optional dictionary live in
`%AppData%/XzoundWave/`; your work lives in a `.xzw` project file wherever you put it.
Upgrading from a build called MRAudioKit carries your old settings over on first start.

**The AES key is never bundled.** It is yours to supply.

## What it does

The window is one row of tabs — you are browsing, or testing, or building, or comparing
mods, and the screen shows that job's tools.

### Browse

- Character → skin → bank, plus **205 non-character banks** (announcers, bosses, NPCs,
  UI, music, maps) grouped by kind.
- **Ctrl+click** several `.bnk` files to look at them together — across characters if you
  want. One list, with a `.bnk` column saying where each sound lives.
- Event name, media id, codec, category and the shipped **subtitle** for every sound.
- Categories are authored in Chinese and the game ships no English for them. A built-in
  translator handles 98–100% offline, deriving hero and ability names from the game's
  own English text; an optional CC-CEDICT download covers the remainder.
- Preview anything, or **autoplay** down the list to scan a character's lines. A sound
  with a file staged over it plays the staged file — that is what will ship — and
  **Play the game's original** on the right-click menu is there to compare against.
- **As in game** plays it the way the `.bnk` plays it rather than the way the file sits
  on disk: the volume trim, pitch shift, start delay and loop, applied. Filters,
  distance attenuation, HDR ducking and anything driven at runtime are not reproduced,
  and the status line names them when a sound has them rather than leaving you to
  wonder. An endless loop is previewed as four passes.
- **Colour coding** at a glance: green staged, red muted, purple changed by a mod.
- **What the game does to each sound**, in its own column: the volume trim, pitch
  shift, delay, loop or runtime curve the `.bnk` applies on top of the audio. None of
  it is in the file, and a swap inherits all of it — which is why a replacement can
  come out quieter, late or looping when the file plainly is not. Hover for the
  breakdown, including what it inherits from the containers above it.
- Three things worth knowing **before** replacing anything, all in that column:
  - **`SILENT`** — the bank turns this sound fully down and nothing raises it again.
    82 sounds across two characters are in that state, mostly foley; −96 dB is Wwise's
    floor and pulling a level to it is how a layer is switched off without being
    deleted. Replacing the audio there changes nothing anybody will hear.
  - **`1/9`** — it is one of nine alternatives the container picks between, so it is
    heard about one time in nine. Swapping it alone leaves the other eight.
  - **also played by** — the other events that reach the same audio, which one swap
    changes along with it.
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

A mute is not only a test-build idea — a muted sound is silenced in **every** `.bnk` the
tool writes, including a normal build and a volume build, because silencing a line you
do not want is a mod in its own right. Staging a file over a muted sound overrides the
mute; nothing is deleted, and unmuting brings the original back.
- **Silent banks**, for identifying a sound by its absence.

### Build

- Drop **wav, mp3, ogg, flac, m4a, aac, wma or wem** — anything that is not already a
  wem is encoded to Wwise Vorbis on the way in. No Wwise install, no ffmpeg.
- Conversions are **remembered**, so the same file is never encoded twice. Matched on
  the file's contents, so a copy or a rename still counts as the same audio and an
  edited file correctly does not. Settings › Importing shows what is held and can move
  or clear it. A second import of a 174 KB wem went 592 ms → 0 ms.
- Name files `{MediaID}-{your note}.wem`, or drop a single file onto a selection to give
  every one of them the same audio from one encode. **Set replacement…** takes any of
  those formats too, not only wem.
- **No skin selection needed** when audio is staged. A named file already says which
  sound it replaces, and the media id says which `.bnk` — so a folder that spans several
  characters builds in one go, which no single skin selection could express. Picking a
  skin is only required when there is nothing staged and you are building mutes alone.
- **Volume** per sound or in bulk, always applied to the original so setting it twice
  does not compound two lossy round trips. Hand-set levels survive a bulk change.
- Output mirrors the game's own paths. A sound that lives in a bank goes into the bank;
  one that does not ships loose at the exact path the game ships it, language folder and
  all.

### Mods

- **Open** someone else's `.bnk` files, or pick them out of a mod `.pak`, and see them
  diffed against the shipped ones — every entry marked `MODDED`, `NEW` or untouched.
  Several can be loaded at once; each overlays the game's copy of that one `.bnk`.
- The tree says which is which: a loaded `.bnk` and the hero and skin above it go
  **purple**, with a count of what the mod changed, so a collapsed character still shows
  that something inside it is modded.
- A `.bnk` two mods both claim belongs to the **newest one loaded**, the same way the
  last pak read wins in game. The one it displaced is named rather than dropped quietly.
- **Building with a mod loaded** uses that mod as the base, so its work stays in and
  yours goes on top. Close the mod to build against the game's original audio instead.
  Both of those warnings can be switched off with the checkbox on the warning itself.
- **Extract only the modded wems**, named so they drop straight back in.
- **Merge** two mods: each is diffed against the shipped bank so only what its author
  replaced carries over, and the first one you pick wins any collision.
- **Recompress .bnk** — re-encode a `.bnk`'s uncompressed PCM audio to Vorbis, around
  8× smaller. Only the audio changes; event ids, media ids and the sound hierarchy are
  untouched. An entry that would come out larger is left as PCM.

## Headless verbs

Everything is verifiable without a window, driving the same code the UI does.

```
XzoundWave.exe --selftest <skinId> [report.txt]        full pipeline check
XzoundWave.exe --build <skinId> <wemDir> <outDir> [--prefetch]
XzoundWave.exe --testbank <skinId> <clipDir> <outDir> [bankFilter]
XzoundWave.exe --openbank <bnk|pak> [rows]             diff a mod against the game
XzoundWave.exe --merge <modA> <modB> <outDir>          A wins collisions
XzoundWave.exe --import <file> <outDir>                any supported format -> wem
XzoundWave.exe --recompress <bnk> <outDir>             PCM bank -> Vorbis
XzoundWave.exe --volume <skinId> <wemDir> <outDir> <gain>
XzoundWave.exe --media <id> [<id>…] [--bank <name>]    where a media lives, and how
XzoundWave.exe --wwiseprobe <skinId> [event|?]          what a .bnk says about a sound
                                                       beyond its audio: the event
                                                       graph, loop counts, and the
                                                       volume/pitch the game adds
XzoundWave.exe --medialayout                           loose-media layout and overlap
XzoundWave.exe --banks                                 every bank, grouped
XzoundWave.exe --tts <outDir> <first> <last>           generate spoken numbers
XzoundWave.exe --usmap [--force]                       newest mappings from the depot
XzoundWave.exe --doctor                                what this machine provides
XzoundWave.exe --licences                              every notice inside the binary
```

Self-tests, each printing `ALL CHECKS PASSED` or a numbered failure:

```
--selftest  --seltest  --bulktest  --tags  --rowfilters  --menutest  --themetest
--mediapath  --playtest  --cachetest  --mutetest  --noskinbuild  --openorder  --modset
--prefetchpreview  --applied  --ingame
--roundtrip  --fmtcheck  --vorbistest  --setuproundtrip
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

MIT — see `LICENSE`.

Third-party components keep their own terms, set out in `THIRD-PARTY-NOTICES.md`. Two
of them ride inside the exe: the **aoTuV Vorbis codebooks** (Xiph BSD) and **vgmstream**
with its codec libraries, some of which are **LGPL**. Both notices are compiled in, so
they travel with the binary automatically — `XzoundWave.exe --licences` prints them, and
they are written out beside the unpacked vgmstream. If you redistribute the exe, that
obligation is already met; if you repackage it, keep them with it.

Marvel Rivals and its audio belong to NetEase and Marvel. This tool ships none of it.
