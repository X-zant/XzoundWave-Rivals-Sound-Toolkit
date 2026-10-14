using System.Buffers.Binary;
using CUE4Parse.UE4.Wwise;
using CUE4Parse.UE4.Wwise.Enums;
using CUE4Parse.UE4.Wwise.Objects.HIRC;
using CUE4Parse.UE4.Wwise.Objects.HIRC.Containers;

namespace MRAudioKit;

/// <summary>One HIRC object: its type byte, its body, and what CUE4Parse reads it as.</summary>
public sealed class HircObject
{
    public byte RawType;
    public EAKBKHircType Type;
    public byte[] Body;
    public AbstractHierarchy Node;
    public uint Id => Node?.Id ?? BinaryPrimitives.ReadUInt32LittleEndian(Body);
}

/// <summary>
/// A soundbank as sections, with its HIRC split into objects, each read by CUE4Parse's own
/// reader for its type over exactly its own body. A reader that stops short of the body or
/// runs past it is an error here, not a silent reposition: the voice-line builder writes
/// objects next to these, and it can only trust a layout the reader accounts for completely.
///
/// <see cref="BnkBuilder"/> swaps media and copies HIRC through. This is the other half:
/// the bank's hierarchy as objects that can be added to and rewritten one field at a time.
/// </summary>
public sealed class HircBank
{
    public string Name;
    public uint Version;
    public List<BnkBuilder.Section> Sections;
    public List<HircObject> Objects = [];
    public List<BnkBuilder.MediaEntry> Media = [];
    public Dictionary<uint, AbstractHierarchy> Nodes = [];

    public static HircBank Parse(string name, byte[] bytes)
    {
        var b = new HircBank { Name = name, Sections = BnkBuilder.ReadSections(bytes) };
        var bkhd = b.Sections.Single(s => s.Tag == "BKHD");
        b.Version = BinaryPrimitives.ReadUInt32LittleEndian(bkhd.Body);
        var didx = b.Sections.FirstOrDefault(s => s.Tag == "DIDX");
        if (didx is not null) b.Media = BnkBuilder.ReadDidx(didx.Body);

        var hirc = b.Sections.Single(s => s.Tag == "HIRC").Body;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(hirc);
        var p = 4;
        for (var i = 0; i < count; i++)
        {
            var raw = hirc[p];
            var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(hirc.AsSpan(p + 1));
            var body = hirc.AsSpan(p + 5, len).ToArray();
            p += 5 + len;
            var o = new HircObject { RawType = raw, Type = raw.MapToCurrent(b.Version), Body = body };
            o.Node = ReadExact(o.Type, body, b.Version, $"object #{i}");
            b.Objects.Add(o);
            b.Nodes[o.Id] = o.Node;
        }
        if (p != hirc.Length) throw new InvalidDataException($"HIRC has {hirc.Length - p} trailing bytes");
        return b;
    }

    public static FWwiseArchive Archive(byte[] body, uint version) =>
        new("obj", body) { Version = version };

    /// <summary>The type's own CUE4Parse reader, which must consume the body exactly.</summary>
    public static AbstractHierarchy ReadExact(EAKBKHircType type, byte[] body, uint version, string what)
    {
        var ar = Archive(body, version);
        AbstractHierarchy node = type switch
        {
            EAKBKHircType.State => new HierarchySettings(ar),
            EAKBKHircType.SoundSfxVoice => new HierarchySoundSfxVoice(ar),
            EAKBKHircType.EventAction => new HierarchyEventAction(ar),
            EAKBKHircType.Event => new HierarchyEvent(ar),
            EAKBKHircType.RandomSequenceContainer => new HierarchyRandomSequenceContainer(ar),
            EAKBKHircType.SwitchContainer => new HierarchySwitchContainer(ar),
            EAKBKHircType.ActorMixer => new HierarchyActorMixer(ar),
            EAKBKHircType.AudioBus => new HierarchyAudioBus(ar),
            EAKBKHircType.LayerContainer => new HierarchyLayerContainer(ar),
            EAKBKHircType.MusicSegment => new HierarchyMusicSegment(ar),
            EAKBKHircType.MusicTrack => new HierarchyMusicTrack(ar),
            EAKBKHircType.MusicSwitchContainer => new HierarchyMusicSwitchContainer(ar),
            EAKBKHircType.MusicRandomSequenceContainer => new HierarchyMusicRandomSequenceContainer(ar),
            EAKBKHircType.Attenuation => new HierarchyAttenuation(ar),
            EAKBKHircType.DialogueEvent => new HierarchyDialogueEvent(ar),
            EAKBKHircType.FxShareSet => new HierarchyFxShareSet(ar),
            EAKBKHircType.FxCustom => new HierarchyFxCustom(ar),
            EAKBKHircType.AuxiliaryBus => new HierarchyAuxiliaryBus(ar),
            EAKBKHircType.AudioDevice => new HierarchyAudioDevice(ar),
            _ => throw new InvalidDataException($"{what}: no reader for type {type}"),
        };
        if (ar.Position != body.Length)
            throw new InvalidDataException($"{what} ({type} {node.Id}): reader stopped at {ar.Position} of {body.Length}");
        return node;
    }

