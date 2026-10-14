using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

namespace MRAudioKit;

/// <summary>
/// Follows a <see cref="VoiceLine.Target"/> path through a VoiceItem's properties (as
/// UAssetAPI reads them) and adds the line's VoiceID where it points.
///
/// Segments, separated by dots outside brackets:
///   Name            a struct field (step into it) or, last, the int list to add to;
///   Name[3]         element 3 of a list of structs;
///   Name[K=V,...]   the element whose keys hold those values (K may be dotted, as in
///                   BuildingTag.TagName=...); there must be one;
///   Name[+K=V,...]  the same, made when there is none: a copy of the list's first element
///                   with its voice lists emptied, nested lists cut to one emptied element,
///                   and the keys set.
///
/// Keys rather than an index wherever an entry has them: an index names a position that a
/// game update can shift, and two lines for one new entry (two lines for the same victim)
/// have to land in the ONE entry the first of them made -- the game keys entries into a
/// dict and keeps only the later of two with the same keys.
///
/// It also reports a TEMPLATE voice: a line that already sits where the new one goes (or,
/// for a new entry, in the element it was copied from). The builder copies that line's
/// table row, event and bank routing, so a new line is categorised, heard and mixed exactly
/// like its siblings without any trigger needing code of its own.
/// </summary>
public static class VoiceItemPath
{
    /// <summary>Int lists that hold VoiceIDs. Any other int list (SourceAbilityIDs...) is a key.</summary>
    public static readonly string[] VoiceLists = ["Voices", "BaseVoices", "AdvanceVoices", "Lines"];

    public sealed record Result(int TemplateVoiceId, string Description);

    public static Result Apply(UAsset asset, NormalExport export, string path, int voiceId, IReadOnlySet<int> ours = null)
    {
        var segments = Split(path);
        List<PropertyData> container = export.Data;
        StructPropertyData owner = null;            // null: the export's own top level
        var template = 0;
        var trail = new List<string>();
        for (var i = 0; i < segments.Count; i++)
        {
            var (name, spec) = ParseSegment(segments[i]);
            var prop = container.FirstOrDefault(p => p.Name.ToString() == name)
                       ?? throw new InvalidDataException($"{string.Join(".", trail.Append(name))}: no such field (have {string.Join(", ", container.Select(p => p.Name.ToString()))})");
            var last = i == segments.Count - 1;

            if (spec is null && last)
            {
                // The list the line joins.
                if (prop is not ArrayPropertyData list || !VoiceLists.Contains(name))
                    throw new InvalidDataException($"{path}: the last segment must be a voice list ({string.Join("/", VoiceLists)})");
                var existing = list.Value.OfType<IntPropertyData>().Select(v => v.Value).ToList();
                if (existing.Contains(voiceId)) throw new InvalidOperationException($"{path}: already holds {voiceId}");
                if (template == 0) template = existing.FirstOrDefault(v => ours?.Contains(v) != true);
                list.Value = [.. list.Value, new IntPropertyData(list.Name) { Value = voiceId }];
                // An empty list was read as zero; holding a line, it no longer is.
                if (owner is null) UnversionedZero.RefreshTop(export, list); else UnversionedZero.RefreshIn(owner, list);
                trail.Add(name);
                return new Result(template, $"{string.Join(".", trail)} += {voiceId}");
            }

            if (spec is null)
            {
                owner = prop as StructPropertyData
                    ?? throw new InvalidDataException($"{name}: expected a struct, found {prop.GetType().Name}");
                container = owner.Value;
                trail.Add(name);
                continue;
            }

            if (prop is not ArrayPropertyData array || array.Value.Length == 0 || array.Value[0] is not StructPropertyData)
                throw new InvalidDataException($"{name}[...]: expected a non-empty list of structs");
            StructPropertyData element;
            if (int.TryParse(spec, out var index))
            {
                if (index < 0 || index >= array.Value.Length)
                    throw new InvalidDataException($"{name}[{index}]: there are {array.Value.Length} entries");
                element = (StructPropertyData)array.Value[index];
                trail.Add($"{name}[{index}]");
            }
            else
            {
                var create = spec.StartsWith('+');
                var keys = ParseKeys(create ? spec[1..] : spec);
                if (keys.Count == 0) throw new FormatException($"{name}[{spec}]: no keys");
                var at = Array.FindIndex(array.Value, e => e is StructPropertyData s && keys.All(kv => SameKey(Read(s, kv.Key), kv.Value)));
                if (at >= 0)
                {
                    element = (StructPropertyData)array.Value[at];
                    // An entry with no lines of its own (one an earlier line made has one)
                    // borrows its routing from the list's first, as a new entry does.
                    if (template == 0) template = FirstVoice(element, ours) is var own and not 0 ? own : FirstVoice((StructPropertyData)array.Value[0], ours);
                    trail.Add($"{name}[{at}: {string.Join(",", keys.Select(k => $"{k.Key}={k.Value}"))}]");
                }
                else if (!create)
                    throw new InvalidOperationException($"{name}: no entry with {spec}");
                else
                {
                    var first = (StructPropertyData)array.Value[0];
                    if (template == 0) template = FirstVoice(first, ours);
                    element = (StructPropertyData)first.Clone();
                    Empty(element);
                    foreach (var (k, v) in keys) Assign(asset, element, k, v);
                    // The copy's fields keep the zero flags read from the original; recompute them
                    // from the values it holds now, or a key set from 0 is written as 0.
                    UnversionedZero.RefreshAll(element);
                    array.Value = [.. array.Value, element];
                    trail.Add($"{name}[{array.Value.Length - 1} new: {spec[1..]}]");
                }
            }
            owner = element;
            container = element.Value;
        }
        throw new InvalidDataException($"{path}: does not end at a voice list");
    }

