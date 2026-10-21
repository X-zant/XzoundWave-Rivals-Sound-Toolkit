using System.Buffers.Binary;
using CUE4Parse.UE4.Wwise;
using CUE4Parse.UE4.Wwise.Enums;
using CUE4Parse.UE4.Wwise.Objects;
using CUE4Parse.UE4.Wwise.Objects.Actions;
using CUE4Parse.UE4.Wwise.Objects.HIRC;
using CUE4Parse.UE4.Wwise.Objects.HIRC.Containers;

namespace MRAudioKit;

/// <summary>
/// Where each field of a HIRC object sits, found by stepping CUE4Parse's own readers over
/// the object's body in the order those readers read it, then checked against the value the
/// full reader produced. Nothing here is an offset typed in by hand.
/// </summary>
public static class HircLayout
{
    static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
    static void Check(bool ok, string what) { if (!ok) throw new InvalidDataException("HIRC layout check failed: " + what); }

    /// <param name="ParentAt">DirectParentId</param>
    /// <param name="End">one past the last byte, where the full reader stopped</param>
    public sealed record BaseSpan(int Start, int BusAt, int ParentAt, int End, BaseHierarchy Parsed);

    /// <summary>
    /// CAkParameterNodeBase: OverrideFx, AkFxParams, then (v &gt; 136) OverrideParentMetadataFlag
    /// and the FxChunk array, then (89 &lt; v &lt;= 145) OverrideAttachmentParams, then
    /// OverrideBusId and DirectParentId. The same calls are made here, in that order, to find
    /// the two ids; the whole block is then read again by the reader itself to find its end.
    /// </summary>
    public static BaseSpan Base(byte[] body, int start, uint version)
    {
        var ar = HircBank.Archive(body, version);
        ar.Position = start;
        ar.ReadBool();
        _ = new AkFxParams(ar);
        if (ar.Version > 136)
        {
            ar.ReadBool();
            _ = ar.ReadArray(ar.Read<byte>(), () => new AkFxChunk(ar));
        }
        if (ar.Version > 89 && ar.Version <= 145) ar.ReadBool();
        var busAt = (int)ar.Position; ar.Read<uint>();
        var parentAt = (int)ar.Position; ar.Read<uint>();

        ar.Position = start;
        var parsed = new BaseHierarchy(ar);
        var end = (int)ar.Position;

        Check(U32(body, busAt) == parsed.OverrideBusId, "OverrideBusId");
        Check(U32(body, parentAt) == parsed.DirectParentId, "DirectParentId");
        return new BaseSpan(start, busAt, parentAt, end, parsed);
    }

    /// <summary>Where a BaseHierarchy's AkPropBundle (its properties, then its ranged modifiers) sits.</summary>
    public sealed record PropsSpan(int At, int End, AkPropBundle Parsed);

    /// <summary>
    /// The BaseHierarchy's property bundle, found where reading it back gives exactly the bundle the
    /// object's own reader parsed AND writing it gives exactly the bytes there. Null for an object
    /// with no properties, which has nothing in its bundle to write. The bytes between DirectParentId
    /// and the bundle (the priority flags) are not named here, only stepped over: the search starts
    /// at DirectParentId's end and goes no further than a few bytes.
    /// </summary>
    private static bool SameBundle(AkPropBundle a, AkPropBundle b) =>
        (a.Props ?? []).Length == (b.Props ?? []).Length &&
        (a.Props ?? []).Zip(b.Props ?? []).All(p => p.First.Id == p.Second.Id && p.First.Value.u32 == p.Second.Value.u32) &&
        (a.PropRanges ?? []).Length == (b.PropRanges ?? []).Length &&
        (a.PropRanges ?? []).Zip(b.PropRanges ?? []).All(p => p.First.Id == p.Second.Id &&
            p.First.Min.u32 == p.Second.Min.u32 && p.First.Max.u32 == p.Second.Max.u32);

