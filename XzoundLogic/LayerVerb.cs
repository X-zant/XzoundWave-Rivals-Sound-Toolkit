using System.IO;
using CUE4Parse.UE4.Wwise.Objects.HIRC;
using CUE4Parse.UE4.Wwise.Objects.HIRC.Containers;

namespace MRAudioKit;

/// <summary>
/// XzoundWave --addlayer &lt;bank&gt; &lt;startEvent&gt; &lt;audio&gt; &lt;outDir&gt; [--stop &lt;event&gt;] [--name &lt;pak&gt;]:
/// one sound added under an event the game already has (EventLayer), the bank rebuilt and packed.
/// What the in-game test of a layer is built with, before the window has a way to ask for one.
/// </summary>
public static class LayerVerb
{
    public static int Run(string bankName, string startEvent, string audio, string outDir, string stopEvent, string pakName)
    {
        void W(string s) => Console.WriteLine(s);
        var settings = Settings.Load();
        settings.AutoDetect();
        var session = new GameSession();
        session.Mount(settings, _ => { });
        if (session.Provider is null) { W("the game did not mount; set it up first"); return 2; }

        // The bank by its name, in the language being played when it has one per language.
        var stem = Path.GetFileNameWithoutExtension(bankName);
        var candidates = session.BankPaths.Where(p => Path.GetFileNameWithoutExtension(p).Equals(stem, StringComparison.OrdinalIgnoreCase)).ToList();
        var bp = candidates.FirstOrDefault(p => GameSession.LanguageOf(p) is null)
                 ?? candidates.FirstOrDefault(p => string.Equals(GameSession.LanguageOf(p), session.Language, StringComparison.OrdinalIgnoreCase));
        if (bp is null) { W($"no bank {stem} in this game"); return 2; }

        var stock = session.Bank(bp).Read();
        var bank = HircBank.Parse(Path.GetFileName(bp), stock);
        if (!bank.Write().AsSpan().SequenceEqual(stock)) { W($"{bp} does not round-trip; refusing to edit it"); return 1; }

        var wem = PendingWem.ToWemBytes(audio, out var reason);
        if (wem is null) { W($"could not use {audio}: {reason}"); return 2; }
        W($"audio: {Path.GetFileName(audio)} -> {wem.Length:N0} B wem");

        // Hashed from the events and the file, so the same layer comes out with the same ids, and
        // moved on while the game already uses one: a media id is the game's as well as the bank's.
        var name = LayerBuild.NameFor(bank, $"{startEvent}/{Path.GetFileNameWithoutExtension(audio)}",
                                      id => session.MediaBank.ContainsKey(id) || session.LooseMedia(id) is not null);
        var ids = EventLayer.Ids.For(name);

        EventLayer.Added added;
        try
        {
            added = EventLayer.Add(bank, VoiceLine.Fnv(startEvent), wem, ids,
                                   stopEvents: stopEvent is null ? [] : [VoiceLine.Fnv(stopEvent)]);
        }
        catch (InvalidDataException ex) { W("could not add the layer: " + ex.Message); return 1; }
        foreach (var line in added.Log) W("  " + line);
        // Named in the bank's manifest, as a window build names it, so an update after a game
        // patch can take it out of the old bank and put it on the new one.
        // The stop event it was given, by name: an update keeps to that one rather than every event
        // that stops the Sound it copies.
        LayerManifest.Write(bank, LayerManifest.Read(bank),
                            [LayerManifest.Of(name, startEvent, VoiceLine.Fnv(startEvent), added, stopsWithTemplate: false)], bp, stock);

        // Read back as the game will: the event now ends in the layer's Play, and that plays it.
        var bytes = bank.Write();
        var check = HircBank.Parse("check", bytes);
        var ev = (HierarchyEvent)check.Nodes[VoiceLine.Fnv(startEvent)];
        var ok = ev.EventActionIds.Contains(ids.PlayId) && check.Nodes.ContainsKey(ids.SoundId) &&
                 check.Media.Any(m => m.Id == ids.MediaId) &&
                 (stopEvent is null || ((HierarchyEvent)check.Nodes[VoiceLine.Fnv(stopEvent)]).EventActionIds.Contains(ids.StopId));
        W($"  read back: {startEvent} has {ev.EventActionIds.Length} actions, the layer's last -> {(ok ? "ok" : "FAIL")}");
        if (!ok) return 1;

        Directory.CreateDirectory(outDir);
        var dest = Path.Combine(outDir, bp.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllBytes(dest, bytes);
        W($"  {bp}: {stock.Length:N0} B -> {bytes.Length:N0} B");

        var pak = pakName ?? $"XzoundLayer_{stem}{VerbPak.PatchSuffix}";
        return VerbPak.Pack(outDir, pak, [bp], W) ? 0 : 1;
    }
}
