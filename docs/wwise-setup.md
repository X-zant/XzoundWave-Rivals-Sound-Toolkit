# Installing Wwise for Vorbis conversion (optional)

**Most people never need this.** Read the first section before installing several GB.

## Do you actually need it?

| What you're doing | Needs Wwise |
| --- | --- |
| Browse, preview, notes, CSV export | no |
| Build a mod from `.wem` files you already have | no |
| Numbered test bank from a supplied clip set | no |
| Generate spoken numbers as PCM (goes inside a bank) | no |
| Convert your own wav/mp3/ogg to a **Vorbis** `.wem` in-app | **yes** |
| Loose `WwiseAudio/Media/##/` SFX mods | **yes** — Vorbis is mandatory there |

Only the last two rows. Everything else works with an empty Setup panel.

Note that soundKit has the same requirement for its Vorbis path and also asks you to
supply the path — nobody bundles WwiseConsole, because Audiokinetic's licence does not
allow redistributing it.

## Which version

**Wwise 2022.1.**

Marvel Rivals' soundbanks report bank version **145**, and 145 is Wwise 2022.1. Picking
a much newer version risks writing media in a layout the shipped engine does not expect.

You can confirm the game's version yourself — every bank header carries it, and the
tool prints it when reading one (`wwiseVer=145`).

## Installing the smallest useful footprint

1. **Get the Audiokinetic Launcher** — small download, and it is the only way to get
   Wwise. A free account is required. Wwise is free for non-commercial use; read the
   licence terms yourself if you are unsure whether that covers you.

2. **Launcher → Wwise tab → Install**, and choose **2022.1**.

3. On the install options screen, deselect aggressively. The defaults install far more
   than this needs:

   **Packages**
   - ✅ **Authoring** — required. `WwiseConsole.exe` lives here and nothing else does.
   - ❌ SDK — C++ libraries for integrating Wwise into a game engine. Irrelevant.
   - ❌ Sample Project — a demo project.
   - ❌ Documentation — available online.

   **Deployment Platforms**
   - ✅ **Windows** — required. `convert-external-source --platform Windows` will fail
     without it.
   - ❌ everything else — Android, iOS, PS5, Xbox, Switch, Mac, Linux. Each one is a
     large chunk and none is used.

   **Plug-ins**
   - ❌ all third-party plug-ins (iZotope, Auro, McDSP, Crankcase, Impacter, …).
   - Vorbis is a built-in Audiokinetic codec that ships with Authoring. There is no
     plug-in to tick for it.

4. **Note the install path.** `WwiseConsole.exe` ends up at roughly:

   ```
   <install>\Authoring\x64\Release\bin\WwiseConsole.exe
   ```

5. In the app's **Setup** panel, paste that path into **WwiseConsole.exe**. Leave the
   **Wwise .wproj** field empty and the app creates a throwaway project itself; point it
   at an existing `.wproj` if you would rather reuse one.

You never open the Wwise GUI, and you never touch the project again.

## What the app does with it

```
WwiseConsole.exe create-new-project <path>\encoder.wproj          once
WwiseConsole.exe convert-external-source <proj> --platform Windows --source-file <list>.xml
```

Before every batch it rewrites the project's `Conversion Settings\Default Work Unit.wwu`
to force `Format = Vorbis`. That is deliberate: Wwise regenerates that file as **PCM**
whenever it feels like it, and the usual symptom is "the encoder ignored me". The output
is then checked, and you get an error naming the codec if Wwise fell back to PCM anyway.

## Reclaiming the space afterwards

If you only needed a one-off batch, uninstall through the Launcher when you are done —
the clips you generated keep working. The Launcher itself is small enough to leave
installed.

## Unverified

The install steps above are written from Audiokinetic's documented Launcher options and
the CLI interface, **not from a run on a machine that has Wwise**. The version mapping
(145 → 2022.1) is confirmed; the exact package names and on-disk sizes may differ in
your Launcher build. Corrections welcome — this page should be fixed by whoever installs
it first rather than left as an estimate.
