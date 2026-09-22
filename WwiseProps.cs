using CUE4Parse.UE4.Wwise.Objects;
using CUE4Parse.UE4.Wwise.Objects.HIRC;
using CUE4Parse.UE4.Wwise.Objects.HIRC.Containers;

namespace MRAudioKit;

/// <summary>
/// What the game does to a sound that the audio file itself does not contain.
///
/// A .bnk stores only the properties an author changed, so everything here is a
/// deliberate edit: a volume trim, a pitch shift, a delay before the sound starts, a
/// loop. Replace the audio and these still apply -- which is why a replacement can come
/// out quieter, later or looping when the file plainly is not.
///
/// The property ids are VERSION SPECIFIC. This game's banks are version 145 (Wwise
/// 2023.1), whose table is the one Wwise has used since 128. CUE4Parse ships an older
/// table that omits LFE at id 1, so every name from there on is off by one through it --
/// which is how a sound's initial delay came out labelled "TransitionTime" and an
/// attenuation id came out as a denormal float called "MidiVelocityRangeMax".
///
/// Table: bnnm/wwiser, wwiser/parser/wdefs.py, AkPropID_128 (used for versions 128-145).
/// </summary>
public static class WwiseProps
{
    /// <summary>How a value should be read and shown.</summary>
    public enum Unit
    {
        Db,         // additive down the chain
        Cents,      // additive
        Seconds,
        Percent,
        Multiplier,
        Count,
        ObjectId,   // a u32 id, not a number -- reading it as a float gives nonsense
        Curve,
        Number,
    }

    private static readonly Dictionary<byte, (string Name, Unit Unit)> Table = new()
    {
        [0x00] = ("Volume", Unit.Db),
        [0x01] = ("LFE", Unit.Db),
        [0x02] = ("Pitch", Unit.Cents),
        [0x03] = ("Low-pass", Unit.Percent),
        [0x04] = ("High-pass", Unit.Percent),
        [0x05] = ("Bus volume", Unit.Db),
        [0x06] = ("Make-up gain", Unit.Db),
        [0x07] = ("Priority", Unit.Number),
        [0x08] = ("Priority distance offset", Unit.Number),
        [0x09] = ("Feedback volume", Unit.Db),
        [0x0A] = ("Feedback low-pass", Unit.Percent),
        [0x0B] = ("Mute ratio", Unit.Number),
        [0x0C] = ("Pan L/R", Unit.Percent),
        [0x0D] = ("Pan F/R", Unit.Percent),
        [0x0E] = ("Centre %", Unit.Percent),
        [0x0F] = ("Delay", Unit.Seconds),
        [0x10] = ("Transition time", Unit.Seconds),
        [0x11] = ("Probability", Unit.Percent),
        [0x12] = ("Dialogue mode", Unit.Number),
        [0x13] = ("Aux send 0", Unit.Db),
        [0x14] = ("Aux send 1", Unit.Db),
        [0x15] = ("Aux send 2", Unit.Db),
        [0x16] = ("Aux send 3", Unit.Db),
        [0x17] = ("Game aux send", Unit.Db),
        [0x18] = ("Output bus volume", Unit.Db),
        [0x19] = ("Output bus high-pass", Unit.Percent),
        [0x1A] = ("Output bus low-pass", Unit.Percent),
        [0x1B] = ("HDR threshold", Unit.Db),
        [0x1C] = ("HDR ratio", Unit.Number),
        [0x1D] = ("HDR release", Unit.Seconds),
        [0x1E] = ("HDR game param", Unit.ObjectId),
        [0x1F] = ("HDR game param min", Unit.Number),
        [0x20] = ("HDR game param max", Unit.Number),
        [0x21] = ("HDR active range", Unit.Db),
        [0x22] = ("Loop start", Unit.Seconds),
        [0x23] = ("Loop end", Unit.Seconds),
        [0x24] = ("Trim in", Unit.Seconds),
        [0x25] = ("Trim out", Unit.Seconds),
        [0x26] = ("Fade in", Unit.Seconds),
        [0x27] = ("Fade out", Unit.Seconds),
        [0x28] = ("Fade-in curve", Unit.Curve),
        [0x29] = ("Fade-out curve", Unit.Curve),
        [0x2A] = ("Loop crossfade", Unit.Seconds),
        [0x2B] = ("Crossfade up curve", Unit.Curve),
        [0x2C] = ("Crossfade down curve", Unit.Curve),
        [0x2D] = ("MIDI root note", Unit.Number),
        [0x2E] = ("MIDI play on note", Unit.Number),
        [0x2F] = ("MIDI transposition", Unit.Number),
        [0x30] = ("MIDI velocity offset", Unit.Number),
        [0x31] = ("MIDI key range min", Unit.Number),
        [0x32] = ("MIDI key range max", Unit.Number),
        [0x33] = ("MIDI velocity range min", Unit.Number),
        [0x34] = ("MIDI velocity range max", Unit.Number),
        [0x35] = ("MIDI channel mask", Unit.Number),
        [0x36] = ("Playback speed", Unit.Multiplier),
        [0x37] = ("MIDI tempo source", Unit.Number),
        [0x38] = ("MIDI target", Unit.ObjectId),
        [0x39] = ("Attached plugin", Unit.ObjectId),
        [0x3A] = ("Loop", Unit.Count),
        [0x3B] = ("Initial delay", Unit.Seconds),
        [0x3C] = ("Aux send low-pass 0", Unit.Percent),
        [0x3D] = ("Aux send low-pass 1", Unit.Percent),
        [0x3E] = ("Aux send low-pass 2", Unit.Percent),
        [0x3F] = ("Aux send low-pass 3", Unit.Percent),
        [0x40] = ("Aux send high-pass 0", Unit.Percent),
        [0x41] = ("Aux send high-pass 1", Unit.Percent),
        [0x42] = ("Aux send high-pass 2", Unit.Percent),
        [0x43] = ("Aux send high-pass 3", Unit.Percent),
        [0x44] = ("Game aux send low-pass", Unit.Percent),
        [0x45] = ("Game aux send high-pass", Unit.Percent),
        [0x46] = ("Attenuation", Unit.ObjectId),
        [0x47] = ("Positioning blend", Unit.Percent),
        [0x48] = ("Reflection bus volume", Unit.Db),
        [0x49] = ("Pan U/D", Unit.Percent),
    };

