# Third-party notices

XzoundWave is MIT licensed (see `LICENSE`). The components below are not, and their
terms travel with any copy you distribute.

XzoundWave ships as one file per platform: `XzoundWave.exe` on Windows, `xzoundwave` on
Linux. Everything down to **Supplied by you** is **inside that file**: the aoTuV
codebooks are compiled into `XzoundCore.dll`, vgmstream is an embedded zip written out to
your app data the first time it is needed, the spoken word clips are embedded resources,
and the NuGet packages are folded in with the rest by the single-file publish. The Linux
build is self-contained and carries the .NET runtime as well; the Windows one asks the
machine for it. What is listed under **Supplied by you** is never bundled at all.

---

## aoTuV 6.03 codebooks — embedded

`XzoundCore/Vorbis/packed_codebooks_aoTuV_603.bin` is compiled into `XzoundCore.dll`.

Wwise's runtime carries a fixed table of Vorbis codebooks and a shipped setup packet
names them by index rather than spelling them out, which is why one is ~200 bytes
instead of ~3 KB. Encoding a file the game can read therefore means holding the same
table. It is data from aoTuV, a tuned fork of Xiph.Org's libvorbis, and it is
redistributed here under libvorbis's licence:

```
Copyright (c) 2002-2020 Xiph.org Foundation

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

- Redistributions of source code must retain the above copyright
  notice, this list of conditions and the following disclaimer.

- Redistributions in binary form must reproduce the above copyright
  notice, this list of conditions and the following disclaimer in the
  documentation and/or other materials provided with the distribution.

- Neither the name of the Xiph.org Foundation nor the names of its
  contributors may be used to endorse or promote products derived from
  this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE FOUNDATION
OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

This notice satisfies the binary-redistribution condition. If you ship XzoundWave or
`XzoundCore.dll` onward, ship this file with it.

## vgmstream — embedded, unpacked on first use

vgmstream is embedded in XzoundWave and written out to your app data the first time
playback, duration measurement or volume scaling needs it. A build carries only the copy
for the platform it was built for.

On Windows that is `vgmstream-cli.exe` and the codec libraries it loads, into
`%AppData%/XzoundWave/vgmstream/`. They are **unmodified** binaries from the official
win64 release of [vgmstream](https://github.com/vgmstream/vgmstream), with the Winamp and
XMPlay plugins removed because the command-line tool does not use them. vgmstream's own
`COPYING` is unpacked alongside them.

On Linux it is one file, `vgmstream-cli`, into `~/.config/XzoundWave/vgmstream/`. It is
vgmstream's own r2117 build for linux64 and it is statically linked: the same codec
components are inside that single binary rather than sitting beside it, and it brings no
`COPYING` of its own, so this file is what states their terms. Nothing here modifies it.

XzoundWave runs `vgmstream-cli` as a separate process. It does not link against it, and
the two remain separate programs.

The bundle includes components under the **LGPL**, which is worth stating plainly
because it is the strongest condition attached to anything XzoundWave redistributes:

| Component | Licence |
| --- | --- |
| vgmstream itself | ISC |
| `avcodec` / `avformat` / `avutil` / `swresample` (FFmpeg) | LGPL 2.1+ |
| `libmpg123-0` | LGPL 2.1 |
| `libvorbis` | BSD 3-clause (Xiph.Org) |
| `libatrac9`, `libcelt`, `libg719_decode`, `libspeex` | see vgmstream's `COPYING` |

The LGPL requires that these libraries can be replaced. On Windows they can be replaced
one at a time: they sit on disk as ordinary `.dll` files in the folder above, and
swapping one for your own build works without touching XzoundWave. In the Linux binary
they are linked in, so what gets replaced is vgmstream itself. Its source and its builds
are published by the project at the link above, and because XzoundWave only ever runs it
as a separate process, pointing Settings › Tools at your own `vgmstream-cli` is the whole
of the swap. Nothing here is a modified build.

## The .NET runtime, in the Linux build only

The Linux release is self-contained: the runtime and its class libraries are published
into `xzoundwave` rather than installed on the machine, which is what makes that one file
about 53 MB and "nothing to install" true there. It is
[.NET](https://github.com/dotnet/runtime), **MIT** licensed, Copyright (c) .NET
Foundation and Contributors.

The Windows release carries none of it and asks the machine for the .NET Desktop Runtime
instead, so this section does not apply to `XzoundWave.exe`.

## Spoken number clips, recorded once and embedded

Thirty short `.wav` files in `assets/numberwords/` are compiled into `XzoundLogic.dll`:
the words zero to nineteen, the tens, "hundred" and "thousand". Every number a test bank
speaks is composed from those thirty, which is what lets numbered test banks work on a
machine with no speech voice.

They are taken from nobody. They are the output of the Windows speech voice on the
machine that ran `--numberclips`, recorded once and checked in. The speech engine and its
voice data stay where they are; what travels in the binary is the audio they produced.

## NuGet packages, compiled into the binary

Restored at build time, then folded into the single file by the publish, so a copy you
pass on contains them:

| Package | Licence |
| --- | --- |
| [CUE4Parse](https://github.com/FabianFG/CUE4Parse) | Apache-2.0 |
| [NAudio](https://github.com/naudio/NAudio) | MIT |
| [NVorbis](https://github.com/NVorbis/NVorbis) | MIT — Copyright (c) 2020 Andrew Ward |
| [OggVorbisEncoder](https://github.com/SteveLillis/.NET-Ogg-Vorbis-Encoder) | MIT — Steve Lillis |
| Microsoft.Bcl.Memory | MIT |

## Supplied by you — never bundled

**The Marvel Rivals AES key.** Yours to obtain and yours to keep. It is not in this
repository, not in any release, and is stored only in your own `%AppData%`.

**The `.usmap` mappings file.** Per patch, from the modding community.

**Whatever plays the sound**, on the builds that do not use Windows' own WaveOut.
XzoundWave loads PortAudio if the machine already has it (`libportaudio.so.2`), and
otherwise runs `pw-play`, `paplay` or `aplay` if one of those is on the PATH. No copy of
any of them is shipped or written out, so none of their terms attaches to a copy of
XzoundWave, and a machine with none of them loses playback and nothing else.

**Audiokinetic Wwise** (`WwiseConsole.exe` and a `.wproj`), optional, only for
generating test clips. Nobody bundles it; Audiokinetic's licence does not allow it.
See `docs/wwise-setup.md`.

**[CC-CEDICT](https://www.mdbg.net/chinese/dictionary?page=cc-cedict)**, the optional
offline Chinese–English dictionary. Licensed **CC BY-SA 4.0** by MDBG. It is
deliberately *not* shipped: share-alike terms would attach to whatever it travels
with. XzoundWave downloads it on request into your `%AppData%` and shows the licence
before doing so. If you choose to redistribute it, CC BY-SA 4.0 applies to your
distribution.

## Game content

Marvel Rivals and its audio are the property of NetEase and Marvel. This tool ships
none of it. It reads the copy you already own, and anything you export or build from
it stays yours to handle responsibly.
