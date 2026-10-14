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
    public static void Base(BinaryWriter w, byte[] template, HircLayout.BaseSpan span, uint version,
                            uint? directParentId = null)
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
        w.Write(template, span.ParentAt + 4, span.End - (span.ParentAt + 4));
    }

    /// <summary>
    /// CAkSound with a chosen id and source: the plugin id exactly as the template stores it,
    /// then source type, source id, in-memory size and source flags as given, the template's
    /// plugin parameters if any, and the template's BaseHierarchy (so the new Sound routes
    /// exactly like the one it was made from).
    /// </summary>
    public static byte[] Sound(byte[] template, HircLayout.SoundSpan span, uint version, uint id,
                               EAKBKSourceType sourceType, uint sourceId, uint inMemoryMediaSize, byte sourceFlags,
                               uint? directParentId = null)
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
        Base(w, template, span.Base, version, directParentId);
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