    /// <summary>The bank versions this table is right for.</summary>
    public const uint FirstVersion = 128;
    public const uint LastVersion = 145;

    public sealed record Prop(byte Id, string Name, Unit Unit, float Float, uint Raw, string Owner)
    {
        /// <summary>The value written the way the property means it.</summary>
        public string Text => Unit switch
        {
            Unit.Db => $"{Float:+0.##;-0.##;0} dB",
            Unit.Cents => $"{Float:+0.##;-0.##;0} cents",
            Unit.Seconds => $"{Float:0.###} s",
            Unit.Percent => $"{Float:0.##}%",
            Unit.Multiplier => $"{Float:0.###}x",
            Unit.Count => Float == 0 ? "forever" : $"{Float:0} time(s)",
            Unit.ObjectId => $"#{Raw}",
            Unit.Curve => $"curve {Raw}",
            _ => $"{Float:0.###}",
        };

        public override string ToString() => $"{Name} {Text}";
    }

    /// <summary>Everything acting on one sound, nearest first.</summary>
    public sealed class Applied
    {
        public List<Prop> Props { get; } = [];

        /// <summary>0 means it repeats for ever, 1 means it plays once.</summary>
        public int? LoopCount { get; set; }

        /// <summary>How many curves change this at runtime, and on which object.</summary>
        public int Rtpc { get; set; }
        public int StateGroups { get; set; }

        /// <summary>Seconds the event's own action waits before starting it.</summary>
        public float ActionDelay { get; set; }

        /// <summary>
        /// The short form for a column: the things that change what you hear, in the
        /// order someone would notice them. Volume and pitch add up down the chain the
        /// way Wwise applies them, so one number is the honest summary.
        /// </summary>
        public string Summary
        {
            get
            {
                var bits = new List<string>();

                var db = Props.Where(p => p.Unit == Unit.Db && p.Id is 0x00 or 0x06)
                              .Sum(p => p.Float);
                if (Math.Abs(db) > 0.001) bits.Add($"{db:+0.#;-0.#} dB");

                var cents = Props.Where(p => p.Unit == Unit.Cents).Sum(p => p.Float);
                if (Math.Abs(cents) > 0.001) bits.Add($"{cents:+0.#;-0.#} cents");

                var speed = Props.Where(p => p.Unit == Unit.Multiplier)
                                 .Aggregate(1f, (a, p) => a * p.Float);
                if (Math.Abs(speed - 1) > 0.001) bits.Add($"{speed:0.##}x speed");

                var delay = Props.Where(p => p.Id is 0x0F or 0x3B).Sum(p => p.Float) + ActionDelay;
                if (delay > 0.0005) bits.Add($"{delay:0.###}s late");

                if (LoopCount == 0) bits.Add("loops");
                else if (LoopCount > 1) bits.Add($"{LoopCount}x");

                if (Rtpc > 0) bits.Add("live");

                // Something is set, but nothing that changes what you hear. Saying so
                // beats an empty cell that reads as "not looked at".
                if (bits.Count == 0 && Props.Count > 0) return "—";
                return string.Join(" · ", bits);
            }
        }