    /// <summary>Where a BaseHierarchy's AkAdvSettingsParams (playback limit, virtual voice, HDR) sits: six bytes.</summary>
    public sealed record AdvSpan(int At, AkAdvSettingsParams Parsed);

    /// <summary>
    /// The playback-limit block, found by reading the BaseHierarchy's pieces in their order from where
    /// its property bundle starts: the bundle, positioning, aux sends, then this. Accepted only where the
    /// bundle and the block read back as the object's own reader parsed them and the block is the six
    /// bytes <see cref="HircWriter.AdvSettings"/> writes back exactly; a block that reads two ways is refused.
    /// </summary>
    public static AdvSpan Adv(byte[] body, BaseSpan span, uint version)
    {
        var want = span.Parsed.AdvSettingsParams;
        AdvSpan found = null;
        for (var at = span.ParentAt + 4; at <= span.ParentAt + 8 && at < span.End; at++)
        {
            int advAt;
            AkAdvSettingsParams adv;
            try
            {
                var ar = HircBank.Archive(body, version);
                ar.Position = at;
                if (!SameBundle(new AkPropBundle(ar), span.Parsed.PropBundle)) continue;
                _ = new AkPositioningParams(ar);
                _ = new AkAuxParams(ar);
                advAt = (int)ar.Position;
                adv = new AkAdvSettingsParams(ar);
                if ((int)ar.Position - advAt != 6 || advAt + 6 > span.End) continue;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { continue; }
            if (adv.AdvSettingsFlags != want.AdvSettingsFlags || adv.MaxNumInstance != want.MaxNumInstance ||
                adv.VirtualQueueBehavior != want.VirtualQueueBehavior || adv.BelowThresholdBehavior != want.BelowThresholdBehavior ||
                adv.HdrEnvelopeFlags != want.HdrEnvelopeFlags ||
                !HircWriter.AdvSettings(adv, Convert.ToByte(adv.AdvSettingsFlags), adv.MaxNumInstance).AsSpan().SequenceEqual(body.AsSpan(advAt, 6)))
                continue;
            Check(found is null || found.At == advAt, "the playback-limit block reads one way");
            found = new AdvSpan(advAt, adv);
        }
        Check(found is not null, "the playback-limit block is where its reader puts it");
        return found;
    }

    public static PropsSpan Props(byte[] body, BaseSpan span, uint version)
    {
        var want = span.Parsed.PropBundle;
        if (want.Props is not { Length: > 0 }) return null;
        static bool Same(AkPropBundle a, AkPropBundle b) => SameBundle(a, b);
        for (var at = span.ParentAt + 4; at <= span.ParentAt + 8 && at < span.End; at++)
        {
            AkPropBundle got;
            int end;
            try
            {
                var ar = HircBank.Archive(body, version);
                ar.Position = at;
                got = new AkPropBundle(ar);
                end = (int)ar.Position;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { continue; }
            if (end <= span.End && Same(got, want) &&
                HircWriter.PropBundle(got.Props, got.PropRanges).AsSpan().SequenceEqual(body.AsSpan(at, end - at)))
                return new PropsSpan(at, end, got);
        }
        throw new InvalidDataException("HIRC layout check failed: the property bundle is not where its reader puts it");
    }

    public sealed record SoundSpan(int SourceStart, int SourceTypeAt, int SourceIdAt, int MemSizeAt,
                                   int FlagsAt, int PluginParamsAt, int SourceEnd, BaseSpan Base,
                                   HierarchySoundSfxVoice Parsed);

    /// <summary>
    /// CAkSound: id, then AkBankSourceData (for 112 &lt; v &lt;= 150: plugin id, source type,
    /// source id, in-memory media size, the source flags byte and, for a Source-type plugin
    /// only, plugin parameters), then the BaseHierarchy.
    /// </summary>
    public static SoundSpan Sound(byte[] body, uint version)
    {
        Check(version > 112 && version <= 150, $"AkBankSourceData branch for version {version}");
        var parsed = (HierarchySoundSfxVoice)HircBank.ReadExact(EAKBKHircType.SoundSfxVoice, body, version, "sound");
        var ar = HircBank.Archive(body, version);
        ar.Read<uint>();
        var srcStart = (int)ar.Position;
        _ = WwisePlugin.GetPluginId(ar);
        var typeAt = (int)ar.Position; ar.Read<EAKBKSourceType>();
        var sourceIdAt = (int)ar.Position; ar.Read<uint>();
        var memAt = (int)ar.Position; ar.Read<uint>();
        var flagsAt = (int)ar.Position; ar.Read<byte>();
        var paramsAt = (int)ar.Position;

        ar.Position = srcStart;
        var src = new AkBankSourceData(ar);
        var srcEnd = (int)ar.Position;

        Check(body[typeAt] == (byte)src.SourceType, "SourceType");
        Check(U32(body, sourceIdAt) == src.SourceId && src.SourceId == parsed.Source.SourceId, "SourceId");
        Check(U32(body, memAt) == src.InMemoryMediaSize, "InMemoryMediaSize");
        Check(body[flagsAt] == (byte)src.BankSourceFlags, "BankSourceFlags");
        Check(src.HasPluginParams || srcEnd == paramsAt, "no plugin params means the source ends at the flags");

        var b = Base(body, srcEnd, version);
        Check(b.End == body.Length, "BaseHierarchy ends the sound");
        return new SoundSpan(srcStart, typeAt, sourceIdAt, memAt, flagsAt, paramsAt, srcEnd, b, parsed);
    }

    public sealed record MixerSpan(BaseSpan Base, int ChildrenAt, HierarchyActorMixer Parsed);

    /// <summary>
    /// A random or sequence container: its BaseHierarchy, its own fields (loop count, transition,
    /// avoid-repeat, modes, playlist flags) up to <see cref="ChildrenAt"/>, its AkChildren, then
    /// its playlist from <see cref="PlaylistAt"/> to the end.
    /// </summary>
    public sealed record ContainerSpan(BaseSpan Base, int ChildrenAt, int PlaylistAt, HierarchyRandomSequenceContainer Parsed);

    /// <summary>
    /// The children list is found where its count and every id read back as the reader's, after
    /// the container's own fields; the playlist after it must hold the reader's items and end the
    /// body exactly, or the layout is not the one this writes.
    /// </summary>
    public static ContainerSpan RandomContainer(byte[] body, uint version)
    {
        var parsed = (HierarchyRandomSequenceContainer)HircBank.ReadExact(EAKBKHircType.RandomSequenceContainer, body, version, "container");
        var b = Base(body, 4, version);
        var kids = parsed.ChildIds ?? [];
        Check(kids.Length > 0, "a container with children");
        var childrenAt = -1;
        for (var at = b.End; at + 4 + kids.Length * 4 <= body.Length && childrenAt < 0; at++)
            if (U32(body, at) == kids.Length && kids.Select((k, i) => U32(body, at + 4 + i * 4) == k).All(x => x))
                childrenAt = at;
        Check(childrenAt >= 0, "AkChildren in the container");
        var playlistAt = childrenAt + 4 + kids.Length * 4;
        var items = parsed.Playlist ?? [];
        Check(playlistAt + 2 <= body.Length && BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(playlistAt)) == items.Length &&
              playlistAt + 2 + items.Length * 8 == body.Length, "the playlist ends the container");
        for (var i = 0; i < items.Length; i++)
            Check(U32(body, playlistAt + 2 + i * 8) == items[i].PlayId &&
                  BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(playlistAt + 6 + i * 8)) == items[i].Weight, "playlist item");
        return new ContainerSpan(b, childrenAt, playlistAt, parsed);
    }