    /// <summary>The children of a container object, or null for an object that has none.</summary>
    public static uint[] ChildrenOf(AbstractHierarchy node) => node switch
    {
        HierarchyRandomSequenceContainer r => r.ChildIds,
        HierarchyActorMixer m => m.ChildIds,
        HierarchySwitchContainer sw => sw.ChildIds,
        HierarchyLayerContainer l => l.ChildIds,
        _ => null,
    };

    /// <summary>
    /// First HIRC index of an object's subtree. Banks lay every hierarchy out post-order --
    /// children's subtrees in ascending id, then the parent -- so a new child goes in front
    /// of its next sibling's subtree, or directly before the parent when it sorts last.
    /// </summary>
    public int SubtreeStart(uint id)
    {
        var min = Objects.FindIndex(o => o.Id == id);
        if (ChildrenOf(Nodes[id]) is { } kids)
            foreach (var k in kids) if (Nodes.ContainsKey(k)) min = Math.Min(min, SubtreeStart(k));
        return min;
    }

    /// <summary>
    /// Embed a new medium: DIDX gains the entry in the bank's own id order, and DATA is laid
    /// out again in DIDX order at the bank's own alignment, the way Wwise writes it. Every
    /// existing medium keeps its bytes; only its offset can move.
    /// </summary>
    public void AddMedia(uint id, byte[] payload) => Relayout(blobs =>
    {
        if (blobs.Any(b => b.Id == id)) throw new InvalidOperationException($"{Name} already embeds media {id}");
        blobs.Add((id, payload));
    });

    /// <summary>Take an embedded medium out again; the others keep their bytes and order.</summary>
    public void RemoveMedia(uint id) => Relayout(blobs =>
    {
        if (blobs.RemoveAll(b => b.Id == id) == 0) throw new InvalidOperationException($"{Name} embeds no media {id}");
    });

    private void Relayout(Action<List<(uint Id, byte[] Bytes)>> change)
    {
        var di = Sections.FindIndex(s => s.Tag == "DIDX");
        var da = Sections.FindIndex(s => s.Tag == "DATA");
        if (di < 0 || da < 0) throw new InvalidDataException($"{Name} embeds no media (no DIDX/DATA)");
        var entries = BnkBuilder.ReadDidx(Sections[di].Body);
        var align = BnkBuilder.InferAlignment(entries);
        var data = Sections[da].Body;
        var blobs = entries.Select(e => (e.Id, Bytes: data.AsSpan((int)e.Offset, (int)e.Size).ToArray())).ToList();
        var sorted = entries.Select(e => e.Id).SequenceEqual(entries.Select(e => e.Id).OrderBy(x => x));
        change(blobs);
        if (sorted) blobs = blobs.OrderBy(b => b.Id).ToList();

        var didx = new byte[blobs.Count * 12];
        using var blob = new MemoryStream();
        for (var i = 0; i < blobs.Count; i++)
        {
            var offset = align <= 1 ? blob.Length : (blob.Length + align - 1) / align * align;
            while (blob.Length < offset) blob.WriteByte(0);
            BinaryPrimitives.WriteUInt32LittleEndian(didx.AsSpan(i * 12), blobs[i].Id);
            BinaryPrimitives.WriteUInt32LittleEndian(didx.AsSpan(i * 12 + 4), (uint)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(didx.AsSpan(i * 12 + 8), (uint)blobs[i].Bytes.Length);
            blob.Write(blobs[i].Bytes);
        }
        Sections[di] = new BnkBuilder.Section("DIDX", didx);
        Sections[da] = new BnkBuilder.Section("DATA", blob.ToArray());
        Media = BnkBuilder.ReadDidx(didx);
    }

    /// <summary>The bytes of one embedded medium.</summary>
    public byte[] MediaBytes(uint id)
    {
        var e = Media.Single(m => m.Id == id);
        var d = Sections.Single(s => s.Tag == "DATA").Body;
        return d.AsSpan((int)e.Offset, (int)e.Size).ToArray();
    }

    /// <summary>The bank as bytes again: every section in its own order, HIRC rebuilt from Objects.</summary>
    public byte[] Write()
    {
        using var ms = new MemoryStream();
        foreach (var s in Sections)
        {
            var body = s.Tag == "HIRC" ? HircBody() : s.Body;
            ms.Write(System.Text.Encoding.ASCII.GetBytes(s.Tag));
            var len = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)body.Length);
            ms.Write(len);
            ms.Write(body);
        }
        return ms.ToArray();
    }

    private byte[] HircBody()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((uint)Objects.Count);
        foreach (var o in Objects)
        {
            w.Write(o.RawType);
            w.Write((uint)o.Body.Length);
            w.Write(o.Body);
        }
        w.Flush();
        return ms.ToArray();
    }
}
