# Third-party notices

XzoundWave is MIT licensed (see `LICENSE`). The components below are not, and their
terms travel with any copy you distribute.

XzoundWave ships as a single `.exe`. The first two sections below are **inside that
file**: the aoTuV codebooks are compiled into `XzoundCore.dll`, and vgmstream is an
embedded zip written out to your app data the first time it is needed. The rest either
arrive as NuGet packages at build time or are supplied by you and never bundled.

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

`vgmstream-cli.exe` and the codec libraries it loads are embedded in XzoundWave.exe and
written to `%AppData%/XzoundWave/vgmstream/` the first time playback, duration
measurement or volume scaling needs them. They are **unmodified** binaries from the
official win64 release of [vgmstream](https://github.com/vgmstream/vgmstream), with the
Winamp and XMPlay plugins removed because the command-line tool does not use them.
vgmstream's own `COPYING` is unpacked alongside them.

XzoundWave runs `vgmstream-cli.exe` as a separate process. It does not link against it,
and the two remain separate programs.

The bundle includes components under the **LGPL**, which is worth stating plainly
because it is the strongest condition attached to anything XzoundWave redistributes:

| Component | Licence |
| --- | --- |
| vgmstream itself | ISC |
| `avcodec` / `avformat` / `avutil` / `swresample` (FFmpeg) | LGPL 2.1+ |
| `libmpg123-0` | LGPL 2.1 |
| `libvorbis` | BSD 3-clause (Xiph.Org) |
| `libatrac9`, `libcelt`, `libg719_decode`, `libspeex` | see vgmstream's `COPYING` |

The LGPL requires that these libraries can be replaced. They are: they sit on disk as
ordinary `.dll` files in the folder above, and swapping one for your own build works
without touching XzoundWave. Source for the exact binaries is published by the vgmstream
project at the link above; nothing here is a modified build.

## NuGet packages — restored at build time

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