    /// <summary>CAkActorMixer: id, BaseHierarchy, AkChildren.</summary>
    public static MixerSpan Mixer(byte[] body, uint version)
    {
        var parsed = (HierarchyActorMixer)HircBank.ReadExact(EAKBKHircType.ActorMixer, body, version, "mixer");
        var b = Base(body, 4, version);
        var ar = HircBank.Archive(body, version);
        ar.Position = b.End;
        var kids = new AkChildren(ar).ChildIds;
        Check(ar.Position == body.Length && kids.SequenceEqual(parsed.ChildIds), "AkChildren ends the mixer");
        return new MixerSpan(b, b.End, parsed);
    }

    public sealed record ActionSpan(int ScopeAt, int TypeAt, int ReferencedAt, int IsBusAt, int PropsAt,
                                    HierarchyEventAction Parsed);

    /// <summary>
    /// CAkAction: id, scope, type, referenced id, (v &gt; 65) IsBus, AkPropBundle, then the
    /// type's own parameters, which for Play are CAkActionParams, the bank id and (v &gt;= 144)
    /// the bank type.
    /// </summary>
    public static ActionSpan Action(byte[] body, uint version)
    {
        var parsed = (HierarchyEventAction)HircBank.ReadExact(EAKBKHircType.EventAction, body, version, "action");
        var ar = HircBank.Archive(body, version);
        ar.Read<uint>();
        var scopeAt = (int)ar.Position; ar.Read<EAkActionScope>();
        var typeAt = (int)ar.Position; ar.Read<EAkActionType>();
        var refAt = (int)ar.Position; ar.Read<uint>();
        var isBusAt = -1;
        if (ar.Version > 65) { isBusAt = (int)ar.Position; ar.ReadBool(); }
        var propsAt = (int)ar.Position;
        _ = new AkPropBundle(ar);
        if (parsed.EventActionType == EAkActionType.Play) _ = new CAkActionPlay(ar);
        Check(parsed.EventActionType != EAkActionType.Play || ar.Position == body.Length, "Play action data ends the action");

        Check(body[scopeAt] == (byte)parsed.EventActionScope, "EventActionScope");
        Check(body[typeAt] == (byte)parsed.EventActionType, "EventActionType");
        Check(U32(body, refAt) == parsed.ReferencedId, "ReferencedId");
        return new ActionSpan(scopeAt, typeAt, refAt, isBusAt, propsAt, parsed);
    }
}

