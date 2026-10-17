using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UDataTable = CUE4Parse.UE4.Assets.Exports.Engine.UDataTable;
using CUE4Parse.UE4.Wwise.Enums;
using CUE4Parse.UE4.Wwise.Enums.Flags;
using CUE4Parse.UE4.Wwise.Objects.HIRC;
using CUE4Parse.UE4.Wwise.Objects.HIRC.Containers;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace MRAudioKit;

/// <summary>
/// Builds new voice lines into a staging folder: the voice set's VoiceItem and voice table
/// with the lines added, one new event asset per line, and the set's voice banks (every
/// language) with each line's event, Play action and in-memory Sound. Packaging the folder is
/// <see cref="ModPackager"/>'s job.
///
/// Everything a line needs that is not its own words and audio is copied from a TEMPLATE
/// line -- one that already sits where the new one goes (see <see cref="VoiceItemPath"/>):
/// its table row (category, season, hear flags as a starting point), its event asset (which
/// banks to load), and its Sound's place in the bank (mixer, bus, positioning). A line
/// therefore routes like its siblings: the first Hela test borrowed a hero-select Sound and
/// played silently through that screen's bus in a match.
///
/// Every value goes through a typed reader and writer, and every writer is made to reproduce
/// the game's own object before it is trusted with a new one.
/// </summary>
public static class VoiceLineBuilder
{
    public const string ManifestTag = "XZWV";

    /// <param name="Wem">the encoded audio (XzoundCore)</param>
    /// <param name="Seconds">its length: the event's duration, which is how long the subtitle shows</param>
    public sealed record Audio(byte[] Wem, double Seconds);

    public sealed record Result(List<string> Files, List<string> Log);

