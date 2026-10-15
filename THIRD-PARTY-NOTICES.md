# Third-party notices

XzoundWave is MIT licensed (see `LICENSE`). The components below are not, and their
terms travel with any copy you distribute.

XzoundWave ships as single files: `XzoundWave.exe` on Windows; on Linux the window,
`XzoundWave`, and the same engine without it, `xzoundwave`. Everything down to
**Supplied by you** is **inside those files**: the aoTuV codebooks are compiled into
`XzoundCore.dll`, vgmstream is an embedded zip written out to your app data the first
time it is needed, the spoken word clips are embedded resources, the Linux window's fonts
are embedded resources, and the NuGet packages are folded in with the rest by the
single-file publish. The Linux builds are self-contained and carry the .NET runtime as
well; the Windows one asks the machine for it. What is listed under **Supplied by you**
is never bundled at all.

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

## The .NET runtime, in the Linux builds only

The Linux release is self-contained: the runtime and its class libraries are published
into `xzoundwave` and `XzoundWave` rather than installed on the machine, which is what
makes "nothing to install" true there. It is
[.NET](https://github.com/dotnet/runtime), **MIT** licensed, Copyright (c) .NET
Foundation and Contributors.

The Windows release carries none of it and asks the machine for the .NET Desktop Runtime
instead, so this section does not apply to `XzoundWave.exe`.

## Fonts, used by the Linux window

Windows always has Segoe UI and a Chinese font, and the Windows window uses those. A
Linux machine has whatever its distribution installed, so the Linux window brings two,
both licensed under the **SIL Open Font License 1.1**, whose full text is at the end of
this file:

- **[Inter](https://github.com/rsms/inter)** 3.019, the interface font, by way of the
  Avalonia.Fonts.Inter package. Copyright 2020 The Inter Project Authors. "Inter" is a
  trademark of rsms. The package is inside `XzoundWave.exe` too, unused, because the
  headless renderer the window is tested with depends on it, so this notice covers every
  window build.
- **[Noto Sans SC](https://github.com/notofonts/noto-cjk)** 2.004, for Chinese and the
  Japanese kana. © 2014-2021 Adobe (http://www.adobe.com/). In the Linux window only.
  Shipped unmodified as `vendor/fonts/NotoSansSC-Regular.otf`, with its licence beside it.

Neither is modified, and neither is sold on its own: they travel inside the program,
which is what the licence permits. The command-line `xzoundwave` carries neither.

## The window's drawing libraries

The window draws with [Skia](https://github.com/google/skia) and shapes text with
[HarfBuzz](https://github.com/harfbuzz/harfbuzz), native libraries that arrive through the
SkiaSharp and HarfBuzzSharp packages and are folded into the window's single file on both
platforms. On Windows it also carries [ANGLE](https://github.com/google/angle), which
Avalonia renders OpenGL through. Skia and ANGLE are **BSD-3-Clause** (Copyright (c) 2011
Google Inc.; Copyright 2018 The ANGLE Project Authors), HarfBuzz is the **"Old MIT"**
licence, and libSkiaSharp has more linked into it (libpng, zlib, FreeType, expat, ICU,
libjpeg-turbo, libwebp and others), each under its own terms.

Their notices run to some 140 KB, so they are not repeated here. SkiaSharp publishes them
as one file, which is compiled into the window together with SkiaSharp's MIT licence and,
on Windows, ANGLE's licence: `XzoundWave --licences` prints all three after this file,
and they are written out beside the unpacked vgmstream as `XzoundWave-*.txt`. In the
source they are `vendor/notices/`. The command-line `xzoundwave` carries none of these
libraries and none of their notices.

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
| [Avalonia](https://github.com/AvaloniaUI/Avalonia), with its DataGrid, Fluent theme, Skia and headless packages — the window | MIT — Copyright (c) AvaloniaUI OÜ |
| [SkiaSharp and HarfBuzzSharp](https://github.com/mono/SkiaSharp), which Avalonia draws with — the window | MIT — Copyright (c) 2015-2016 Xamarin, Inc., Copyright (c) 2017-2018 Microsoft Corporation; the native libraries inside them, see **The window's drawing libraries** |
| [Avalonia.Fonts.Inter](https://github.com/AvaloniaUI/Avalonia) — the window, used on Linux | MIT; the Inter font inside it is OFL 1.1, see **Fonts** |

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

---

## SIL Open Font License 1.1

The terms Inter and Noto Sans SC are distributed under, as published with Noto Sans SC.

```
This Font Software is licensed under the SIL Open Font License,
Version 1.1.

This license is copied below, and is also available with a FAQ at:
http://scripts.sil.org/OFL

-----------------------------------------------------------
SIL OPEN FONT LICENSE Version 1.1 - 26 February 2007
-----------------------------------------------------------

PREAMBLE
The goals of the Open Font License (OFL) are to stimulate worldwide
development of collaborative font projects, to support the font
creation efforts of academic and linguistic communities, and to
provide a free and open framework in which fonts may be shared and
improved in partnership with others.

The OFL allows the licensed fonts to be used, studied, modified and
redistributed freely as long as they are not sold by themselves. The
fonts, including any derivative works, can be bundled, embedded,
redistributed and/or sold with any software provided that any reserved
names are not used by derivative works. The fonts and derivatives,
however, cannot be released under any other type of license. The
requirement for fonts to remain under this license does not apply to
any document created using the fonts or their derivatives.

DEFINITIONS
"Font Software" refers to the set of files released by the Copyright
Holder(s) under this license and clearly marked as such. This may
include source files, build scripts and documentation.

"Reserved Font Name" refers to any names specified as such after the
copyright statement(s).

"Original Version" refers to the collection of Font Software
components as distributed by the Copyright Holder(s).

"Modified Version" refers to any derivative made by adding to,
deleting, or substituting -- in part or in whole -- any of the
components of the Original Version, by changing formats or by porting
the Font Software to a new environment.

"Author" refers to any designer, engineer, programmer, technical
writer or other person who contributed to the Font Software.

PERMISSION & CONDITIONS
Permission is hereby granted, free of charge, to any person obtaining
a copy of the Font Software, to use, study, copy, merge, embed,
modify, redistribute, and sell modified and unmodified copies of the
Font Software, subject to the following conditions:

1) Neither the Font Software nor any of its individual components, in
Original or Modified Versions, may be sold by itself.

2) Original or Modified Versions of the Font Software may be bundled,
redistributed and/or sold with any software, provided that each copy
contains the above copyright notice and this license. These can be
included either as stand-alone text files, human-readable headers or
in the appropriate machine-readable metadata fields within text or
binary files as long as those fields can be easily viewed by the user.

3) No Modified Version of the Font Software may use the Reserved Font
Name(s) unless explicit written permission is granted by the
corresponding Copyright Holder. This restriction only applies to the
primary font name as presented to the users.

4) The name(s) of the Copyright Holder(s) or the Author(s) of the Font
Software shall not be used to promote, endorse or advertise any
Modified Version, except to acknowledge the contribution(s) of the
Copyright Holder(s) and the Author(s) or with their explicit written
permission.

5) The Font Software, modified or unmodified, in part or in whole,
must be distributed entirely under this license, and must not be
distributed under any other license. The requirement for fonts to
remain under this license does not apply to any document created using
the Font Software.

TERMINATION
This license becomes null and void if any of the above conditions are
not met.

DISCLAIMER
THE FONT SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO ANY WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT
OF COPYRIGHT, PATENT, TRADEMARK, OR OTHER RIGHT. IN NO EVENT SHALL THE
COPYRIGHT HOLDER BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
INCLUDING ANY GENERAL, SPECIAL, INDIRECT, INCIDENTAL, OR CONSEQUENTIAL
DAMAGES, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF THE USE OR INABILITY TO USE THE FONT SOFTWARE OR FROM
OTHER DEALINGS IN THE FONT SOFTWARE.
```