/// <summary>
/// Writes HIRC objects field by field, in the order and widths CUE4Parse reads them. Every
/// writer can reproduce the game's own object from that object's parsed values, and the
/// builder makes it do so before trusting it with anything new.
/// </summary>
public static class HircWriter
{
    /// <summary>
    /// The BaseHierarchy: every field up to and including DirectParentId written from the
    /// parsed values, then the rest of the block (MIDI and priority flags, props,
    /// positioning, aux, advanced settings, states, RTPCs) carried over as the template's own
    /// bytes, its bounds as found by the reader.
    /// </summary>
    /// <param name="dropProps">properties left out of the object's own bundle (a copy that must
    /// not loop where its template does); the rest of the bundle is written from the parsed values,
    /// by a writer that reproduces the template's bundle first</param>
    /// <param name="ignoreParentLimit">set the object's "ignore the parent's playback limit" flag, so its
    /// sounds are not counted by a limit above it (a voice mixer's one-sound-per-character); the rest of
    /// its playback-limit block stays the template's</param>
    public static void Base(BinaryWriter w, byte[] template, HircLayout.BaseSpan span, uint version,
                            uint? directParentId = null, IReadOnlySet<byte> dropProps = null, bool ignoreParentLimit = false)
    {
        var b = span.Parsed;
        w.Write(b.OverrideFx);
        w.Write((byte)b.FxParams.Effects.Length);
        if (b.FxParams.Effects.Length > 0)
        {
            w.Write(b.FxParams.BypassAll);
            foreach (var fx in b.FxParams.Effects)
            {
                if (version > 145) throw new NotSupportedException("AkFx v>145");
                w.Write(fx.FXIndex); w.Write(fx.FXId); w.Write(fx.IsShareSet); w.Write(fx.IsRendered);
            }
        }
        if (version > 136)
        {
            w.Write(b.OverrideParentMetadataFlag);
            w.Write((byte)b.FxChunks.Length);
            foreach (var c in b.FxChunks) { w.Write(c.FxIndex); w.Write(c.FxId); w.Write(c.IsShareSet); }
        }
        if (version > 89 && version <= 145) w.Write(b.OverrideAttachmentParams);
        w.Write(b.OverrideBusId);
        w.Write(directParentId ?? b.DirectParentId);
        // The rest of the block is the template's bytes, with only the pieces asked for rewritten
        // from parsed values: none, for every copy that needs neither, which is then the template's.
        var patches = new List<(int At, int End, byte[] Bytes)>();
        var props = dropProps is { Count: > 0 } ? HircLayout.Props(template, span, version) : null;
        if (props is not null && (props.Parsed.Props.Any(p => dropProps.Contains(p.Id)) ||
                                  (props.Parsed.PropRanges ?? []).Any(p => dropProps.Contains(p.Id))))
            patches.Add((props.At, props.End,
                         PropBundle([.. props.Parsed.Props.Where(p => !dropProps.Contains(p.Id))],
                                    [.. (props.Parsed.PropRanges ?? []).Where(p => !dropProps.Contains(p.Id))])));
        if (ignoreParentLimit)
        {
            var adv = HircLayout.Adv(template, span, version);
            patches.Add((adv.At, adv.At + 6,
                         AdvSettings(adv.Parsed, (byte)(Convert.ToByte(adv.Parsed.AdvSettingsFlags) | IgnoreParentMaxNumInst), adv.Parsed.MaxNumInstance)));
        }
        var pos = span.ParentAt + 4;
        foreach (var (at, end, bytes) in patches.OrderBy(p => p.At))
        {
            w.Write(template, pos, at - pos);
            w.Write(bytes);
            pos = end;
        }
        w.Write(template, pos, span.End - pos);
    }

