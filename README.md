# XzoundWave

A sound toolkit for Marvel Rivals. Browse, preview, annotate and re-pack the game's
Wwise soundbanks.

Standalone in the literal sense: the release is **one `XzoundWave.exe`** on Windows, and
on Linux **one `XzoundWave`** (the window) or **one `xzoundwave`** (the same engine with no
window, for scripts and servers). Nothing to install, nothing to unzip beside it, no
folders exported from FModel first. The Vorbis encoder and vgmstream both ship inside the
binary.

## Running it on Windows

Download `XzoundWave.exe` and run it.

The one thing it does not carry is the **[.NET 10 Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0)**,
the plain one: the Desktop Runtime is not needed.
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

## Running it on Linux

Two downloads, the same engine in both:

- **`XzoundWave`** is the window, the same tabs as on Windows. Make it executable and run
  it from your desktop. It needs a desktop session (X11, or Wayland through XWayland),
  which every Linux desktop already is; on a bare server image the missing pieces are
  `libx11-6`, `libice6`, `libsm6` and `libfontconfig1`. It brings its own fonts, Inter for the interface and
  Noto Sans SC for the game's Chinese, so it looks the same on every distribution and the
  Category column is readable whether or not the machine has a Chinese font.
- **`xzoundwave`** is the engine without the window: every verb under
  [Headless verbs](#headless-verbs), driving exactly the code the window drives, and the
  one to use over SSH or in a script. The verbs that make a mod (`--build`, `--merge`,
  `--updatemod`, ...) write a finished `.pak` beside their output, the same as the window.
  The window's binary takes the same verbs, so the smaller download is only needed where
  there is no desktop.

Nothing to install at all here, not even the runtime: both carry .NET inside them, which
is why they are 57 MB and 76 MB. The platform that could not assume a runtime is the one
that had to bring its own.

`./xzoundwave --doctor` says what the machine provides, and writes the same report to
`~/.config/XzoundWave/diagnostics.txt`. Settings, the unpacked tools and the optional
dictionary live in that folder, where `%AppData%` would be on Windows. vgmstream unpacks
itself there and runs: the Linux payload is one statically linked binary, so there is no
redistributable to install and no codec libraries beside it to go missing.

Sound is the one thing that cannot ride inside the binary, because handing samples to a
device is the machine's own business. So `xzoundwave` uses PortAudio where the machine
has it, and otherwise pipes raw PCM to `pw-play`, `paplay` or `aplay`, whichever is on
the PATH. A desktop that can already play a sound needs nothing installed for this one
either. `./xzoundwave --doctor` names the output it found, or what to install if it found
none, and `./xzoundwave --playtest` decodes a real sound and plays it, which separates a
decoding problem from a playback one.

**mp3, m4a, aac, wma and flac are decoded by the bundled vgmstream**, the same decoder
this build already unpacks and runs, so it imports those five rather than turning them
away. Only wav and Ogg Vorbis are read inside the process, and that is true on both
platforms. `./xzoundwave --decode` prints the route every extension takes and checks it.

Spoken numbers need no voice installed. They used to come from the Windows speech engine,
which is what made numbered test banks a Windows feature; the words are now recorded once
and composed from thirty clips inside the binary, so `--tts` and `--testbank` work here
and write the same files.

The game is looked for under `~/.local/share/Steam`, `~/.steam/steam`, `~/Games/Steam`
and the Flatpak and Snap trees, and then in every library Steam lists in its own
`libraryfolders.vdf`, so a game on a second drive or a Steam Deck's SD card is found too.
An install played through Proton is found where a native one would be.

Numbers printed for a reader come out plain: a size reads `937,404` here, where a Windows
machine set to German reads `937.404`. That is the visible half of a deliberate trade.
This build is published with globalization switched off, so that it neither depends on
an ICU library being on the machine nor has to carry one, and a download that installs
nothing cannot afford either. Nothing the tool *writes* rests on it: legends, folder
names and project files come out the same under any locale on either platform.

Everything the tool builds is byte for byte the same. From the same inputs the two
builds produce the same output, checked by SHA-256: the wem from an `--import` of a wav
(46,418 B), the `.bnk` from a `--build` (993,292 B), all four banks of a `--testbank`,
and `--selftest`, which rebuilds the game's own banks and reports `IDENTICAL` on both.
The legend beside a test bank and the translated category table match too, once line
endings are normalised, because a text file is written with the host's own newline. The
build log beside them is the one thing that does not: it states sizes, and sizes are
printed for a reader.

There is one exception, and it lives in the decoder rather than in anything this tool
writes. The two downloads carry different vgmstream builds, r1980 here and r2117 on
Linux, linked against different versions of FFmpeg, and the two round the last bit of a
sample differently. Decoding one of the game's own sounds on both and comparing: every
byte of the header agrees, a quarter of the samples differ, and none differs by more
than 1 part in 32,768, which is about 90 dB below full scale and inaudible. It reaches
whatever vgmstream decodes, so an mp3, m4a, aac or wma import, and the volume tool,
which decodes before it re-encodes. It does not reach a wav or an Ogg Vorbis import,
which are read in this process instead, and it does not reach any bank, because the
audio in a mod is the wem you gave it copied through. Two people on the two platforms
building the same mod from the same files get the same bytes. The same two re-levelling
the game's own audio get files that measure the same and sound the same, but do not hash
the same.

## Build

```
dotnet build -c Release
```

That is the whole story, and `XzoundWave.slnx` beside it is what makes it true: without
a solution file the command resolved to the one csproj at the root, which is the old WPF
window, and never built the Avalonia one at all.

Dependencies come from NuGet; there are no submodules, no sibling checkouts and no paths
into another project.

### Which XzoundWave.exe is which

The window is built twice, once for each target framework, and the two land in different
folders. Run the one for the platform you are on:

| Build | Run it on |
| --- | --- |
| `XzoundUI/bin/Release/net10.0-windows/XzoundWave.exe` | Windows |
| `XzoundUI/bin/Release/net10.0/XzoundWave.exe` | Linux and macOS |

The suffix is not decoration. NAudio's WaveOut is packaged for a windows target
framework alone, so it is only reachable from the build that has one; the portable build
looks for PortAudio and then for a system player, finds neither on a stock Windows
machine, and is silent. Both builds are the same source and the same window.

`bin/Release/net10.0-windows/win-x64/XzoundWave.exe` at the repo root is a third file
with the same name: that is the old WPF window, kept for reference.

### Publishing

A publish rather than a build, because it has to take the native libraries and, on
Linux, the runtime with it. The window targets two frameworks, so one has to be named:

```
dotnet publish XzoundUI/XzoundUI.csproj   -c Release -f net10.0-windows -r win-x64 --self-contained false
dotnet publish XzoundUI/XzoundUI.csproj   -c Release -f net10.0 -r linux-x64 --self-contained
dotnet publish XzoundCli/XzoundCli.csproj -c Release -r linux-x64 --self-contained -p:EnableCompressionInSingleFile=true
```

`--self-contained false` is spelled out because a runtime identifier on its own is taken
as a request to bundle the runtime, which more than doubles the 60 MB exe to save the
reader an install they have almost certainly already done. The Windows binary asks for .NET 10,
and the plain runtime rather than the Desktop one is enough for it.

The Linux binaries do carry their runtime, because "nothing to install" is the point of
that release. It is the runtime identifier and not the framework that turns globalization
off for them, which is why the Windows line keeps it and the two Linux ones do not. The
same runtime identifier is what puts the Chinese font into the Linux window and keeps it
out of the Windows one (Inter is in both, because the headless renderer depends on it,
and only the Linux window uses it), and what compresses the Linux window: 76 MB, against
136 MB without compression. The CLI line asks for compression on the command line, which
keeps it to 57 MB.

Each publish is the one file and nothing beside it. The projects leave out the debug
symbols the Skia packages would otherwise drop next to the exe (about 100 MB of them), and
UAssetTool's own runtimeconfig and launcher, which this app never uses because it calls
UAssetTool as a library.

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
`--usmap [--force]` does the same from a console.

vgmstream used to be on this list and no longer is: it unpacks itself out of the binary.
Settings › Tools can still point at your own build of it.

The Paks folder and a `*.usmap` sitting beside the program are found automatically on
first run. Settings, the unpacked tools and the optional dictionary live in
`%AppData%/XzoundWave/`, or `~/.config/XzoundWave/` on Linux; your work lives in a `.xzw`
project file wherever you put it.
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
- **What the game does to each sound**, in the **Ingame** column: the volume trim, pitch
  shift, delay, loop or runtime curve the `.bnk` applies on top of the audio. None of
  it is in the file, and a swap inherits all of it — which is why a replacement can
  come out quieter, late or looping when the file plainly is not. Hover for the
  breakdown, including what it inherits from the containers above it.
- Three things worth knowing **before** replacing anything, all in that column:
  - **`SILENT`** — the bank turns this sound fully down and nothing raises it again.
    82 sounds across two characters are in that state, mostly foley; −96 dB is Wwise's
    floor and pulling a level to it is how a layer is switched off without being
    deleted. Replacing the audio there changes nothing anybody will hear.
  - **Group**, second form — `F27 of 4`: sounds with no alternatives of their own but
    mixed together under one actor-mixer, which is how a set like `left_release1` …
    `left_release4` is knowable as a set without reading the names. It is a mixing
    group and **not** an order: Wwise states an order with a sequence container, and
    this game uses none, so which one plays first is the game's own logic.
  - **Group** (its own column) — `V85 6/6 29%`: which set of alternatives the sound
    belongs to, which one it is, and how often it is picked. Sort by it and a group
    gathers; hover to see every member and its share. The shares are the container's
    own playlist weights read from the `.bnk`, not an assumption — every random
    container in the character banks carries one, and where a bank does not the split
    is marked as assumed rather than stated. Most are even, but not all: Moon Knight's
    `ftp_foley2` has five alternatives at 14.3% and a sixth at 28.6%.
  - **also played by** — the other events that reach the same audio, which one swap
    changes along with it.
- **Tags** from a shared vocabulary (`[ULT]`, `[MVP]`, `[LOOP]`, `[3D]` …) picked from a
  menu and never typed, so two people's notes mean the same thing. Free-text notes sit
  alongside. Both survive a patch: they re-link by event name when media ids change.
- Columns whose text carries a number **sort by the number**: `V2` before `V10`, `#2`
  before `#100`, `3.7s` before `12.0s`. The label is only a label.
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

- Drop **wav, mp3, ogg, flac, m4a, aac, wma or wem** — or opus, aiff, mp4, mp2, ac3 and
  the rest of what the decoder declares it reads, twenty extensions in all, which
  `--decode` lists. Anything that is not already a wem is encoded to Wwise Vorbis on the
  way in. No Wwise install, no ffmpeg.
- Conversions are **remembered**, so the same file is never encoded twice. Matched on the
  file's contents and on the decoder that produced them, so a copy or a rename still
  counts as the same audio, an edited file correctly does not, and a change of decoder is
  never served an older one's bytes. Settings › Importing shows what is held and can move
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

Everything is verifiable without a window, driving the same code the UI does. Put
`XzoundWave.exe` in front of these on Windows, or `./xzoundwave` on Linux. The verbs
themselves are the same on both.

```
--selftest <skinId> [report.txt]        full pipeline check
--build <skinId> <wemDir> <outDir> [--prefetch]
--testbank <skinId> <clipDir> <outDir> [bankFilter]
--openbank <bnk|pak> [rows]             diff a mod against the game
--merge <modA> <modB> <outDir>          A wins collisions
--updatemod <mod> <outDir> [--made-for <version>] [--history-dir <dir>]
                                        an old mod onto the current banks
--organizewems <inDir> <outDir> [0-WEM-FOLDER-NAME-LIST.txt]
                                        one folder per mod
--snapshot [paksDir]                    record this game version for updates
--history [dir]                         the versions and SoundKit lists known
--importsubs <soundKitFolder>           SoundKit's old subtitle lists
--import <file> <outWem>                any supported format -> wem
--recompress <bnk|wem|pak|folder> <outDir>
                                        PCM -> Vorbis
--volume <skinId> <gain> <outDir> [bankFilter]
--voicelinetest <audio> <outDir>        a new voice line, built and read back
--media <id> [<id>…] [--bank <name>]    where a media lives, and how
--wwiseprobe <skinId> [event|?]         what a .bnk says about a sound beyond
                                        its audio: the event graph, loop
                                        counts, and the volume/pitch the game
                                        adds
--medialayout                           loose-media layout and overlap
--banks                                 every bank, grouped
--tts <outDir> <first> <last>           generate spoken numbers
--usmap [--force]                       newest mappings from the depot
--doctor                                what this machine provides
--licences                              every notice inside the binary
```

Self-tests, each printing `ALL CHECKS PASSED` or a numbered failure:

```
--selftest  --seltest  --bulktest  --tags  --rowfilters  --updatetest  --music
--mediapath  --playtest  --cachetest  --mutetest  --noskinbuild  --openorder  --modset
--prefetchpreview  --applied  --ingame  --sorting
--roundtrip  --fmtcheck  --vorbistest  --setuproundtrip
--platform  --culture  --pipeaudio
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
of them ride inside the binary: the **aoTuV Vorbis codebooks** (Xiph BSD) and
**vgmstream** with its codec libraries, some of which are **LGPL**. The Linux build
carries a third, the **.NET runtime** itself, which the Windows one leaves to the
machine. The window adds the native libraries it draws with (**Skia**, **HarfBuzz** and,
on Windows, **ANGLE**, with what is linked into them) and, on Linux, its **fonts** (SIL
Open Font License). All of those notices are compiled in, so they travel with the binary
automatically — `--licences` prints them, and they are written out beside the unpacked
vgmstream. If you redistribute the binary, that obligation is already met; if you
repackage it, keep them with it.

Marvel Rivals and its audio belong to NetEase and Marvel. This tool ships none of it.
