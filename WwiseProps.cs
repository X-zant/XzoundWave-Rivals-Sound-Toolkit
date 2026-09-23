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

    /// <summary>
    /// What a runtime curve can point at. A DIFFERENT enumeration from the properties
    /// above -- they agree as far as BusVolume and then diverge, so id 6 is an initial
    /// delay here and a make-up gain there. Matching one against the other by number
    /// would mislabel exactly the values this is meant to explain.
    ///
    /// Table: bnnm/wwiser, AkRTPC_ParameterID_135 (versions above 134).
    /// </summary>
    private static readonly Dictionary<ushort, (string Name, byte PropId)> RtpcTargets = new()
    {
        // PropId pairs a curve back to the property it drives; 0xFF where the property
        // table above has nothing that means the same thing.
        [0x00] = ("Volume", 0x00),
        [0x01] = ("LFE", 0x01),
        [0x02] = ("Pitch", 0x02),
        [0x03] = ("Low-pass", 0x03),
        [0x04] = ("High-pass", 0x04),
        [0x05] = ("Bus volume", 0x05),
        [0x06] = ("Initial delay", 0x3B),
        [0x07] = ("Make-up gain", 0x06),
        [0x08] = ("Feedback volume", 0x09),
        [0x09] = ("Feedback low-pass", 0x0A),
        [0x0A] = ("Feedback pitch", 0xFF),
        [0x0B] = ("MIDI transposition", 0x2F),
        [0x0C] = ("MIDI velocity offset", 0x30),
        [0x0D] = ("Playback speed", 0x36),
        [0x0E] = ("Mute ratio", 0x0B),
        [0x0F] = ("Transition value", 0xFF),
        [0x10] = ("Max instances", 0xFF),
        [0x11] = ("Priority", 0x07),
        [0x12] = ("Pan L/R", 0x0C),
        [0x13] = ("Pan F/R", 0x0D),
        [0x14] = ("Pan X (3D)", 0xFF),
        [0x15] = ("Pan Y (3D)", 0xFF),
        [0x16] = ("Pan Z (3D)", 0xFF),
        [0x17] = ("Positioning blend", 0x47),
        [0x18] = ("Centre %", 0x0E),
        [0x19] = ("Cone attenuation on/off", 0xFF),
        [0x1A] = ("Cone attenuation", 0xFF),
        [0x1B] = ("Cone low-pass", 0xFF),
        [0x1C] = ("Cone high-pass", 0xFF),
        [0x1D] = ("Bypass effect 0", 0xFF),
        [0x1E] = ("Bypass effect 1", 0xFF),
        [0x1F] = ("Bypass effect 2", 0xFF),
        [0x20] = ("Bypass effect 3", 0xFF),
        [0x21] = ("Bypass all effects", 0xFF),
        [0x22] = ("HDR threshold", 0x1B),
        [0x23] = ("HDR release", 0x1D),
        [0x24] = ("HDR ratio", 0x1C),
        [0x25] = ("HDR active range", 0x21),
        [0x26] = ("Game aux send", 0x17),
        [0x27] = ("Aux send 0", 0x13),
        [0x28] = ("Aux send 1", 0x14),
        [0x29] = ("Aux send 2", 0x15),
        [0x2A] = ("Aux send 3", 0x16),
        [0x2B] = ("Output bus volume", 0x18),
        [0x2C] = ("Output bus high-pass", 0x19),
        [0x2D] = ("Output bus low-pass", 0x1A),
        [0x2E] = ("Attenuation on/off", 0xFF),
        [0x2F] = ("Reflections volume", 0x48),
        [0x30] = ("Aux send low-pass 0", 0x3C),
        [0x31] = ("Aux send low-pass 1", 0x3D),
        [0x32] = ("Aux send low-pass 2", 0x3E),
        [0x33] = ("Aux send low-pass 3", 0x3F),
        [0x34] = ("Aux send high-pass 0", 0x40),
        [0x35] = ("Aux send high-pass 1", 0x41),
        [0x36] = ("Aux send high-pass 2", 0x42),
        [0x37] = ("Aux send high-pass 3", 0x43),
        [0x38] = ("Game aux send low-pass", 0x44),
        [0x39] = ("Game aux send high-pass", 0x45),
        [0x3A] = ("Pan Z (2D)", 0xFF),
        [0x3B] = ("Bypass all metadata", 0xFF),
    };

    /// <summary>The bank versions this table is right for.</summary>
    public const uint FirstVersion = 128;
    public const uint LastVersion = 145;

    public sealed record Prop(byte Id, string Name, Unit Unit, float Float, uint Raw, string Owner)
    {
        /// <summary>
        /// A game parameter drives this while the sound plays, so the value stored in
        /// the bank is a starting point rather than what you hear. Setting a level to
        /// its minimum and letting a curve raise it is ordinary Wwise practice, and
        /// reading that -96 dB as "silent" gets the sound exactly backwards.
        /// </summary>
        public bool Driven { get; set; }

        public string Where => Driven ? $"on the {Owner}, driven by a game parameter"
                                      : $"on the {Owner}";

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

        /// <summary>What those curves point at, named.</summary>
        public List<string> Live { get; } = [];

        /// <summary>Properties a curve drives, so their stored value is only a start.</summary>
        public IEnumerable<Prop> DrivenProps => Props.Where(p => p.Driven);

        /// <summary>
        /// The fixed part of the level, in dB: volume and make-up gain summed the way
        /// Wwise sums them down the chain, leaving out anything a game parameter
        /// drives because that is not a number anyone can add up here.
        /// </summary>
        public float StaticDb => Props
            .Where(p => !p.Driven && p.Unit == Unit.Db && p.Id is 0x00 or 0x06)
            .Sum(p => p.Float);

        /// <summary>
        /// The bank turns this sound down so far that nothing comes out. -96 dB is
        /// Wwise's floor, and dragging a level to it is how a designer switches a layer
        /// off without deleting it from the hierarchy -- so this is usually deliberate
        /// rather than a mistake. It matters because replacing the audio on one of
        /// these changes nothing anybody will hear.
        /// </summary>
        public bool Silenced => StaticDb <= -60;
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

                // Only what is fixed. A level a curve drives is not a number anyone
                // can add up here, and adding it anyway produces the -106 dB that
                // reads as "they muted it" for a sound that is plainly audible.
                if (Silenced) bits.Add($"SILENT ({StaticDb:0.#} dB)");
                else if (Math.Abs(StaticDb) > 0.001) bits.Add($"{StaticDb:+0.#;-0.#} dB");

                var cents = Props.Where(p => !p.Driven && p.Unit == Unit.Cents).Sum(p => p.Float);
                if (Math.Abs(cents) > 0.001) bits.Add($"{cents:+0.#;-0.#} cents");

                var speed = Props.Where(p => p.Unit == Unit.Multiplier)
                                 .Aggregate(1f, (a, p) => a * p.Float);
                if (Math.Abs(speed - 1) > 0.001) bits.Add($"{speed:0.##}x speed");

                var delay = Props.Where(p => p.Id is 0x0F or 0x3B).Sum(p => p.Float) + ActionDelay;
                if (delay > 0.0005) bits.Add($"{delay:0.###}s late");

                if (LoopCount == 0) bits.Add("loops");
                else if (LoopCount > 1) bits.Add($"{LoopCount}x");

                if (DrivenProps.Any(p => p.Unit == Unit.Db)) bits.Add("level driven");
                else if (Rtpc > 0) bits.Add("live");

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
                    lines.Add($"{p.Name}: {p.Text}   ({p.Where})");

                if (Rtpc > 0)
                {
                    lines.Add("");
                    lines.Add($"{Rtpc} value(s) driven by a game parameter at runtime" +
                              (Live.Count > 0 ? ": " + string.Join(", ", Live.Distinct()) : "") +
                              ".");
                }

                if (Silenced)
                {
                    lines.Add("");
                    lines.Add($"THIS SOUND IS SILENT. The .bnk turns it down by {-StaticDb:0.#} dB " +
                              "and nothing raises it again -- no game parameter, no state.");
                    lines.Add("-96 dB is Wwise's floor, and pulling a level to it is how a sound " +
                              "is switched off without being deleted from the hierarchy, so this " +
                              "is almost certainly deliberate.");
                    lines.Add("Replacing the audio here changes nothing anybody will hear.");
                }
                else if (DrivenProps.Any(p => p.Unit == Unit.Db))
                    lines.Add("A level a game parameter drives is left out of the figure above: " +
                              "what the bank stores is where it starts, not what you hear.");
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

            var here = Bundle(bp, owner, known).ToList();

            // A curve belongs to the object it sits on, so it can only mark the
            // properties of THAT object. A volume driven on the container says nothing
            // about a volume set on the sound inside it.
            foreach (var (name, propId) in Curves(bp))
            {
                applied.Rtpc++;
                applied.Live.Add($"{name} (on the {owner})");
                foreach (var p in here.Where(p => p.Id == propId)) p.Driven = true;
            }

            applied.Props.AddRange(here);
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

    /// <summary>The runtime curves on one object, and which property each drives.</summary>
    private static IEnumerable<(string Name, byte PropId)> Curves(object baseParams)
    {
        if (Field(baseParams, "RtpcList") is not System.Collections.IEnumerable list)
            yield break;

        foreach (var rtpc in list)
        {
            var raw = Field(rtpc, "ParamId");
            if (raw is null) { yield return ("a value", 0xFF); continue; }

            var id = Convert.ToUInt16(raw);
            yield return RtpcTargets.TryGetValue(id, out var t) ? t : ($"parameter {id}", (byte)0xFF);
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
