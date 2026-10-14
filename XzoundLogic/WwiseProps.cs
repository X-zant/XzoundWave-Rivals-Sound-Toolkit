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

    /// <param name="Owner">
    /// What KIND of level this sits on: the sound itself, the container holding it, an
    /// actor-mixer above that. Not unique within a chain, because an actor-mixer hierarchy
    /// nests and these banks nest it deep enough that nearly every row inherits from
    /// several mixers at once.
    /// </param>
    /// <param name="OwnerId">
    /// The id of the object the property is actually on, which is the only name the bank
    /// ships for it: Wwise keeps authored names for events and strips them from the
    /// hierarchy. It is what lets a caller say WHICH mixer rather than guess.
    /// </param>
    /// <param name="OwnerRank">
    /// This level's place among the levels of its own kind, counted from the sound
    /// upwards, so the first actor-mixer above the sound is 1. It counts LEVELS and not
    /// contributions, which is why a group of values can skip a number: that is a reader
    /// seeing that the third mixer sets nothing of that property. A Prop built without a
    /// chain behind it is the only level there is, hence the default.
    /// </param>
    public sealed record Prop(byte Id, string Name, Unit Unit, float Float, uint Raw, string Owner,
                              uint OwnerId = 0, int OwnerRank = 1)
    {
        /// <summary>
        /// A game parameter drives this while the sound plays, so the value stored in
        /// the bank is a starting point rather than what you hear. Setting a level to
        /// its minimum and letting a curve raise it is ordinary Wwise practice, and
        /// reading that -96 dB as "silent" gets the sound exactly backwards.
        /// </summary>
        public bool Driven { get; set; }

        /// <summary>
        /// The chain holds more than one level wearing this <see cref="Owner"/> label, so
        /// the kind alone does not say which of them this came from. Set by
        /// <see cref="Read"/> once the walk is finished, because one property cannot see
        /// its siblings: it is the SECOND actor-mixer that makes the first one ambiguous.
        /// </summary>
        public bool OwnerRepeated { get; set; }

        /// <summary>
        /// The level, told apart from the others like it. "The actor-mixer" is a definite
        /// article for one of several objects on nearly every row here, which is how five
        /// separate mixers each sending +10 dB came out as five identical lines.
        /// </summary>
        public string Level => LevelPhrase(Owner, OwnerRank, OwnerRepeated);

        public string Where => Driven ? $"on {Level}, driven by a game parameter"
                                      : $"on {Level}";

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

    /// <summary>
    /// One slot of a container, as the container itself states it.
    ///
    /// The media a slot reaches, plural, because a slot's child need not be a Sound.
    /// bnk_sfx_1034001's container 369457676 gives each of its three slots a layer
    /// container holding two media that the game plays together, and bnk_vo_1022001's
    /// container 34402701 gives two of its four slots a seven-way container of their own.
    /// A slot is one alternative whichever of those it is, which is why it stays one
    /// entry and carries a list rather than being split into several.
    /// </summary>
    /// <param name="Index">The slot's position in the container's child list.</param>
    /// <param name="Share">This slot's share of the picks, worded as the set words it.</param>
    /// <param name="Media">Every medium reachable through this slot, in the bank's order.</param>
    public sealed record Alternate(int Index, string Share, IReadOnlyList<uint> Media)
    {
        /// <summary>The one medium this slot reaches, or 0 where it reaches several.</summary>
        public uint MediaId => Media.Count == 1 ? Media[0] : 0;
    }

    /// <summary>
    /// One of several alternatives the container picks between.
    ///
    /// The thing a modder needs before replacing anything: a line that is one of seven
    /// random variations is heard about one time in seven, so swapping it alone looks
    /// like the mod barely works. Sequence containers are worse to guess at, because
    /// the order is fixed and position matters.
    /// </summary>
    /// <param name="Chance">Share of the picks this one gets, 0-1.</param>
    /// <param name="FromWeights">
    /// The share is the container's own playlist weights, not an assumption. Every
    /// random container in this game's character banks carries a playlist, so the even
    /// split below is a fallback for banks that do not -- and the two must not look
    /// alike on screen, or a guess reads as a measurement.
    /// </param>
    /// <param name="Members">
    /// Every slot of the container, read off the container and never gathered from the
    /// rows that happen to carry it.
    ///
    /// The bank builds the same few media into container after container, through Sound
    /// objects of its own each time: bnk_sfx_1022001 holds 498 random containers, 101 of
    /// their media multisets are held by more than one of them, and 18 of them hold at
    /// least one of the four media 673786398, 53978063, 634398524 and 160354566 between
    /// them. The index keeps one row per bank, event and medium, so those rows
    /// scatter across whichever container each medium was met in first: container
    /// 183834482 holds all three of 673786398, 53978063 and 634398524, and only the first
    /// of them comes back with a row naming it, the other two naming container 479715779.
    /// Gathering rows on the group id therefore finds a FRACTION of the set and cannot
    /// tell that it has: 47 sets across skins 1030001, 1029001, 1034001, 1022001 and
    /// 1048001 are short that way, 18 of them in bnk_sfx_1022001 alone. A list of
    /// alternatives that silently drops members is worse than no list, because a modder
    /// replacing what it shows leaves the rest playing the old sound.
    /// </param>
    public sealed record Variation(int Index, int Count, string Mode, double Chance,
                                   bool FromWeights, uint GroupId,
                                   IReadOnlyList<Alternate> Members)
    {
        /// <summary>True when the bank gives this one a different share from its siblings.</summary>
        public bool Uneven => FromWeights && Math.Abs(Chance - 1.0 / Count) > 0.005;

        public string Share => Mode == "sequence" ? ""
            : FromWeights ? $"{Chance:P0}"
            : $"~{Chance:P0} (even split assumed)";

        public string Text
        {
            get
            {
                if (Count <= 1) return "";
                var share = Share.Length == 0 ? "" : $", heard {Share} of the time";
                return $"{Index + 1} of {Count} {Mode}{share}";
            }
        }
    }

    /// <summary>Everything acting on one sound, nearest first.</summary>
    public sealed class Applied
    {
        /// <summary>Which alternative this is, when the container picks between several.</summary>
        public Variation Alternative { get; set; }

        /// <summary>
        /// The nearest actor-mixer above this sound, and how many things it holds.
        ///
        /// A mixer is how a designer keeps a set of related sounds together -- one
        /// ability's four shots, a footstep set -- so two sounds under the same one
        /// belong together whatever their events happen to be called. It is a MIXING
        /// group and not a playback order: Wwise attaches no sequence to it, and the
        /// child list is sorted by id rather than by anything an author chose.
        /// </summary>
        public uint FamilyId { get; set; }
        public int FamilySize { get; set; }

        /// <summary>The rate the bank says the source was encoded at.</summary>
        public uint SampleRate { get; set; }

        /// <summary>
        /// The length the .bnk states for this clip, when the bank times the clip itself.
        ///
        /// A music track's playlist records each clip's own duration and places the clip by
        /// that figure, which makes it the one thing here a replacement can contradict: the
        /// audio changes and the stated number does not, because a rebuild copies the
        /// hierarchy through untouched. Null on everything the bank does not time, which is
        /// every sound outside a music track.
        /// </summary>
        public double? ClipSeconds { get; set; }

        /// <summary>The stated length of the music segment the clip sits in, when it states one.</summary>
        public double? SegmentSeconds { get; set; }

        /// <summary>The playlist trims this clip, against the audio the game shipped.</summary>
        public bool ClipTrimmed { get; set; }

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
        /// A curve aims at one of the two properties <see cref="StaticDb"/> adds up, so the
        /// level is not the fixed thing that figure makes it look.
        ///
        /// Asked separately from <see cref="DrivenProps"/> because that can only mark a
        /// property the bank STORES: an object that leaves its volume at the default and puts
        /// a curve on it marks nothing, and the row then reads as having no game parameter on
        /// its level at all. Six silenced rows of skin 1029001 and one of 1034001 are exactly
        /// that shape, and they were being told nothing raises them while a mixer above them
        /// carries a Volume curve. Kept to the ids StaticDb sums rather than to every dB
        /// property, because an aux send changes where the sound goes and not how loud it is.
        /// </summary>
        public bool LevelDriven { get; set; }

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

                // Only what is fixed. A level a game parameter drives is not a number
                // anyone can add up here: the bank stores where that level starts and
                // the curve decides where it ends, so a figure counting it in would
                // describe neither of the two.
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

                // The length the bank itself states for the clip. It belongs beside the
                // volume and the delay for the same reason they do: it is something the
                // .bnk applies that the audio file cannot change, and this column is what
                // someone reads before deciding what to put in the slot.
                if (ClipSeconds is > 0) bits.Add($"timed {ClipSeconds:0.##}s");

                if (DrivenProps.Any(p => p.Unit == Unit.Db)) bits.Add("level driven");
                else if (Rtpc > 0) bits.Add("live");


                // Something is set, but nothing that changes what you hear. Saying so in
                // words beats an empty cell that reads as "not looked at", and beats the
                // bare dash that was here: a reader cannot tell a dash meaning "nothing
                // audible" from one meaning "no answer".
                if (bits.Count == 0 && Props.Count > 0) return "no audible change";
                return string.Join(" · ", bits);
            }
        }

        /// <summary>The long form, for a tooltip: every property and where it comes from.</summary>
        public string Detail
        {
            get
            {
                if (Props.Count == 0 && LoopCount is null or 1 && Rtpc == 0 && ActionDelay <= 0
                    && ClipSeconds is null && Alternative is null or { Count: <= 1 })
                    return "The .bnk sets nothing on this sound, so it plays as the file is.";

                var lines = new List<string>();
                if (Alternative is { Count: > 1 })
                {
                    lines.Add($"One of several: {Alternative.Text}.");
                    if (Alternative.Uneven)
                        lines.Add("The bank gives this one a different weight from its " +
                                  "siblings, so the alternatives are not equally likely.");
                    lines.Add("Replacing only this one leaves the others as they are.");
                    lines.Add("");
                }
                if (ActionDelay > 0)
                    lines.Add($"Event waits {ActionDelay:0.###} s before playing it");
                if (LoopCount == 0) lines.Add("Repeats for ever until something stops it");
                else if (LoopCount > 1) lines.Add($"Plays {LoopCount} times");

                // The level is no longer parenthesised, because Where now carries an
                // ordinal of its own and brackets around it read as a nested aside.
                foreach (var p in Props)
                    lines.Add($"{p.Name}: {p.Text}   {p.Where}");

                if (Rtpc > 0)
                {
                    lines.Add("");
                    lines.Add($"{Rtpc} value(s) driven by a game parameter at runtime" +
                              (Live.Count > 0 ? ": " + string.Join(", ", Live.Distinct()) : "") +
                              ".");
                }

                if (ClipSeconds is > 0)
                {
                    lines.Add("");
                    lines.Add($"THE .BNK TIMES THIS CLIP AT {ClipSeconds:0.###} s" +
                              (ClipTrimmed ? ", and trims its in and out points" : "") + ".");
                    lines.Add("A rebuild copies the hierarchy through byte for byte, which is what " +
                              "keeps the rest of the bank intact, so audio of a different length " +
                              "leaves those figures describing a clip that is no longer there.");
                    if (SegmentSeconds is { } seg)
                        lines.Add($"The segment it plays in is {seg:0.###} s long.");
                }

                if (Silenced)
                {
                    lines.Add("");
                    lines.Add($"THIS SOUND IS SILENT. The .bnk turns it down by {-StaticDb:0.#} dB " +
                              "and nothing raises it again: no game parameter and no state.");
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

        if (soundNode is HierarchySoundSfxVoice sfx)
            applied.SampleRate = sfx.Source.SampleRate;

        var node = soundNode;
        // What the nearest level is called, for the "on the ..." each property carries.
        // A music clip's own properties sit on its track, and calling that "the sound"
        // would name an object the bank does not have.
        var owner = soundNode is HierarchyMusicTrack ? "music track" : "sound";
        var seen = new HashSet<uint>();
        var child = (soundNode as AbstractHierarchy)?.Id ?? 0;

        // How many levels of each kind the walk read, which is what decides whether a
        // kind names one object or several. It cannot be decided while walking, because
        // the level that makes an earlier one ambiguous is one this loop has not reached.
        var levels = new Dictionary<string, int>();

        // The runtime curves, held until the levels are counted so each can name the
        // object it sits on. Two mixers driving the same parameter used to produce two
        // identical strings, which a Distinct() then folded into one against a count of
        // two, so the sentence said "2 values driven" and then named one.
        var live = new List<(string Name, string Owner, int Rank)>();

        while (node is not null)
        {
            if (node is HierarchyRandomSequenceContainer rnd)
                applied.LoopCount ??= rnd.LoopCount;

            var bp = Base(node);
            if (bp is null) break;

            // Counted after the block check, so the tally is levels the walk actually
            // read. A level whose block is missing ends the walk without contributing
            // anything, and counting it would mark its kind repeated and put an ordinal
            // on a level that is the only one of its kind on screen.
            var rank = levels[owner] = levels.GetValueOrDefault(owner) + 1;
            var ownerId = (node as AbstractHierarchy)?.Id ?? 0;

            var here = Bundle(bp, owner, ownerId, rank, known).ToList();

            // A curve belongs to the object it sits on, so it can only mark the
            // properties of THAT object. A volume driven on the container says nothing
            // about a volume set on the sound inside it.
            foreach (var (name, propId) in Curves(bp))
            {
                applied.Rtpc++;
                live.Add((name, owner, rank));
                if (propId is 0x00 or 0x06) applied.LevelDriven = true;
                foreach (var p in here.Where(p => p.Id == propId)) p.Driven = true;
            }

            applied.Props.AddRange(here);
            applied.StateGroups += Count(bp, "StateGroups");

            var parentId = Field(bp, "DirectParentId") as uint? ?? 0;
            if (parentId == 0 || !seen.Add(parentId) || !nodes.TryGetValue(parentId, out var parent))
                break;

            // Only the container the sound sits in directly decides whether it is one
            // alternative among several. A grandparent picking between containers is a
            // different question and would read as a false reassurance here.
            applied.Alternative ??= AlternativeIn(parent, child, nodes);
            if (applied.FamilyId == 0 && parent is HierarchyActorMixer mixer)
            {
                applied.FamilyId = mixer.Id;
                applied.FamilySize = mixer.ChildIds?.Length ?? 0;
            }
            child = parent.Id;
            node = parent;
            owner = parent switch
            {
                HierarchyRandomSequenceContainer => "container",
                HierarchySwitchContainer => "switch container",
                HierarchyActorMixer => "actor-mixer",
                HierarchyMusicSegment => "music segment",
                HierarchyMusicRandomSequenceContainer => "music playlist",
                HierarchyMusicSwitchContainer => "music switch",
                // The last container kind the parser has, and the only one the fallback
                // below was actually reaching: it lower-cases the type name whole, so a
                // reader was told a property sits "on the layercontainer". 1,420 property
                // lines across skins 1030001, 1029001 and 1031001 read that way.
                HierarchyLayerContainer => "layer container",
                _ => parent.GetType().Name.Replace("Hierarchy", "").ToLowerInvariant(),
            };
        }

        // A kind is ambiguous exactly when the walk read more than one level wearing it,
        // and every contribution from such a kind has to say which level it came from:
        // naming only the one that repeats would leave "the actor-mixer" beside "the 2nd
        // actor-mixer above it", where the definite article is the part that misleads.
        bool Several(string kind) => levels.GetValueOrDefault(kind) > 1;
        foreach (var p in applied.Props) p.OwnerRepeated = Several(p.Owner);
        foreach (var (name, curveOwner, curveRank) in live)
            applied.Live.Add(
                $"{name} (on {LevelPhrase(curveOwner, curveRank, Several(curveOwner))})");

        // A sound's own Loop property beats the container's count when it has one.
        var ownLoop = applied.Props.FirstOrDefault(p => p.Id == 0x3A);
        if (ownLoop is not null) applied.LoopCount = (int)ownLoop.Float;

        return applied;
    }

    /// <summary>
    /// One level of the chain, named so a reader can tell it from the others of its kind.
    ///
    /// The bank ships no authored name for a mixer, so position is all there is to say in
    /// words: an ordinal counted outwards from the sound, which is the order the
    /// properties themselves are listed in. The object's own id travels beside it on the
    /// Prop, for anything that has to name the mixer exactly rather than place it.
    /// </summary>
    private static string LevelPhrase(string owner, int rank, bool repeated) =>
        repeated ? $"the {Ordinal(rank)} {owner} above it" : $"the {owner}";

    /// <summary>
    /// 1st, 2nd, 3rd. The teens take "th" whatever their last digit is, and a chain deep
    /// enough to reach eleven levels of one kind is not worth a wrong suffix.
    /// </summary>
    private static string Ordinal(int n) => n + ((n % 100) is >= 11 and <= 13 ? "th"
        : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    private static IEnumerable<Prop> Bundle(object baseParams, string owner, uint ownerId,
                                            int rank, bool known)
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
            yield return new Prop(prop.Id, name, unit, prop.Value.f32, prop.Value.u32, owner,
                                  ownerId, rank);
        }
    }

    /// <summary>
    /// Where this child sits among its container's alternatives, how often it is picked,
    /// and what the whole set is. Weights are the container's own; equal weights make an
    /// even split, so a container with no playlist at all is treated as even.
    ///
    /// The set is built here, from the container, because here is the only place that
    /// holds it. By the time a caller has rows it has one row per bank, event and medium
    /// and no way back to the container's child list, which is how the set came to be
    /// reconstructed by gathering rows on the group id and came out short.
    /// </summary>
    private static Variation AlternativeIn(AbstractHierarchy parent, uint childId,
                                           IReadOnlyDictionary<uint, AbstractHierarchy> nodes)
    {
        if (parent is not HierarchyRandomSequenceContainer rnd || rnd.ChildIds is null) return null;

        var index = Array.IndexOf(rnd.ChildIds, childId);
        if (index < 0 || rnd.ChildIds.Length <= 1) return null;

        var mode = rnd.Mode.ToString().Equals("Sequence", StringComparison.OrdinalIgnoreCase)
            ? "sequence" : "random";

        var total = rnd.Playlist is { Length: > 0 }
            ? rnd.Playlist.Sum(i => (double)Math.Max(i.Weight, 0)) : 0;
        var fromWeights = total > 0;

        // The slot's own share and not the sound's, so the figure beside a member of the
        // set means the same thing as the figure in the sentence above it.
        double ChanceOf(uint slotChild) => fromWeights
            ? rnd.Playlist.Where(i => i.PlayId == slotChild)
                          .Sum(i => (double)Math.Max(i.Weight, 0)) / total
            : 1.0 / rnd.ChildIds.Length;

        // Built before the record, because Share is worded off Mode and FromWeights and a
        // member's share has to read exactly as the current one's does.
        string ShareOf(uint slotChild) =>
            new Variation(0, rnd.ChildIds.Length, mode, ChanceOf(slotChild), fromWeights,
                          rnd.Id, []).Share;

        var members = rnd.ChildIds
            .Select((slotChild, at) => new Alternate(at, ShareOf(slotChild),
                SoundsUnder(slotChild, nodes)
                    .Select(s => s.Source.SourceId != 0 ? s.Source.SourceId : s.Source.FileId)
                    .Where(m => m != 0).Distinct().ToArray()))
            .ToArray();

        return new Variation(index, rnd.ChildIds.Length, mode, ChanceOf(childId), fromWeights,
                             rnd.Id, members);
    }

    /// <summary>
    /// Every Sound at or below one object, by the same walk the index builds its rows
    /// with, so a container's stated membership and the rows that exist cannot be two
    /// different readings of the same tree.
    ///
    /// A container's child is usually a Sound and sometimes another container, and the
    /// kinds that can hold children are the four below: bnk_vo_1022001's container
    /// 34402701 reaches seven media through one of its slots and bnk_sfx_1034001's
    /// container 369457676 reaches two through each of its three.
    /// </summary>
    public static IEnumerable<HierarchySoundSfxVoice> SoundsUnder(
        uint id, IReadOnlyDictionary<uint, AbstractHierarchy> nodes, HashSet<uint> seen = null)
    {
        seen ??= [];
        if (!seen.Add(id) || !nodes.TryGetValue(id, out var n)) yield break;
        var children = n switch
        {
            HierarchySoundSfxVoice => null,
            HierarchyRandomSequenceContainer r => r.ChildIds,
            HierarchySwitchContainer sw => sw.ChildIds,
            HierarchyLayerContainer l => l.ChildIds,
            HierarchyActorMixer m => m.ChildIds,
            _ => null,
        };
        if (n is HierarchySoundSfxVoice s) yield return s;
        foreach (var c in children ?? [])
            foreach (var x in SoundsUnder(c, nodes, seen)) yield return x;
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

    /// <summary>
    /// The inherited block, whatever the object calls it.
    ///
    /// A Sound, a MusicTrack and the generic containers name it BaseParams. The music
    /// containers, MusicSegment and MusicRanSeq and MusicSwitch, all derive from
    /// BaseHierarchyMusic, which names the SAME BaseHierarchy block ContainerHierarchy.
    /// Looking for one name only stopped the chain at the first music container, which is
    /// where most of music_formal's properties live: they sit on its music playlists and
    /// segments, and apply to the tracks below them. Without this the column read as "the
    /// .bnk sets nothing on this sound" for the whole music bank. Nothing else in the
    /// parser has a ContainerHierarchy field, so trying both names cannot reach any node
    /// the old walk could not.
    /// </summary>
    private static object Base(object node) =>
        Field(node, "BaseParams") ?? Field(node, "ContainerHierarchy");

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