    /// <param name="bankSource">
    /// The bank as it stands for this build when an earlier step already wrote it (a build
    /// that also swaps audio in it, or another voice set of the same hero); null for the game's.
    /// </param>
    /// <param name="keep">
    /// Lines an earlier step of THIS build already added to these banks (another voice set of
    /// the same hero). Every other line a bank's manifest names came from an older build -- a
    /// loaded mod used as the base -- and is taken out before this build's lines go in, so a
    /// bank always carries exactly the project's lines.
    /// </param>
    public static Result Build(GameSession session, Settings settings, GameAssetExtractor extractor, VoiceSet set,
                               IReadOnlyList<VoiceLine> lines, Func<uint, Audio> audioFor, string stage,
                               Action<string> progress = null, Func<string, byte[]> bankSource = null,
                               IReadOnlySet<int> keep = null)
    {
        // The game's assets are extracted outside the stage, never into it: only what the
        // build changed or added goes into the mod, not untouched copies of the game's own.
        // A folder of this build's own, so two builds at once cannot take each other's files.
        var source = Path.Combine(Path.GetTempPath(), "XzoundWave", "voicelines-src-" + Guid.NewGuid().ToString("N"));
        try { return BuildCore(session, settings, extractor, set, lines, audioFor, stage, progress, bankSource, keep, source); }
        finally { try { if (Directory.Exists(source)) Directory.Delete(source, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static Result BuildCore(GameSession session, Settings settings, GameAssetExtractor extractor, VoiceSet set,
                                    IReadOnlyList<VoiceLine> lines, Func<uint, Audio> audioFor, string stage,
                                    Action<string> progress, Func<string, byte[]> bankSource, IReadOnlySet<int> keep,
                                    string source)
    {
        var log = new List<string>();
        void Say(string s) { log.Add(s); progress?.Invoke(s); }
        var files = new List<string>();
        var usmap = new Usmap(settings.UsmapPath);
        // An asset an earlier step of this build already edited (another voice set sharing
        // this VoiceItem or table) is built on, not fetched again from the game: the second
        // step would otherwise write over the first step's lines.
        UAsset Load(string vfs)
        {
            var staged = Path.Combine(stage, vfs.Replace('/', Path.DirectorySeparatorChar));
            return new(File.Exists(staged) ? staged : extractor.Extract(vfs, source, progress), EngineVersion.VER_UE5_3, usmap);
        }
        var heroId = int.Parse(set.HeroId);

        // ---- the voice table, and the ids every table in the game already uses -----------
        var tableVfs = VoiceSet.VfsPath(set.TablePackage);
        var table = Load(tableVfs);
        var dt = (DataTableExport)table.Exports.Single();
        var gameIds = AllVoiceIds(session);
        foreach (var l in lines)
        {
            if (l.VoiceId / 10000 != heroId)
                throw new InvalidDataException($"line {l.VoiceId}: a {set.HeroId} line's VoiceID must start with {set.HeroId} (the game drops it otherwise)");
            if (gameIds.Contains(l.VoiceId))
                throw new InvalidDataException($"line {l.VoiceId}: that VoiceID is already used by a game table; every hero's table shares one lookup in a match");
        }
        if (lines.Select(l => l.VoiceId).Distinct().Count() != lines.Count)
            throw new InvalidDataException("two lines share a VoiceID");

        // ---- the VoiceItem(s): add each line where its path points -------------------------
        foreach (var l in lines)
            if (VoiceTriggers.Of(l) is { } trig && VoiceTriggers.KeysDiffer(trig, l) is { } why)
                throw new InvalidDataException($"line {l.VoiceId}: {l.Target}: {why}");
        // Each line goes into its own form's VoiceItem only: the game reads one per form, and the
        // same path in another form's item is another entry, or none.
        foreach (var l in lines)
            if (!set.ItemPackages.Contains(l.ItemIn(set)))
                throw new InvalidDataException($"line {l.VoiceId}: {l.Item} is not one of {set.Label}'s voice items");
        var templateOf = new Dictionary<int, int>();
        // Never a template: this build's lines, the ones of this step and of the steps before it
        // (their rows are being made, some in another set's table).
        var ours = lines.Select(l => l.VoiceId).Concat(keep ?? Enumerable.Empty<int>()).ToHashSet();
        foreach (var itemPkg in set.ItemPackages.Where(i => lines.Any(l => l.ItemIn(set) == i)))
        {
            var item = Load(VoiceSet.VfsPath(itemPkg));
            foreach (var l in lines.Where(l => l.ItemIn(set) == itemPkg))
            {
                var r = VoiceItemPath.Apply(item, (NormalExport)item.Exports.Single(), l.Target, l.VoiceId, ours);
                if (r.TemplateVoiceId == 0) throw new InvalidDataException($"line {l.VoiceId}: {l.Target} has no existing line to copy routing from");
                templateOf.TryAdd(l.VoiceId, r.TemplateVoiceId);
                Say($"{Path.GetFileName(itemPkg)}: {r.Description} (like {r.TemplateVoiceId})");
            }
            files.Add(Save(item, stage, VoiceSet.VfsPath(itemPkg)));
        }

        // ---- rows and event assets ----------------------------------------------------------
        var rows = dt.Table.Data;
        var events = new Dictionary<int, (string Name, uint TemplateEventId, string TemplateEvent, string Package)>();
        foreach (var l in lines)
        {
            var tmplRow = rows.FirstOrDefault(r => VoiceIdOf(r) == templateOf[l.VoiceId])
                          ?? throw new InvalidDataException($"line {l.VoiceId}: template {templateOf[l.VoiceId]} has no row in {Path.GetFileName(tableVfs)}");
            var row = (StructPropertyData)tmplRow.Clone();
            row.Name = Name(table, l.VoiceId.ToString());
            Field<IntPropertyData>(row, "VoiceID").Value = l.VoiceId;
            var text = Field<TextPropertyData>(row, "Lines");
            text.HistoryType = TextHistoryType.Base;
            text.TableId = null;
            text.Namespace = new FString("XzoundWave");
            text.Value = new FString($"{Path.GetFileNameWithoutExtension(tableVfs)}_{l.VoiceId}_Lines");
            text.CultureInvariantString = new FString(l.Subtitle ?? "");
            Field<BoolPropertyData>(row, "bLocallyHear").Value = l.HearSelf;
            Field<BoolPropertyData>(row, "bFriendlyHear").Value = l.HearAllies;
            Field<BoolPropertyData>(row, "bEnemyHear").Value = l.HearEnemies;
            if (l.HearRelevant is { } relevant) Field<BoolPropertyData>(row, "bRelevantHear").Value = relevant;

            // The template's event: its package names the folder the new one goes in.
            var tmplRef = Field<ObjectPropertyData>(tmplRow, "Event").Value;
            var tmplImport = table.Imports[-tmplRef.Index - 1];
            var tmplPkg = table.Imports[-tmplImport.OuterIndex.Index - 1].ObjectName.ToString();
            var folder = tmplPkg[..(tmplPkg.LastIndexOf('/') + 1)];
            var package = folder + l.EventName;
            var pkgIdx = table.AddImport(new Import(table.Imports[-tmplImport.OuterIndex.Index - 1].ClassPackage,
                                                    table.Imports[-tmplImport.OuterIndex.Index - 1].ClassName,
                                                    new FPackageIndex(0), Name(table, package), false));
            var objIdx = table.AddImport(new Import(tmplImport.ClassPackage, tmplImport.ClassName, pkgIdx, Name(table, l.EventName), false));
            Field<ObjectPropertyData>(row, "Event").Value = objIdx;
            foreach (var deps in new[] { dt.SerializationBeforeSerializationDependencies, dt.CreateBeforeSerializationDependencies,
                                         dt.SerializationBeforeCreateDependencies, dt.CreateBeforeCreateDependencies })
                if (deps.Any(d => d.Index == tmplRef.Index)) deps.Add(objIdx);
            UnversionedZero.RefreshAll(row);
            rows.Add(row);

            // The event asset: the template's, renamed, with its own id, no streamed media
            // (the audio is in the bank) and the clip's duration.
            var ev = Load(VoiceSet.VfsPath(tmplPkg));
            var evx = ev.Exports.Single() as AkAudioEventExport
                      ?? throw new InvalidDataException($"{tmplPkg}: not read as an AkAudioEvent (check the usmap)");
            var tmplEventId = U32(Field<PropertyData>(evx.EventCookedData, "EventId"));
            var oldName = tmplImport.ObjectName.ToString();
            var names = ev.GetNameMapIndexList();
            for (var i = 0; i < names.Count; i++)
                if (names[i].Value.Contains(oldName)) ev.SetNameReference(i, new FString(names[i].Value.Replace(oldName, l.EventName)));
            ev.FolderName = new FString(package);      // the zen converter files a package under this
            SetU32(Field<PropertyData>(evx.EventCookedData, "EventId"), l.EventId);
            foreach (var lang in Languages(evx))
            {
                SetU32(Field<PropertyData>(lang, "EventId"), l.EventId);
                // Left flagged non-zero on purpose: the struct keeps the header it was read
                // with, which lists Media, so it is written as an explicit empty list -- the
                // shape the in-game test shipped.
                Field<ArrayPropertyData>(lang, "Media").Value = [];
            }
            var audio = audioFor(l.MediaId) ?? throw new InvalidDataException($"line {l.VoiceId}: no audio staged (media {l.MediaId})");
            evx.MaximumDuration = evx.MinimumDuration = (float)audio.Seconds;
            files.Add(Save(ev, stage, VoiceSet.VfsPath(package)));
            events[l.VoiceId] = (l.EventName, tmplEventId, oldName, package);
            Say($"row {l.VoiceId} \"{l.Subtitle}\" -> {package} (from {oldName})");
        }
        files.Add(Save(table, stage, tableVfs));

        // ---- the banks: every language the template events load ------------------------------
        var bankPaths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, e) in events)
        {
            var ev = new UAsset(Path.Combine(stage, VoiceSet.VfsPath(e.Package).Replace('/', Path.DirectorySeparatorChar)), EngineVersion.VER_UE5_3, usmap);
            foreach (var lang in Languages((AkAudioEventExport)ev.Exports.Single()))
                foreach (var b in Field<ArrayPropertyData>(lang, "SoundBanks").Value.Cast<StructPropertyData>())
                    bankPaths.Add("Marvel/Content/WwiseAudio/" + Text(b.Value.Single(p => p.Name.ToString() == "SoundBankPathName")));
        }
        foreach (var bp in bankPaths)
        {
            // The bank as it stands for this build: one an earlier step already wrote, else
            // the game's. The manifest records the GAME's bank as what was built on either way:
            // that is what an update after a game patch has to compare against.
            var game = (session.Bank(bp) ?? throw new FileNotFoundException(bp)).Read();
            var stock = bankSource?.Invoke(bp) ?? game;
            var bank = HircBank.Parse(Path.GetFileName(bp), stock);
            if (!bank.Write().AsSpan().SequenceEqual(stock)) throw new InvalidDataException($"{bp}: does not round-trip; refusing to edit it");

            // Lines a manifest already in the bank names: kept when this build added them a
            // step ago, taken out otherwise (they are an older build's, and this one rebuilds
            // the project's lines from scratch).
            var manifestLines = new List<object>();
            // A section by that tag that is not a lines manifest (an earlier experiment's, or not
            // JSON at all) is carried along as "previous", never parsed into a failed build.
            object previous = null;
            // The sounds added beside the bank's events (LayerManifest), carried through untouched:
            // the layer step that wrote them runs before this one and is the only one that edits them.
            object layers = null;
            if (bank.Sections.FirstOrDefault(s => s.Tag == ManifestTag) is { } old)
            {
                var text = Encoding.UTF8.GetString(old.Body).TrimEnd(' ', '\0');
                JsonDocument doc = null;
                try { doc = JsonDocument.Parse(text); } catch (JsonException) { }
                using (doc)
                {
                    // Every entry has to name every id, or none of it is acted on: taking out half a
                    // line, or the wrong objects, is worse than keeping a section nobody can read.
                    // A manifest of ours with no "lines" (only added sounds so far) has none to take out.
                    if (doc?.RootElement is { ValueKind: JsonValueKind.Object } root
                        && (root.TryGetProperty("lines", out var oldLines)
                            ? oldLines.ValueKind == JsonValueKind.Array && oldLines.EnumerateArray().All(ml => ManifestIds.Read(ml) is not null)
                            : root.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.String &&
                              f.GetString() == LayerManifest.Format))
                    {
                        if (oldLines.ValueKind == JsonValueKind.Array)
                            foreach (var ml in oldLines.EnumerateArray())
                            {
                                var ids = ManifestIds.Read(ml);
                                if (keep?.Contains(ids.VoiceId) == true) { manifestLines.Add(ml.Clone()); continue; }
                                RemoveLine(bank, ids);
                                Say($"{bp}: took out line {ids.VoiceId}, from an earlier build");
                            }
                        if (root.TryGetProperty("previous", out var carried)) previous = carried.Clone();
                        if (root.TryGetProperty("layers", out var added)) layers = added.Clone();
                    }
                    else
                    {
                        previous = doc?.RootElement.Clone() ?? (object)text;
                        Say($"{bp}: its {ManifestTag} section is not a voice-lines manifest; kept as \"previous\", nothing taken out");
                    }
                }
            }
            foreach (var l in lines)
            {
                var e = events[l.VoiceId];
                var added = AddLine(bank, l, e.TemplateEventId, audioFor(l.MediaId).Wem);
                manifestLines.Add(new
                {
                    voiceId = l.VoiceId, @event = e.Name, eventId = l.EventId, actionId = l.ActionId, soundId = l.SoundId,
                    mediaId = l.MediaId, mixer = added.Mixer, target = l.Target, subtitle = l.Subtitle,
                    // Per line: two voice sets of one hero share his voice bank.
                    voiceSet = new { table = set.TablePackage, items = set.ItemPackages },
                    like = new { @event = e.TemplateEvent, eventId = e.TemplateEventId, voiceId = templateOf[l.VoiceId] },
                });
            }
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                format = "xzoundwave.manifest", version = 1,
                builtOn = new { bank = Path.GetFileNameWithoutExtension(bp), path = bp, sha256 = Convert.ToHexString(SHA256.HashData(game)).ToLowerInvariant() },
                lines = manifestLines,
                layers,
                previous,
            }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            var body = new byte[(manifest.Length + 3) & ~3];
            Array.Fill(body, (byte)' ');
            manifest.CopyTo(body, 0);
            bank.Sections.RemoveAll(s => s.Tag == ManifestTag);
            bank.Sections.Add(new BnkBuilder.Section(ManifestTag, body));

            var bytes = bank.Write();
            var check = HircBank.Parse("check", bytes);
            foreach (var l in lines)
            {
                var ev = (HierarchyEvent)check.Nodes[l.EventId];
                var act = (HierarchyEventAction)check.Nodes[ev.EventActionIds.Single()];
                var snd = (HierarchySoundSfxVoice)check.Nodes[act.ReferencedId];
                if (snd.Source.SourceType != EAKBKSourceType.Data || snd.Source.SourceId != l.MediaId
                    || !check.MediaBytes(l.MediaId).AsSpan().SequenceEqual(audioFor(l.MediaId).Wem))
                    throw new InvalidDataException($"{bp}: line {l.VoiceId} does not read back as written");
            }
            var dest = Path.Combine(stage, bp.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, bytes);
            files.Add(dest);
            Say($"{bp}: +{lines.Count * 3} objects, +{lines.Count} media, {bytes.Length:N0} B");
        }
        return new Result(files, log);
    }

    private sealed record Added(uint Mixer);

    /// <summary>
    /// Take out a line an earlier build added, by the ids its manifest entry names: the event,
    /// its action, its Sound (and the Sound's place in its mixer) and its medium. Each object
    /// goes only when it is the kind the manifest says, so an id that means something else in
    /// this bank is left alone.
    /// </summary>
    /// <summary>The ids one manifest entry names; null when any is missing or not a number.</summary>
    private sealed record ManifestIds(int VoiceId, uint EventId, uint ActionId, uint SoundId, uint MediaId, uint Mixer)
    {
        public static ManifestIds Read(JsonElement ml)
        {
            if (ml.ValueKind != JsonValueKind.Object) return null;
            bool U(string k, out uint v) { v = 0; return ml.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetUInt32(out v); }
            return ml.TryGetProperty("voiceId", out var vid) && vid.ValueKind == JsonValueKind.Number && vid.TryGetInt32(out var voiceId)
                   && U("eventId", out var e) && U("actionId", out var a) && U("soundId", out var s) && U("mediaId", out var m) && U("mixer", out var x)
                ? new ManifestIds(voiceId, e, a, s, m, x) : null;
        }
    }

    /// <summary>
    /// Take out a line an earlier build added, by the ids its manifest entry names: the event,
    /// its action, its Sound (and the Sound's place in its mixer) and its medium. Each object
    /// goes only when it is the kind the manifest says, and the mixer entry and the medium only
    /// with a Sound that really plays that medium, so an id that means something else in this
    /// bank is left alone.
    /// </summary>
    private static void RemoveLine(HircBank bank, ManifestIds ids)
    {
        var sound = bank.Objects.FirstOrDefault(o => o.Id == ids.SoundId && o.Node is HierarchySoundSfxVoice s && s.Source.SourceId == ids.MediaId);
        if (sound is not null && bank.Objects.FirstOrDefault(o => o.Id == ids.Mixer && o.Node is HierarchyActorMixer) is { } mixerObj)
        {
            var span = HircLayout.Mixer(mixerObj.Body, bank.Version);
            if (span.Parsed.ChildIds.Contains(ids.SoundId))
            {
                mixerObj.Body = HircWriter.Mixer(mixerObj.Body, span, bank.Version, span.Parsed.ChildIds.Where(c => c != ids.SoundId).ToArray());
                mixerObj.Node = HircBank.ReadExact(mixerObj.Type, mixerObj.Body, bank.Version, "mixer");
            }
        }
        bool Ours(HircObject o) =>
            o.Id == ids.EventId && o.Node is HierarchyEvent
            || o.Id == ids.ActionId && o.Node is HierarchyEventAction act && act.ReferencedId == ids.SoundId
            || o == sound;
        foreach (var o in bank.Objects.Where(Ours).ToList())
        {
            bank.Objects.Remove(o);
            bank.Nodes.Remove(o.Id);
        }
        if (sound is not null && bank.Media.Any(m => m.Id == ids.MediaId)) bank.RemoveMedia(ids.MediaId);
    }

    // Event -> Play action -> in-memory Sound (a copy of the template's Sound, so it sits in
    // the same mixer and routes the same) -> the line's own medium.
    private static Added AddLine(HircBank bank, VoiceLine l, uint templateEventId, byte[] wem)
    {
        foreach (var id in new[] { l.EventId, l.ActionId, l.SoundId })
            if (bank.Nodes.ContainsKey(id)) throw new InvalidDataException($"{bank.Name}: id {id} is taken");
        if (bank.Media.Any(m => m.Id == l.MediaId)) throw new InvalidDataException($"{bank.Name}: media {l.MediaId} is taken");

        var tEvObj = bank.Objects.Single(o => o.Id == templateEventId && o.Type == EAKBKHircType.Event);
        var tEv = (HierarchyEvent)tEvObj.Node;
        if (HircWriter.Event(tEv.Id, tEv.EventActionIds, bank.Version) is var eb && !eb.AsSpan().SequenceEqual(tEvObj.Body))
            throw new InvalidDataException($"{bank.Name}: event writer does not reproduce {tEv.Id}");
        var tActObj = bank.Objects.First(o => tEv.EventActionIds.Contains(o.Id) && o.Node is HierarchyEventAction { EventActionType: EAkActionType.Play });
        var aSpan = HircLayout.Action(tActObj.Body, bank.Version);
        if (!HircWriter.Action(tActObj.Body, aSpan, bank.Version, aSpan.Parsed.Id, aSpan.Parsed.ReferencedId).AsSpan().SequenceEqual(tActObj.Body))
            throw new InvalidDataException($"{bank.Name}: action writer does not reproduce {aSpan.Parsed.Id}");
        var tSndObj = bank.Objects.Single(o => o.Id == aSpan.Parsed.ReferencedId);
        if (tSndObj.Node is not HierarchySoundSfxVoice)
            throw new InvalidDataException($"{bank.Name}: the template line plays a {tSndObj.Type}, not a Sound; not supported yet");
        var sSpan = HircLayout.Sound(tSndObj.Body, bank.Version);
        if (!HircWriter.SoundAsIs(tSndObj.Body, sSpan, bank.Version).AsSpan().SequenceEqual(tSndObj.Body))
            throw new InvalidDataException($"{bank.Name}: sound writer does not reproduce {sSpan.Parsed.Id}");
        var mixerId = sSpan.Parsed.BaseParams.DirectParentId;
        var mixerObj = bank.Objects.Single(o => o.Id == mixerId);
        if (mixerObj.Node is not HierarchyActorMixer)
            throw new InvalidDataException($"{bank.Name}: the template Sound's parent is a {mixerObj.Type}, not a mixer; not supported yet");
        var mSpan = HircLayout.Mixer(mixerObj.Body, bank.Version);
        if (!HircWriter.Mixer(mixerObj.Body, mSpan, bank.Version, mSpan.Parsed.ChildIds).AsSpan().SequenceEqual(mixerObj.Body))
            throw new InvalidDataException($"{bank.Name}: mixer writer does not reproduce {mixerId}");

        // The flags an in-memory voice Sound carries, from one the game ships in this bank;
        // failing that, the template's own minus Prefetch.
        var inMemory = bank.Nodes.Values.OfType<HierarchySoundSfxVoice>().FirstOrDefault(s => s.Source.SourceType == EAKBKSourceType.Data);
        var flags = inMemory is not null ? (byte)inMemory.Source.BankSourceFlags
                                         : (byte)(sSpan.Parsed.Source.BankSourceFlags & ~EBankSourceFlags.Prefetch);

        var sound = new HircObject { RawType = tSndObj.RawType, Type = tSndObj.Type,
            Body = HircWriter.Sound(tSndObj.Body, sSpan, bank.Version, l.SoundId, EAKBKSourceType.Data, l.MediaId, (uint)wem.Length, flags) };
        sound.Node = HircBank.ReadExact(sound.Type, sound.Body, bank.Version, "new sound");
        bank.AddMedia(l.MediaId, wem);
        var kids = mSpan.Parsed.ChildIds.Append(l.SoundId).OrderBy(x => x).ToArray();
        mixerObj.Body = HircWriter.Mixer(mixerObj.Body, mSpan, bank.Version, kids);
        mixerObj.Node = HircBank.ReadExact(mixerObj.Type, mixerObj.Body, bank.Version, "mixer");
        var next = kids.Where(k => k > l.SoundId).Cast<uint?>().FirstOrDefault();
        bank.Objects.Insert(next is { } n ? bank.SubtreeStart(n) : bank.Objects.IndexOf(mixerObj), sound);
        bank.Nodes[l.SoundId] = sound.Node;

        var action = new HircObject { RawType = tActObj.RawType, Type = tActObj.Type,
            Body = HircWriter.Action(tActObj.Body, aSpan, bank.Version, l.ActionId, l.SoundId) };
        action.Node = HircBank.ReadExact(action.Type, action.Body, bank.Version, "new action");
        bank.Objects.Insert(bank.Objects.IndexOf(tActObj) + 1, action);
        bank.Nodes[l.ActionId] = action.Node;

        var ev = new HircObject { RawType = tEvObj.RawType, Type = tEvObj.Type, Body = HircWriter.Event(l.EventId, [l.ActionId], bank.Version) };
        ev.Node = HircBank.ReadExact(ev.Type, ev.Body, bank.Version, "new event");
        bank.Objects.Insert(bank.Objects.IndexOf(tEvObj) + 1, ev);
        bank.Nodes[l.EventId] = ev.Node;
        return new Added(mixerId);
    }

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>Every VoiceID in every voice table the game ships: one lookup in a match.</summary>
    public static HashSet<int> AllVoiceIds(GameSession session)
    {
        var ids = new HashSet<int>();
        foreach (var path in session.Provider.Files.Keys
                     .Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && k.Contains("HeroVoice", StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var pkg = path[..^".uasset".Length];
                var dt = session.Provider.LoadPackageObject<UDataTable>(pkg + "." + Path.GetFileName(pkg));
                foreach (var (_, row) in dt.RowMap) ids.Add(row.GetOrDefault<int>("VoiceID"));
            }
            catch { /* not a voice table */ }
        }
        return ids;
    }

    /// <summary>
    /// The highest free VoiceID in a voice set's own block: the hero's prefix and the set's
    /// sculpt digit (the thousands digit its rows use), counting down from xxxx999, skipping
    /// every id the game or the project already uses.
    /// </summary>
    public static int AllocateVoiceId(GameSession session, VoiceSet set, IEnumerable<int> alsoTaken, HashSet<int> gameIds = null)
    {
        gameIds ??= AllVoiceIds(session);
        var dt = session.Provider.LoadPackageObject<UDataTable>(set.TablePackage[1..].Replace("Game/", "Marvel/Content/") + "." + Path.GetFileName(set.TablePackage));
        var digit = dt.RowMap.Select(r => r.Value.GetOrDefault<int>("VoiceID") / 1000 % 10)
                      .GroupBy(d => d).OrderByDescending(g => g.Count()).First().Key;
        var taken = new HashSet<int>(alsoTaken);
        var hero = int.Parse(set.HeroId);
        for (var n = 999; n >= 0; n--)
        {
            var id = hero * 10000 + digit * 1000 + n;
            if (!gameIds.Contains(id) && !taken.Contains(id)) return id;
        }
        throw new InvalidOperationException($"{set.Label}: no free VoiceID left in its block");
    }

    private static IEnumerable<StructPropertyData> Languages(AkAudioEventExport e) =>
        Field<MapPropertyData>(e.EventCookedData, "EventLanguageMap").Value.Values.Cast<StructPropertyData>();

    private static string Text(PropertyData p) => p switch
    {
        StrPropertyData s => s.Value?.Value,
        NamePropertyData n => n.Value?.ToString(),
        _ => throw new InvalidDataException($"{p.Name}: not text ({p.GetType().Name})"),
    };

    private static int VoiceIdOf(StructPropertyData row) => Field<IntPropertyData>(row, "VoiceID").Value;

    private static T Field<T>(StructPropertyData s, string name) where T : PropertyData =>
        s.Value.OfType<T>().FirstOrDefault(p => p.Name.ToString() == name)
        ?? throw new InvalidDataException($"{s.Name}: no {typeof(T).Name} {name}");

    private static uint U32(PropertyData p) => p switch
    {
        UInt32PropertyData u => u.Value,
        IntPropertyData i => unchecked((uint)i.Value),
        _ => throw new InvalidDataException($"EventId is a {p.GetType().Name}"),
    };

    private static void SetU32(PropertyData p, uint v)
    {
        switch (p)
        {
            case UInt32PropertyData u: u.Value = v; break;
            case IntPropertyData i: i.Value = unchecked((int)v); break;
            default: throw new InvalidDataException($"EventId is a {p.GetType().Name}");
        }
    }

    private static FName Name(UAsset a, string s) { a.AddNameReference(new FString(s)); return FName.FromString(a, s); }

    // Written, then read back: every export must parse again (UAssetAPI turns one it cannot
    // read into a RawExport) and keep the number of properties it had, or the build stops here
    // rather than shipping an asset the game would misread from the first broken field on.
    private static string Save(UAsset a, string stage, string vfs)
    {
        var p = Path.Combine(stage, vfs.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        a.Write(p);
        var back = new UAsset(p, EngineVersion.VER_UE5_3, a.Mappings);
        for (var i = 0; i < a.Exports.Count; i++)
        {
            var (was, now) = (a.Exports[i], back.Exports[i]);
            if (now is RawExport || now.GetType() != was.GetType())
                throw new InvalidDataException($"{vfs}: export {i} does not read back ({now.GetType().Name})");
            if (was is NormalExport wn && now is NormalExport nn && wn.Data.Count != nn.Data.Count)
                throw new InvalidDataException($"{vfs}: export {i} reads back {nn.Data.Count} properties of {wn.Data.Count}");
            if (was is DataTableExport wd && now is DataTableExport nd && wd.Table.Data.Count != nd.Table.Data.Count)
                throw new InvalidDataException($"{vfs}: the table reads back {nd.Table.Data.Count} rows of {wd.Table.Data.Count}");
            if (was is AkAudioEventExport we && now is AkAudioEventExport ne && (ne.EventCookedData is null) != (we.EventCookedData is null))
                throw new InvalidDataException($"{vfs}: the event's cooked data does not read back");
        }
        return p;
    }
}
