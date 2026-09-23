using System.Collections;
using System.IO;
using System.Reflection;
using CUE4Parse.UE4.Wwise;
using CUE4Parse.UE4.Wwise.Objects;
using CUE4Parse.UE4.Wwise.Enums;
using CUE4Parse.UE4.Wwise.Objects.HIRC;
using CUE4Parse.UE4.Wwise.Objects.HIRC.Containers;

namespace MRAudioKit;

/// <summary>
/// What a .bnk says about a sound BEYOND which bytes to play.
///
/// The question this answers: could the tool suggest which sounds are usable as
/// replacement ult music? That needs three things the audio itself cannot say -- what
/// an event does when it fires, whether the sound loops or is cut off, and what volume
/// or pitch the game applies on top. All three are Wwise object properties, so the
/// place to look is the hierarchy, not the media.
///
/// This prints what the parser exposes, for a real event, so the answer rests on the
/// game's own data rather than on what the format is supposed to contain.
/// </summary>
public static class WwiseProbe
{
    /// <summary>XzoundWave.exe --wwiseprobe &lt;skinId&gt; [eventNameFragment]</summary>
    public static int Run(string skinId, string fragment)
    {
        void W(string s) => Console.WriteLine(s);

        var settings = Settings.Load();
        settings.AutoDetect();
        var session = new GameSession();
        session.Mount(settings, _ => { });

        var charId = skinId[..4];
        var ch = session.Characters.FirstOrDefault(c => c.CharId == charId);
        var skin = ch?.Skins.FirstOrDefault(s => s.SkinId == skinId);
        if (skin is null) { W($"no such skin: {skinId}"); return 1; }

        // ---- 1. what the parser gives us at all --------------------------------
        W("WHAT THE PARSER EXPOSES");
        foreach (var t in new[]
                 {
                     typeof(HierarchyEvent), typeof(HierarchyEventAction),
                     typeof(HierarchySoundSfxVoice), typeof(HierarchyRandomSequenceContainer),
                     typeof(HierarchySwitchContainer), typeof(HierarchyActorMixer),
                     typeof(HierarchyMusicSegment), typeof(HierarchyMusicTrack),
                     typeof(HierarchyAttenuation), typeof(HierarchyAudioBus),
                     typeof(BaseHierarchy), typeof(AkPlayListItem),
                     typeof(AkBankSourceData),
                 })
            W($"  {t.Name,-36} {string.Join(", ", Members(t))}");

        W("");
        W("  EAkPropID, by the number stored in the bank:");
        foreach (var v in Enum.GetValues(typeof(EAkPropID)).Cast<object>().Take(48))
            W($"    {Convert.ToInt32(v),3} {v}");

        // A prop's value is a union. Which member is the real one has to be read off
        // the type rather than guessed at, or a float comes back as its own bit
        // pattern read as an int.
        var propType = Type.GetType("CUE4Parse.UE4.Wwise.Objects.AkProp, CUE4Parse")
                    ?? typeof(AkPropBundle).Assembly.GetTypes()
                           .FirstOrDefault(t => t.Name is "AkProp");
        var unionType = typeof(AkPropBundle).Assembly.GetTypes()
                           .FirstOrDefault(t => t.Name is "AkUnionValue");
        W("");
        W($"  AkProp        {(propType is null ? "not found" : string.Join(", ", Members(propType)))}");
        W($"  AkUnionValue  {(unionType is null ? "not found" : string.Join(", ", Members(unionType)))}");
        W($"  AkPropBundle  {string.Join(", ", Members(typeof(AkPropBundle)))}");

        // ---- 2. the same thing, on the game's own data -------------------------
        foreach (var file in skin.Banks)
        {
            var bankName = Path.GetFileName(file.Path);
            WwiseReader w;
            try
            {
                using var ar = file.CreateReader();
                w = new WwiseReader(new FWwiseArchive(ar), new WwiseGameFileSource(file));
            }
            catch { continue; }
            if (w.Hierarchies is null) continue;

            var nodes = new Dictionary<uint, AbstractHierarchy>();
            foreach (var h in w.Hierarchies)
                if (h.Data is not null) nodes[h.Data.Id] = h.Data;

            var events = w.Hierarchies
                .Select(h => h.Data).OfType<HierarchyEvent>()
                .Select(e => (Ev: e, Name: session.EventName(e.Id)))
                .Where(x => x.Name is not null &&
                            (fragment is null || fragment == "?" ||
                             x.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (events.Count == 0) continue;

            // "?" lists what is in there, for finding the event worth looking at.
            if (fragment == "?")
            {
                W("");
                W($"=== {bankName} ===  {events.Count} named event(s)");
                foreach (var (_, n) in events) W("  " + n);
                continue;
            }
            events = events.Take(fragment is null ? 3 : 200).ToList();

            W("");
            W($"=== {bankName} ===");
            // Which Wwise wrote this. The property-id table is version-specific, so a
            // name that does not fit its value is a table question, not a parse error.
            var hdr = Readable(w.GetType()).FirstOrDefault(m =>
                m.Name.Contains("Header", StringComparison.OrdinalIgnoreCase));
            if (hdr.Get is not null)
                W($"  bank header: {Dump(hdr.Get(w) ?? new object())}");
            foreach (var (ev, name) in events)
            {
                W("");
                W($"EVENT  {name}   ({ev.EventActionIds.Length} action(s))");
                foreach (var actionId in ev.EventActionIds)
                {
                    if (!nodes.TryGetValue(actionId, out var an) || an is not HierarchyEventAction act)
                    { W($"  action {actionId}: not in this .bnk"); continue; }

                    W($"  ACTION {act.EventActionType}  scope={act.EventActionScope}" +
                      $"  -> {act.ReferencedId}  {ActionDetail(act)}");
                    Walk(act.ReferencedId, nodes, "    ", 0);
                }
            }
        }

        W("");
        W("Legend: [] means the parser read the field and it was empty, not that the");
        W("format has nothing there.");
        return 0;

        // Follow the object an action points at, down through whatever contains it.
        void Walk(uint id, Dictionary<uint, AbstractHierarchy> nodes, string pad, int depth)
        {
            if (depth > 6 || !nodes.TryGetValue(id, out var node)) return;
            switch (node)
            {
                case HierarchySoundSfxVoice snd:
                    var media = snd.Source.SourceId != 0 ? snd.Source.SourceId : snd.Source.FileId;
                    W($"{pad}SOUND  media {media}  {snd.Source.SourceType}  {Shape(snd)}");
                    break;

                case HierarchyRandomSequenceContainer rnd:
                    W($"{pad}CONTAINER  {rnd.Mode}  loop={(rnd.LoopCount == 0 ? "INFINITE" : rnd.LoopCount.ToString())}" +
                      $"  transition={rnd.TransitionMode}/{rnd.TransitionTime}  {Shape(rnd)}");
                    foreach (var child in Children(rnd)) Walk(child, nodes, pad + "  ", depth + 1);
                    return;

                case HierarchySwitchContainer sw:
                    W($"{pad}SWITCH  {Dump(sw, "Id")}");
                    foreach (var child in Children(sw)) Walk(child, nodes, pad + "  ", depth + 1);
                    return;

                default:
                    W($"{pad}{node.GetType().Name}  {Dump(node, "Id")}");
                    foreach (var child in Children(node)) Walk(child, nodes, pad + "  ", depth + 1);
                    return;
            }
        }
    }

    // CUE4Parse surfaces most of the Wwise objects as FIELDS, not properties -- reading
    // only properties makes every one of them look like it carries nothing but an Id.
    private static IEnumerable<(string Name, Type Type, Func<object, object> Get)> Readable(Type t)
    {
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            yield return (f.Name, f.FieldType, f.GetValue);
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.GetIndexParameters().Length == 0 && p.DeclaringType != typeof(object))
                yield return (p.Name, p.PropertyType, p.GetValue);
    }

    /// <summary>A Play action's delay and fade, a Stop action's fade -- the cut-off part.</summary>
    private static string ActionDetail(HierarchyEventAction act)
    {
        var bits = PropsText(Get(act, "PropBundle"));
        var data = Get(act, "ActionData");
        if (data is not null)
        {
            var inner = Readable(data.GetType())
                .Select(m =>
                {
                    try
                    {
                        var v = m.Get(data);
                        // The nested params object carries the fade curve and time.
                        if (v is not null && v.GetType().Name.Contains("Params"))
                            return $"{m.Name}{{{Dump(v)}}}";
                        var t = Text(v);
                        return t is null ? null : $"{m.Name}={t}";
                    }
                    catch { return null; }
                })
                .Where(x => x is not null).ToList();
            if (inner.Count > 0) bits.Add(string.Join(" ", inner));
        }
        return string.Join("  ", bits);
    }

    /// <summary>
    /// A set property, named. Wwise stores only what the author changed, so every
    /// entry here is something the game does to the sound that the file itself does
    /// not contain -- a volume trim, a pitch shift, a delay before it starts.
    /// </summary>
    private static List<string> PropsText(object bundle)
    {
        var bits = new List<string>();
        if (Get(bundle, "Props") is not IEnumerable props) return bits;

        foreach (var prop in props.Cast<object>())
        {
            var id = Readable(prop.GetType())
                .FirstOrDefault(m => m.Name is "Id" or "PropId" or "PId" or "Key");
            var val = Readable(prop.GetType())
                .FirstOrDefault(m => m.Name is "Value" or "PValue" or "UnionValue");

            // EAkPropID is byte-backed; handing IsDefined an int throws rather than
            // returning false.
            var rawId = id.Get?.Invoke(prop);
            var n = rawId is null ? -1 : Convert.ToInt32(rawId);
            var name = n < 0 ? "?"
                : n <= byte.MaxValue && Enum.IsDefined(typeof(EAkPropID), (byte)n)
                    ? ((EAkPropID)(byte)n).ToString()
                    : $"prop{n}";

            // The raw id travels with the name. The table is the parser's, not the
            // game's, so a name that looks wrong beside its value can be checked
            // against the number the bank actually stored.
            bits.Add($"{name}#{n}={Scalar(val.Get?.Invoke(prop))}");
        }
        return bits;
    }

    /// <summary>
    /// The number inside a union. AkUnionValue carries the same four bytes as both a
    /// float and a uint and resolves which one it is into Value -- reading f32 because
    /// it happens to come first gives an integer prop back as its own bit pattern
    /// read as a float, which is where 1.9E-19 came from.
    /// </summary>
    private static string Scalar(object v)
    {
        if (v is null) return "null";
        if (v.GetType().IsPrimitive || v.GetType().IsEnum || v is string) return v.ToString();

        if (Get(v, "Value") is { } resolved) return resolved.ToString();
        if (Get(v, "f32") is { } f) return f.ToString();
        return v.ToString();
    }

    private static IEnumerable<string> Members(Type t) =>
        Readable(t).Select(m => $"{m.Name}:{Short(m.Type)}");

    private static string Short(Type t) =>
        t.IsGenericType ? $"{t.Name.Split('`')[0]}<{string.Join(",", t.GetGenericArguments().Select(Short))}>"
        : t.IsArray ? Short(t.GetElementType()) + "[]"
        : t.Name;

    /// <summary>Every readable property with a value worth printing.</summary>
    private static string Dump(object o, params string[] skip)
    {
        var parts = new List<string>();
        foreach (var m in Readable(o.GetType()))
        {
            if (skip.Contains(m.Name)) continue;
            object v;
            try { v = m.Get(o); } catch { continue; }
            var text = Text(v);
            if (text is not null) parts.Add($"{m.Name}={text}");
        }
        return string.Join("  ", parts);
    }

    /// <summary>
    /// The part of an object's shared parameters worth reading: what the game adds on
    /// top of the audio (volume, pitch, playback speed, a delay), whether anything
    /// changes it at runtime, and who it inherits from.
    /// </summary>
    private static string Shape(object node)
    {
        var bp = Get(node, "BaseParams");
        if (bp is null) return "(no shared params)";

        var bits = PropsText(Get(bp, "PropBundle"));
        if (bits.Count == 0) bits.Add("no set props");

        // What a runtime curve actually targets. A property sitting at its minimum with
        // a curve pointing at it is not a silenced sound -- it is a sound whose level
        // the game drives.
        if (Get(bp, "RtpcList") is IEnumerable rtpcs)
            foreach (var r in rtpcs.Cast<object>())
                bits.Add("RTPC{" + Dump(r) + "}");

        // A randomised range on a property. Ignoring it would hide the difference
        // between a level that is off and one that is off on average.
        if (Get(Get(bp, "PropBundle"), "PropRanges") is IEnumerable ranges)
            foreach (var r in ranges.Cast<object>())
                bits.Add("RANGE{" + Dump(r) + "}");

        var states = (Get(bp, "StateGroups") as IEnumerable)?.Cast<object>().Count() ?? 0;
        if (states > 0)
        {
            bits.Add($"{states} state group(s)");
            foreach (var g in (Get(bp, "StateGroups") as IEnumerable).Cast<object>())
                bits.Add("STATE{" + Dump(g) + "}");
        }

        var pos = Get(bp, "PositioningParams");
        if (Get(pos, "IsLooping") is true) bits.Add("positioning loops");

        var parent = Get(bp, "DirectParentId");
        if (parent is uint pid and not 0) bits.Add($"parent {pid}");

        return string.Join("  ", bits);
    }

    private static object Get(object o, string name)
    {
        if (o is null) return null;
        var m = Readable(o.GetType()).FirstOrDefault(x => x.Name == name);
        if (m.Get is null) return null;
        try { return m.Get(o); } catch { return null; }
    }

    /// <summary>The property bundles, which is where volume, pitch and loop live.</summary>
    private static string Props(object o)
    {
        var parts = new List<string>();
        foreach (var m in Readable(o.GetType()))
        {
            if (!m.Name.Contains("Prop", StringComparison.OrdinalIgnoreCase) &&
                !m.Name.Contains("Param", StringComparison.OrdinalIgnoreCase) &&
                !m.Name.Contains("Rtpc", StringComparison.OrdinalIgnoreCase)) continue;
            object v;
            try { v = m.Get(o); } catch { continue; }
            parts.Add($"{m.Name}=" + (v is null ? "null" : Deep(v, 3)));
        }
        return parts.Count == 0 ? "(no property bundle on this type)" : string.Join("  ", parts);
    }

    private static string Deep(object v, int depth)
    {
        if (v is null) return "null";
        if (depth <= 0) return v.GetType().Name;
        if (v is string s) return s;
        if (v is IEnumerable list and not string)
        {
            var items = list.Cast<object>().Take(12).Select(x => Deep(x, depth - 1)).ToList();
            return "[" + string.Join(", ", items) + "]";
        }
        if (v.GetType().IsPrimitive || v.GetType().IsEnum) return v.ToString();

        var fields = Readable(v.GetType()).Select(m =>
        {
            try { return $"{m.Name}={Deep(m.Get(v), depth - 1)}"; }
            catch { return null; }
        }).Where(x => x is not null);
        return "{" + string.Join(" ", fields) + "}";
    }

    private static string Text(object v) => v switch
    {
        null => null,
        string s => s.Length == 0 ? null : s,
        IEnumerable e and not string => "[" + string.Join(",", e.Cast<object>().Take(8)) + "]",
        _ => v.ToString(),
    };

    /// <summary>Child ids, whatever the containing type calls them.</summary>
    private static IEnumerable<uint> Children(object node)
    {
        foreach (var p in Readable(node.GetType()))
        {
            if (!p.Name.Contains("Child", StringComparison.OrdinalIgnoreCase)) continue;
            object v;
            try { v = p.Get(node); } catch { continue; }
            if (v is IEnumerable e and not string)
                foreach (var x in e)
                    if (x is uint u) yield return u;
                    else if (x is int i) yield return (uint)i;
        }
    }
}
