namespace MRAudioKit;

/// <summary>
/// Warnings someone can switch off, and what each one means.
///
/// The dialog belongs to whichever window is showing it; this is the half that does
/// not: the keys, the wording, and whether a key has been silenced. Keeping them here
/// means the settings page that offers to bring a warning back and the code that raises
/// it are reading the same table, rather than two copies that drift until one of them
/// describes a warning the other no longer shows.
/// </summary>
public static class Notices
{
    /// <summary>Keys, in one place so a typo cannot silently create a second warning.</summary>
    public const string ModReplaced = "mod-replaced-an-already-loaded-bnk";
    public const string BuildOnMod = "build-uses-a-loaded-mod-as-its-base";

    /// <summary>
    /// What a key means, for the list in settings. A key is written to be stable, not
    /// to be read, and "mod-replaced-an-already-loaded-bnk" is not an explanation.
    /// </summary>
    public static string Describe(string key) => key switch
    {
        ModReplaced => "Loading a .bnk another mod had already claimed replaces it.",
        BuildOnMod => "Building while a mod is loaded uses that mod as the base.",
        _ => key,
    };

    public static bool IsSuppressed(Settings settings, string key) =>
        settings?.SuppressedWarnings?.Contains(key, StringComparer.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Remember that this one was silenced. Only ever called when the author ACCEPTED
    /// the dialog: silencing a warning someone cancelled out of would suppress it for a
    /// decision they had refused.
    /// </summary>
    public static void Suppress(Settings settings, string key)
    {
        if (settings is null) return;
        settings.SuppressedWarnings ??= [];
        if (!settings.SuppressedWarnings.Contains(key, StringComparer.OrdinalIgnoreCase))
            settings.SuppressedWarnings.Add(key);
        settings.Save();
    }

    public static void Restore(Settings settings, string key)
    {
        settings?.SuppressedWarnings?.RemoveAll(k =>
            k.Equals(key, StringComparison.OrdinalIgnoreCase));
        settings?.Save();
    }
}