        /// <summary>The long form, for a tooltip: every property and where it comes from.</summary>
        public string Detail
        {
            get
            {
                if (Props.Count == 0 && LoopCount is null or 1 && Rtpc == 0 && ActionDelay <= 0)
                    return "The .bnk sets nothing on this sound — it plays as the file is.";

                var lines = new List<string>();
                if (ActionDelay > 0)
                    lines.Add($"Event waits {ActionDelay:0.###} s before playing it");
                if (LoopCount == 0) lines.Add("Repeats for ever until something stops it");
                else if (LoopCount > 1) lines.Add($"Plays {LoopCount} times");

                foreach (var p in Props)
                    lines.Add($"{p.Name}: {p.Text}   (on the {p.Owner})");

                if (Rtpc > 0)
                    lines.Add($"{Rtpc} value(s) driven by a game parameter at runtime, " +
                              "so this changes while playing");
                if (StateGroups > 0)
                    lines.Add($"{StateGroups} state group(s) can change it");

                return string.Join(Environment.NewLine, lines);
            }
        }
    }

    /// <summary>
    /// Read what applies to this sound: its own properties, then everything it inherits
    /// by sitting inside a container. Wwise adds these up on the way down, so the chain
    /// is the answer and the sound alone is not.
    /// </summary>
    public static Applied Read(object soundNode, IReadOnlyDictionary<uint, AbstractHierarchy> nodes,
                               uint version, float actionDelay = 0)
    {
        var applied = new Applied { ActionDelay = actionDelay };
        if (soundNode is null) return applied;

        // A table for a version it was not written for would label the values wrongly,
        // which is worse than saying nothing: the numbers would look authoritative.
        var known = version is >= FirstVersion and <= LastVersion;

        var node = soundNode;
        var owner = "sound";
        var seen = new HashSet<uint>();

        while (node is not null)
        {
            if (node is HierarchyRandomSequenceContainer rnd)
                applied.LoopCount ??= rnd.LoopCount;

            var bp = Base(node);
            if (bp is null) break;

            foreach (var p in Bundle(bp, owner, known)) applied.Props.Add(p);
            applied.Rtpc += Count(bp, "RtpcList");
            applied.StateGroups += Count(bp, "StateGroups");

            var parentId = Field(bp, "DirectParentId") as uint? ?? 0;
            if (parentId == 0 || !seen.Add(parentId) || !nodes.TryGetValue(parentId, out var parent))
                break;
            node = parent;
            owner = parent switch
            {
                HierarchyRandomSequenceContainer => "container",
                HierarchySwitchContainer => "switch container",
                HierarchyActorMixer => "actor-mixer",
                _ => parent.GetType().Name.Replace("Hierarchy", "").ToLowerInvariant(),
            };
        }

        // A sound's own Loop property beats the container's count when it has one.
        var ownLoop = applied.Props.FirstOrDefault(p => p.Id == 0x3A);
        if (ownLoop is not null) applied.LoopCount = (int)ownLoop.Float;

        return applied;
    }

    private static IEnumerable<Prop> Bundle(object baseParams, string owner, bool known)
    {
        if (Field(baseParams, "PropBundle") is not AkPropBundle bundle || bundle.Props is null)
            yield break;

        foreach (var prop in bundle.Props)
        {
            var (name, unit) = known && Table.TryGetValue(prop.Id, out var t)
                ? t
                : ($"Property {prop.Id}", Unit.Number);

            // An object id shares its four bytes with a float. Read the wrong view and
            // an attenuation reference comes back as a number near zero.
            yield return new Prop(prop.Id, name, unit, prop.Value.f32, prop.Value.u32, owner);
        }
    }

    private static object Base(object node) => Field(node, "BaseParams");

    private static int Count(object o, string name) =>
        Field(o, name) is System.Collections.IEnumerable e ? e.Cast<object>().Count() : 0;

    // Looked up once per type and name rather than on every sound: this runs for each
    // object in each chain, several times over, for every row in the list.
    private static readonly Dictionary<(Type, string), Func<object, object>> Getters = [];

    private static object Field(object o, string name)
    {
        if (o is null) return null;
        var key = (o.GetType(), name);

        Func<object, object> get;
        lock (Getters)
        {
            if (!Getters.TryGetValue(key, out get))
            {
                var f = key.Item1.GetField(name);
                var p = f is null ? key.Item1.GetProperty(name) : null;
                get = f is not null ? f.GetValue : p is not null ? p.GetValue : _ => null;
                Getters[key] = get;
            }
        }
        try { return get(o); } catch { return null; }
    }
}
