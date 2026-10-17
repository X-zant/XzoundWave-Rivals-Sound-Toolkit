using Avalonia.Media;

namespace MRAudioKit.UI;

/// <summary>
/// A row of the tree, as data.
///
/// The WPF version had no model at all: it built TreeViewItem objects, painted their
/// Background by hand, kept a HashSet of them as application state, and encoded the
/// changed count by appending it to the header string. Which meant the tree could
/// disagree with the thing it was describing, and often did.
///
/// Here a node knows what it is and what a mod has done to it, and the template draws
/// it. Marking is option B from the mockup: the same fill the grid uses for a changed
/// row, so the tree and the list say "a mod touched this" in one language.
/// </summary>
public sealed class Node
{
    /// <summary>
    /// NewLines: the project's new voice lines, one row each, staged like any sound.
    /// AddedSounds: the sounds it adds beside the game's events, the same way.
    /// </summary>
    public enum Kind { Character, Skin, Bank, Group, NewLines, AddedSounds }

    public Kind What { get; init; }
    public string Label { get; init; } = "";

    /// <summary>What this node points at. Null on a grouping row.</summary>
    public string CharId { get; init; }
    public SkinEntry Skin { get; init; }

    /// <summary>The .bnk file name, or null when the node is a character or skin.</summary>
    public string Bank { get; init; }

    public List<Node> Children { get; init; } = [];

    /// <summary>Open in the tree. Set by the render harness so a shot shows depth.</summary>
    public bool Expanded { get; set; }

    /// <summary>Sounds a loaded mod changed at or below this node.</summary>
    public int Changed { get; set; }

    public bool IsModded => Changed > 0;

    /// <summary>The count, shown only when there is one.</summary>
    public string CountText => Changed > 0 ? Changed.ToString("N0") : "";

    /// <summary>
    /// Option B: the grid's own tints. A .bnk a mod supplies is the stronger of the
    /// two, the way a NEW row is stronger than a MODDED one in the list.
    /// </summary>
    public IBrush Fill => !IsModded ? Brushes.Transparent
        : What == Kind.Bank ? Palette.RowNew : Palette.RowModded;

    public IBrush Ink => IsModded ? Palette.TagPurple : Palette.Fg;

    public override string ToString() => Label;
}

/// <summary>
/// The app's colours, reachable from code as well as from XAML. The same values as
/// App.axaml; kept here so a template binding and a code path cannot drift.
/// </summary>
public static class Palette
{
    public static readonly IBrush Fg        = New("#DFE1E5");
    public static readonly IBrush Muted     = New("#9BA1A9");
    public static readonly IBrush Accent    = New("#6BA5F8");
    public static readonly IBrush Line      = New("#3C3F41");
    public static readonly IBrush TagPurple = New("#B79BE8");
    public static readonly IBrush TagGreen  = New("#6FD68F");
    public static readonly IBrush TagRed    = New("#E88C97");
    public static readonly IBrush RowModded = New("#3B2A5C");
    public static readonly IBrush RowNew    = New("#4A3570");
    public static readonly IBrush RowStaged = New("#204A2F");
    public static readonly IBrush RowMuted  = New("#5A2A33");

    private static IBrush New(string hex) => new SolidColorBrush(Color.Parse(hex));
}