    /// <summary>The playback-limit flag bit "ignore the parent's max instances" (wwiser's bIgnoreParentMaxNumInst).</summary>
    public const byte IgnoreParentMaxNumInst = 0x08;

    /// <summary>
    /// AkAdvSettingsParams as these banks lay it out: flags, virtual-voice queue behaviour, max
    /// instances (u16), below-threshold behaviour, HDR envelope flags. <see cref="HircLayout.Adv"/>
    /// only accepts a block this writes back byte for byte.
    /// </summary>
    public static byte[] AdvSettings(AkAdvSettingsParams a, byte flags, ushort maxNumInstance) =>
        [flags, Convert.ToByte(a.VirtualQueueBehavior), (byte)maxNumInstance, (byte)(maxNumInstance >> 8),
         Convert.ToByte(a.BelowThresholdBehavior), Convert.ToByte(a.HdrEnvelopeFlags)];

    /// <summary>
    /// AkPropBundle as this bank version lays it out: the properties (count, ids, values), then
    /// the ranged modifiers (count, ids, min/max pairs). <see cref="HircLayout.Props"/> only
    /// accepts a bundle this writes back byte for byte.
    /// </summary>
    public static byte[] PropBundle(IReadOnlyList<AkProp> props, IReadOnlyList<AkPropRange> ranges)
    {
        ranges ??= [];
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)props.Count);
        foreach (var p in props) w.Write(p.Id);
        foreach (var p in props) w.Write(p.Value.u32);
        w.Write((byte)ranges.Count);
        foreach (var r in ranges) w.Write(r.Id);
        foreach (var r in ranges) { w.Write(r.Min.u32); w.Write(r.Max.u32); }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// CAkSound with a chosen id and source: the plugin id exactly as the template stores it,
    /// then source type, source id, in-memory size and source flags as given, the template's
    /// plugin parameters if any, and the template's BaseHierarchy (so the new Sound routes
    /// exactly like the one it was made from).
    /// </summary>
    public static byte[] Sound(byte[] template, HircLayout.SoundSpan span, uint version, uint id,
                               EAKBKSourceType sourceType, uint sourceId, uint inMemoryMediaSize, byte sourceFlags,
                               uint? directParentId = null, IReadOnlySet<byte> dropProps = null, bool ignoreParentLimit = false)
    {
        var s = span.Parsed.Source;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(id);
        w.Write(template, span.SourceStart, span.SourceTypeAt - span.SourceStart);
        w.Write((byte)sourceType);
        w.Write(sourceId);
        w.Write(inMemoryMediaSize);
        w.Write(sourceFlags);
        if (s.HasPluginParams) w.Write(template, span.PluginParamsAt, span.SourceEnd - span.PluginParamsAt);
        Base(w, template, span.Base, version, directParentId, dropProps, ignoreParentLimit);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>The template Sound written back from its own parsed values.</summary>
    public static byte[] SoundAsIs(byte[] template, HircLayout.SoundSpan span, uint version)
    {
        var s = span.Parsed.Source;
        return Sound(template, span, version, span.Parsed.Id, s.SourceType, s.SourceId, s.InMemoryMediaSize, (byte)s.BankSourceFlags);
    }

    /// <summary>CAkActorMixer: id, BaseHierarchy, AkChildren.</summary>
    /// <summary>
    /// CAkRanSeqCntr with a chosen id, children and playlist: the BaseHierarchy and the container's
    /// own fields as the template has them, so the copy plays, sends and loops exactly as it does.
    /// </summary>
    public static byte[] RandomContainer(byte[] template, HircLayout.ContainerSpan span, uint version, uint id,
                                         IReadOnlyList<uint> childIds, IReadOnlyList<(uint Id, int Weight)> playlist,
                                         bool ignoreParentLimit = false)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(id);
        Base(w, template, span.Base, version, ignoreParentLimit: ignoreParentLimit);
        w.Write(template, span.Base.End, span.ChildrenAt - span.Base.End);
        w.Write((uint)childIds.Count);
        foreach (var c in childIds) w.Write(c);
        w.Write((ushort)playlist.Count);
        foreach (var (pid, weight) in playlist) { w.Write(pid); w.Write(weight); }
        w.Flush();
        return ms.ToArray();
    }

    public static byte[] Mixer(byte[] template, HircLayout.MixerSpan span, uint version, uint[] childIds)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(span.Parsed.Id);
        Base(w, template, span.Base, version);
        w.Write((uint)childIds.Length);
        foreach (var c in childIds) w.Write(c);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// CAkAction with a chosen id and target: scope, type and IsBus from the template, the
    /// prop bundle and the type's own parameters carried over, bounds from the reader.
    /// </summary>
    public static byte[] Action(byte[] template, HircLayout.ActionSpan span, uint version, uint id, uint referencedId)
    {
        var a = span.Parsed;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(id);
        w.Write((byte)a.EventActionScope);
        w.Write((byte)a.EventActionType);
        w.Write(referencedId);
        if (version > 65) w.Write(a.IsBus);
        w.Write(template, span.PropsAt, template.Length - span.PropsAt);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>CAkEvent for 122 &lt; v &lt;= 154: id, action count as a 7-bit big-endian varint, action ids.</summary>
    public static byte[] Event(uint id, IReadOnlyList<uint> actions, uint version)
    {
        if (version <= 122 || version > 154) throw new NotSupportedException($"CAkEvent for bank version {version}");
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(id);
        var n = actions.Count;
        var groups = new List<byte>();
        do { groups.Insert(0, (byte)(n & 0x7F)); n >>= 7; } while (n > 0);
        for (var i = 0; i < groups.Count - 1; i++) groups[i] |= 0x80;
        w.Write(groups.ToArray());
        foreach (var a in actions) w.Write(a);
        w.Flush();
        return ms.ToArray();
    }
}