    // A copied entry starts with no lines: voice lists emptied, nested lists of structs cut to
    // their first element (itself emptied), keys left for Assign to set.
    private static void Empty(StructPropertyData s)
    {
        foreach (var p in s.Value)
        {
            if (p is ArrayPropertyData a)
            {
                if (VoiceLists.Contains(p.Name.ToString())) a.Value = [];
                else if (a.Value.Length > 0 && a.Value[0] is StructPropertyData first)
                {
                    var keep = (StructPropertyData)first.Clone();
                    Empty(keep);
                    a.Value = [keep];
                }
            }
            else if (p is StructPropertyData inner) Empty(inner);
        }
    }

    // The game's own first line in s: never one of this build's (its row, event and bank
    // objects are being made in the same pass, so it has nothing yet to copy from).
    private static int FirstVoice(StructPropertyData s, IReadOnlySet<int> ours)
    {
        foreach (var p in s.Value)
        {
            if (p is ArrayPropertyData a && VoiceLists.Contains(p.Name.ToString())
                && a.Value.OfType<IntPropertyData>().FirstOrDefault(v => ours?.Contains(v.Value) != true) is { } v) return v.Value;
            if (p is ArrayPropertyData sa && sa.Value.FirstOrDefault() is StructPropertyData es && FirstVoice(es, ours) is var n and not 0) return n;
            if (p is StructPropertyData inner && FirstVoice(inner, ours) is var m and not 0) return m;
        }
        return 0;
    }

    private static PropertyData Find(StructPropertyData s, string dotted)
    {
        var parts = dotted.Split('.');
        List<PropertyData> c = s.Value;
        PropertyData p = null;
        for (var i = 0; i < parts.Length; i++)
        {
            p = c.FirstOrDefault(x => x.Name.ToString() == parts[i])
                ?? throw new InvalidDataException($"key {dotted}: no field {parts[i]} (have {string.Join(", ", c.Select(x => x.Name.ToString()))})");
            if (i < parts.Length - 1)
                c = p is StructPropertyData st ? st.Value : throw new InvalidDataException($"key {dotted}: {parts[i]} is not a struct");
        }
        return p;
    }

    private static string Read(StructPropertyData s, string dotted)
    {
        try
        {
            return Find(s, dotted) switch
            {
                IntPropertyData i => i.Value.ToString(),
                UInt32PropertyData u => u.Value.ToString(),
                BoolPropertyData b => b.Value ? "true" : "false",
                // A zero FName reads as null here and as "None" through CUE4Parse (the window).
                NamePropertyData n => n.Value?.ToString() ?? "None",
                StrPropertyData str => str.Value?.Value,
                EnumPropertyData e => e.Value?.ToString(),
                _ => null,
            };
        }
        catch (InvalidDataException) { return null; }
    }

    private static void Assign(UAsset asset, StructPropertyData s, string dotted, string value)
    {
        switch (Find(s, dotted))
        {
            case IntPropertyData i: i.Value = int.Parse(value); break;
            case UInt32PropertyData u: u.Value = uint.Parse(value); break;
            case BoolPropertyData b: b.Value = bool.Parse(value); break;
            case FloatPropertyData f: f.Value = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
            case NamePropertyData n: asset.AddNameReference(new FString(value)); n.Value = FName.FromString(asset, value); break;
            case StrPropertyData str: str.Value = new FString(value); break;
            case EnumPropertyData e: asset.AddNameReference(new FString(Bare(value))); e.Value = FName.FromString(asset, Bare(value)); break;
            case var other: throw new NotSupportedException($"key {dotted}: cannot set a {other.GetType().Name}");
        }
    }

    /// <summary>
    /// An enum value by its member name alone. CUE4Parse, which the window reads entries with,
    /// spells an enum "EAbilityVoiceStage::Activate"; UAssetAPI, which the build edits with,
    /// reads the same value as "Activate". Keys are compared, and enums written, this way.
    /// No other key value carries "::" (tags and names use dots).
    /// </summary>
    public static string Bare(string value) =>
        value is not null && value.LastIndexOf("::", StringComparison.Ordinal) is var c and >= 0 ? value[(c + 2)..] : value;

    /// <summary>
    /// Two spellings of one key value: enums by member name, and without regard to case --
    /// names are case-insensitive in the engine, and the two readers disagree on a bool's
    /// case ("True" against "true").
    /// </summary>
    public static bool SameKey(string a, string b) => string.Equals(Bare(a), Bare(b), StringComparison.OrdinalIgnoreCase);

    private static List<string> Split(string path)
    {
        var parts = new List<string>();
        var depth = 0; var start = 0;
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] == '[') depth++;
            else if (path[i] == ']') depth--;
            else if (path[i] == '.' && depth == 0) { parts.Add(path[start..i]); start = i + 1; }
        }
        parts.Add(path[start..]);
        return parts;
    }

    private static (string name, string spec) ParseSegment(string seg)
    {
        var b = seg.IndexOf('[');
        if (b < 0) return (seg, null);
        if (!seg.EndsWith(']')) throw new FormatException($"segment {seg}: unclosed [");
        return (seg[..b], seg[(b + 1)..^1]);
    }

    private static List<(string Key, string Value)> ParseKeys(string spec) =>
        spec.Length == 0 ? [] : spec.Split(',').Select(kv =>
        {
            var eq = kv.IndexOf('=');
            if (eq <= 0) throw new FormatException($"key {kv}: expected K=V");
            return (kv[..eq].Trim(), kv[(eq + 1)..].Trim());
        }).ToList();
}
