# Building XzoundWave

## Build

Needs the .NET 10 SDK.

```
git clone --recurse-submodules https://github.com/X-zant/XzoundWave-Rivals-Sound-Toolkit.git
cd XzoundWave-Rivals-Sound-Toolkit
dotnet build -c Release
```

UAssetToolRivals, which writes every `.pak` and the voice-line bundles, is a git submodule
in `vendor/UAssetToolRivals`. In a clone made without `--recurse-submodules`, run
`git submodule update --init` first. The libraries come from NuGet; vgmstream, the Linux
window's Chinese font and the native libraries' notices are checked in under `vendor/`.

`XzoundWave.slnx` is what `dotnet build` picks up. Without it the command resolves to the
one `.csproj` at the repo root, which is the old WPF window, and never builds the
Avalonia one.

### Projects

| Project | What it is |
| --- | --- |
| `XzoundCore` | the Wwise Vorbis encoder, no UI and no Windows API, usable on its own |
| `XzoundLogic` | the engine: paks, soundbanks, building, every command-line verb |
| `XzoundUI` | the window (Avalonia), assembly name `XzoundWave` |
| `XzoundCli` | the engine with no window, assembly name `xzoundwave` |

### Which XzoundWave.exe is which

The window targets two frameworks, and the builds land in different folders:

| Build | Run it on |
| --- | --- |
| `XzoundUI/bin/Release/net10.0-windows/XzoundWave.exe` | Windows |
| `XzoundUI/bin/Release/net10.0/XzoundWave` | Linux and macOS (built there) |

The `-windows` build is the only one that can reach NAudio's WaveOut. A Windows build also
leaves an `XzoundWave.exe` in `net10.0/`; it runs, but plays no sound. `bin/Release/net10.0-windows/win-x64/XzoundWave.exe`
at the repo root is a third file of the same name: the old WPF window, kept for reference.

## Publishing

```
dotnet publish XzoundUI/XzoundUI.csproj   -c Release -f net10.0-windows -r win-x64 --self-contained false
dotnet publish XzoundUI/XzoundUI.csproj   -c Release -f net10.0 -r linux-x64 --self-contained
dotnet publish XzoundCli/XzoundCli.csproj -c Release -r linux-x64 --self-contained -p:EnableCompressionInSingleFile=true
```

Each publish is one file and nothing beside it:

| Output | Size | Notes |
| --- | --- | --- |
| `XzoundWave.exe` (Windows) | ~60 MB | asks the machine for the plain .NET 10 Runtime; `--self-contained false` is spelled out because a runtime identifier alone bundles the runtime and more than doubles the file |
| `XzoundWave` (Linux window) | ~76 MB | self-contained and compressed (136 MB uncompressed); carries Noto Sans SC |
| `xzoundwave` (Linux command line) | ~57 MB | self-contained and compressed |

The runtime identifier, not the framework, decides the Linux-only parts: invariant
globalization, the Noto Sans SC font, and compression of the window. The projects drop
the debug symbols the Skia packages would leave beside the exe (about 100 MB) and
UAssetTool's own runtimeconfig and launcher, since the app calls UAssetTool as a library.

### Packing the Linux release

A browser download drops the executable bit, so each Linux binary ships in a `.tar.gz`,
which keeps it. Pack on Linux (or WSL, on its own file system rather than `/mnt/…`, where
every file looks executable), so the mode in the archive is a real one:

```
chmod 755 XzoundWave xzoundwave
tar --owner=0 --group=0 --numeric-owner -czf XzoundWave-linux-x64.tar.gz XzoundWave
tar --owner=0 --group=0 --numeric-owner -czf xzoundwave-cli-linux-x64.tar.gz xzoundwave
```

The two archive names also keep the uploads apart: `XzoundWave` and `xzoundwave` differ only
in case, which Windows, and possibly a release page, treats as one name.

## Platform notes

**Fonts.** The Linux window uses Inter as its interface font and Noto Sans SC as the
fallback for Chinese, so it looks the same on every distribution (`XzoundUI/Fonts.cs`).
Windows keeps Segoe UI. Inter is in every window build anyway, because Avalonia.Headless
depends on it.

**Globalization.** The Linux builds run with globalization off, so they neither need ICU
on the machine nor carry it (about 30 MB). Nothing the tool writes depends on it, because
it sorts with ordinal comparers; the only visible effect is that numbers printed for a
reader are not locale-styled.

**Same bytes on both platforms.** From the same inputs the Windows and Linux builds write
identical files: imported wems, built and test banks, merges, updates, recompressed paks,
and `--selftest`'s rebuilds of the game's banks. Text files differ only in line endings.
The one exception is anything decoded by vgmstream first (every import except wav, ogg
and wem, and the volume tool). The two platforms ship different vgmstream builds (r1980 on Windows,
r2117 on Linux) that round the last bit of a sample differently, about 90 dB below full
scale.

**Audio output.** Windows uses NAudio's WaveOut. Linux uses PortAudio where present,
otherwise pipes PCM to `pw-play`, `paplay` or `aplay`.

**The window on Linux** needs X11 (or XWayland). A bare server image also needs
`libx11-6`, `libice6`, `libsm6` and `libfontconfig1`.

## Tests

Every feature can be checked without a window. On Windows run these against
`XzoundWave.exe`, on Linux against `./xzoundwave`; `--help` lists every verb.

`--selftest <skinId>` is the full pipeline check: it rebuilds a skin's banks from their
own audio and reports each one `IDENTICAL` to the game's. The rest each print
`ALL CHECKS PASSED` or a numbered failure:

```
--seltest  --bulktest  --tags  --rowfilters  --updatetest  --music  --variations
--mediapath  --playtest  --cachetest  --mutetest  --noskinbuild  --openorder  --modset
--prefetchpreview  --applied  --ingame  --sorting  --decode  --voicelinetest  --numbers
--roundtrip  --voicelinecheck  --platform  --culture  --pipeaudio
```

`--setuproundtrip` prints `ROUND TRIP IDENTICAL`; `--vorbistest` and `--fmtcheck` print a
report and exit non-zero on failure.

The window has its own headless checks: `XzoundWave --uitest` lays out the window and
asserts on it, and the `--shot*` verbs render it to PNG with no display attached.

## The encoder

`XzoundCore` writes Wwise Vorbis from scratch: no reference file to copy fields from, no
Wwise install.

The detail that took longest to find: `uMaxPacketSize`, at offset `0x1e` of the 48-byte
fmt extension that follows the 18-byte WAVEFORMATEX (`0x18` into `AkVorbisInfo`), is
required. The runtime sizes its packet buffer from it, so a zero
there means every packet overflows a zero-length buffer and the sound plays as silence,
with no error reported anywhere.
