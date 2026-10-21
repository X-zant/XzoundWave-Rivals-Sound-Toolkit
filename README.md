# XzoundWave

A sound modding toolkit for Marvel Rivals. Find any sound in the game, hear it, replace it
with your own audio, and get a finished mod `.pak` back, ready for `~mods`. No Wwise, no
FModel exports, no repak.

- **Browse every sound** by character, skin and bank, with its event name, subtitle and
  category, and play it the way the game plays it.
- **Replace sounds** by dropping in wav, mp3, ogg, flac or almost any other audio. It is
  converted to the game's format for you.
- **Find out which sound is which** with numbered test builds that make every sound say
  its number in game.
- **Work with other people's mods:** open them, see what they changed, merge two together,
  pull their wems out, or bring an old mod up to date after a patch.
- **Add new voice lines** to a character, for kills, ults and other triggers.
- **Add a sound to an event** without replacing anything, such as music that plays under
  an ult.

## Download

Get the latest version from the
[Releases page](https://github.com/X-zant/XzoundWave-Rivals-Sound-Toolkit/releases).

| | Download | Needs |
| --- | --- | --- |
| **Windows** | `XzoundWave.exe` | the [.NET 10 Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0); Windows offers it on first start if it is missing |
| **Linux** | `XzoundWave-linux-x64.tar.gz` (the app) | nothing |
| **Linux, no desktop** | `xzoundwave-cli-linux-x64.tar.gz` (command line only) | nothing; see [Command line](#command-line) |

Each is a single program. On Linux it comes in a `.tar.gz`, which keeps it executable;
extract it and run it:

```bash
tar -xzf XzoundWave-linux-x64.tar.gz
```

```bash
./XzoundWave
```

The first time you use voice lines, the app downloads the Oodle library they need and keeps
it next to the program.

## First run

1. Open the **Setup** tab.
2. **Paks folder**: found for you when the game is installed through Steam, including
   Steam libraries on other drives, Proton installs and a Steam Deck's SD card. Otherwise
   point it at `…\MarvelRivals\MarvelGame\Marvel\Content\Paks`.
3. **AES key**: the game's 64-character hex key (starting `0x`), the same one FModel asks
   for. It is posted in the Marvel Rivals modding community; the app does not include it.
4. **usmap**: **More › Fetch latest mappings** downloads the newest one for you.
5. **Language**: which voice lines and subtitles to show.
6. **Load game.**

After the first successful load, the game loads by itself every time you open the app.
Settings are kept in `%AppData%\XzoundWave` on Windows and `~/.config/XzoundWave` on Linux.

## Using it

The app has one tab per job. **Voice lines…**, **Columns** and **Settings…** sit on the
right of every tab, next to the project button (it shows your project's name, or
**Untitled ▾** until you save).

### Browse

Pick a character, skin and bank on the left (or one of the map, menu, music and announcer
banks), and every sound in it is listed with its event name, media id, subtitle and
category. Ctrl+click several banks to see them together.

- **Play** and **Autoplay** to listen down the list. Turn on **Ingame** to hear a sound
  with the volume, pitch, delay and looping the game adds to it.
- The **Details** panel shows what the game does to the selected sound, its alternatives
  and how often each plays, and where its audio lives. This explains why a replacement can
  come out quieter, late or looping when your file is not.
- **English categories** translates the category column, which the game only has in
  Chinese.
- Rows are tinted: green for sounds you replaced, red for muted, purple for sounds a mod
  changed.
- Type your own notes on a sound in the **Notes** column. The filter box searches them
  too.
- Drag a column header's edge to resize it; the app remembers the widths.
- Right-click › **Extract wem(s)…** or **Extract as WAV…** writes the selected sounds out of
  the game as `{media id}-{event}` files; drop one back in and it stages onto the same sound.
- Right-click a sound › **Add a sound to this event…** to play a file of yours once every
  time that sound's event plays, alongside what it already plays. Nothing of the game's is
  replaced, and your file comes out of the same bus at the same level as the event's own
  sound. If the sound you picked is one the game has muted, or what you hear sits in a random
  container, the app copies a sound you can actually hear. It plays beside the game's sound, never
  instead of it, even where the game lets a character play only one sound at a time. If the game
  stops that sound (an ult ending, say), you choose whether yours stops there too, with the same fade. Added sounds are listed under **Added sounds** in the tree.
  Drop a file on one to change it, or right-click › **Remove added sound**.

### Test

To find out which sound plays when, build a test mod, play the game, and listen.

- **Numbered test .bnk…** makes every sound in a bank say its number, with a legend that
  maps numbers back to sounds. Each sound's number also shows in the **Test #** column.
- **Test the selection…** and **Test only the selection…** number just the sounds you
  picked; the second silences the rest of the bank, which helps when several sounds play
  at once.
- **Silent .bnk…** silences the selected sounds, so you can find one by its absence.
- Right-click a sound and choose **Mute** to silence it in every build, test or real. Muting
  a line is a mod in its own right.

Test builds come out as a `.pak`; use **Build › Install to ~mods** to try one. Delete the
test pak from `~mods` when you are done, or it keeps playing numbers alongside your real
mod.

### Build

- **Stage your audio:** name each file `{MediaID}-{any note}.wav` (any audio format works)
  and drop it anywhere on the window. The media id is the number in the **Media ID**
  column; right-click › **Copy media id(s)** copies it. You can also select sounds and
  drop one file on them, or use **Staging › Set replacement…**.
- **Build mod** writes a finished `.pak`. If you added new voice lines, it writes the
  `.utoc/.ucas/.pak` set they need.
- **Install to ~mods** copies the last build into the game's `~mods` folder.
- **Volume** sets the level of single sounds or all of them; the **Vol** column shows each
  level, with a `*` on ones you set by hand. **Build at these volumes…** makes one `.pak`
  per level (for example `0.6, 0.8, 1.0`) so players can choose.
- **Output** can split a build into one `.pak` per character, or name and sort the paks
  with a SoundKit `0-PAK-NAME-LIST.txt`.

### Mods

- **Open mod** loads a `.pak`, `.bnk`, `.utoc` or unpacked mod folder, or drop one or several
  onto the window. Its sounds are marked as modified or new, and builds go on top of it.
  When two open mods change the same `.bnk`, the one opened last wins, as in game.
  **Show only changed** hides the rest.
- **Mod tools:**
  - **Update mod…** moves a mod made for an older patch onto the current one, following
    each sound rather than its id.
  - **Merge mods…** combines two mods into one `.pak`. The one you pick first wins any
    sound both changed, and the clashes are listed.
  - **Extract modded wems…** writes out the sounds a mod changed, next to the game's
    originals, named so they drop straight back in.
  - **Organize wems…** sorts a folder of wems into one folder per mod, by character, skin
    and bank or with a SoundKit `0-WEM-FOLDER-NAME-LIST.txt`.
  - **Recompress…** shrinks uncompressed audio in a `.bnk`, a `.pak` or loose wems,
    usually around 8× smaller.

### Voice lines

**Voice lines…** adds brand-new lines to a character's voice set: kill lines for a
specific hero, ult lines, buffs, health packs and the rest of the game's triggers. Write
the subtitle and choose who hears it, then press **Stage it in the main window** and drop
an audio file on the line.

### Projects

The project button saves your work to an `.xzw` file and opens it again later: staged
files, notes, mutes, volume levels, test numbers, new voice lines, added sounds, the mods
you had open (with the `.bnk` files you took from each), and the skin and bank you had open.
After a game patch, staged files and notes follow their sound to its new id.

A built mod also records its new voice lines and added sounds inside each `.bnk` it
changes. Building again on top of it replaces them instead of adding them twice, and
**Update mod…** puts added sounds back onto the patched bank.

## Audio formats

wav, mp3, ogg, flac, m4a, aac, wma, opus, aiff, mp4, mp2, ac3 and wem all import. Anything
that is not already a wem is converted to the game's Vorbis format. Converted files are
remembered, so the same file is never converted twice.

## Troubleshooting

- **No sound when previewing:** open **Settings › Diagnostics**. It says what is wrong and
  has a Copy button for asking for help.
- **Windows:** the audio decoder needs the
  [Microsoft Visual C++ Redistributable (x64)](https://aka.ms/vs/17/release/vc_redist.x64.exe),
  and stops working if antivirus removes one of its files.
- **Linux:** sound plays through PortAudio, PipeWire, PulseAudio or ALSA, whichever the
  machine has. The app needs a desktop (X11, or Wayland through XWayland). If it will not
  open on a minimal install, add `libx11-6`, `libice6`, `libsm6` and `libfontconfig1`.
- **A replacement plays quieter, late or on a loop:** the game applies its own volume,
  pitch, delay and looping on top of the audio. The Details panel shows what, and the
  Ingame preview lets you hear it.
- **The game was not found:** set **Paks folder** on the Setup tab by hand.

## Command line

The main tools also run without the window, for scripts and batch work: building, test
banks, volume builds, merging, updating, recompressing, organizing and converting. Run
`XzoundWave.exe --help` on Windows or `./xzoundwave --help` on Linux for the full list.
The ones you are most likely to want:

```
--build <skinId> <wemDir> <outDir>          build a mod from a folder of named wems
--merge <modA> <modB> <outDir>              combine two mods; A wins clashes
--updatemod <mod> <outDir>                  bring an old mod onto the current patch
--addlayer <bank> <event> <audio> <outDir>  add a sound beside what an event plays
--recompress <bnk|wem|pak|folder> <outDir>  shrink uncompressed audio
--organizewems <inDir> <outDir> [list.txt]  sort wems into one folder per mod
--import <file> <outWem>                    convert any audio to a wem
--usmap                                     download the newest mappings
--doctor                                    check what this machine provides
```

The commands that make a mod write a finished `.pak`, the same as the app.

With no window to set things up in, put the game folder and key in
`~/.config/XzoundWave/settings.json` (`%AppData%\XzoundWave\settings.json` on Windows):

```json
{ "PaksDir": "/path/to/MarvelRivals/MarvelGame/Marvel/Content/Paks", "AesKey": "0x…" }
```

If the app has been set up on the same machine, the command line already uses its
settings.

## Building from source

See [docs/BUILDING.md](docs/BUILDING.md).

## Licence

XzoundWave is MIT licensed; see `LICENSE`. The components it bundles keep their own
licences, listed in `THIRD-PARTY-NOTICES.md`. Their notices are built into the program and
`--licences` prints them.

Marvel Rivals and its audio belong to NetEase and Marvel. XzoundWave ships none of it; it
reads the copy of the game you already have.
