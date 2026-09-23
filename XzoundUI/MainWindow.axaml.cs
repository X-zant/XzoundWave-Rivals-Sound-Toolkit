using Avalonia.Controls;

namespace MRAudioKit.UI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Wire();
    }

    // InitializeComponent is generated from the .axaml, and writing one by hand
    // shadows it: the XAML loads, every x:Name field stays null, and the first line
    // that touches a control throws. Which is exactly what happened.

    /// <summary>
    /// The rail is the only navigation, so this is the whole of it: pick a mode, the
    /// panel on the right changes, nothing else moves.
    /// </summary>
    private void Wire()
    {
        ModeRail.SelectionChanged += (_, _) => ShowMode();
        ShowMode();
    }

    private void ShowMode()
    {
        var mode = (ModeRail.SelectedIndex) switch
        {
            1 => ("Build", "Drop audio onto the list, or stage files here. A named file " +
                           "says which sound it replaces, so no skin has to be picked."),
            2 => ("Test", "Numbered clips, so a sound in game tells you which entry fired. " +
                          "Mute the ones you have identified and build again."),
            3 => ("Mods", "Open someone else's .bnk files and see them diffed against the " +
                          "game. Several at once; a build uses them as its base."),
            _ => ("Browse", "Pick a character, skin or .bnk on the left. Ctrl+click several " +
                            ".bnk files to look at them together."),
        };
        ModeTitle.Text = mode.Item1;
        ModeHint.Text = mode.Item2;
    }
}
