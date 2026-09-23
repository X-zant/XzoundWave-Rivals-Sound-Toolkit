namespace MRAudioKit;

/// <summary>
/// The whole of the command-line host.
///
/// Short on purpose. Every verb already knows how to run itself and none of them needs
/// a message pump, so there is nothing for this to do but hand over the arguments —
/// which is the claim the assembly split was making, stated as a program.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        // No window to put a message in, so the diagnostics keep their default and
        // write to the console. That is what EnvCheck.Tell is for.
        if (args.Length == 0)
        {
            Verbs.PrintHelp();
            return 0;
        }

        return Verbs.Dispatch(args, out var exit)
            ? exit
            // A name that is not a verb is a typo, not a request to do nothing.
            : Unknown(args);
    }

    private static int Unknown(string[] args)
    {
        var named = args.FirstOrDefault(a => a.StartsWith("--", StringComparison.Ordinal))
                    ?? args[0];
        Console.Error.WriteLine($"not a verb: {named}");

        // The nearest few by name, because a verb mistyped is usually nearly right.
        var near = Verbs.All
            .Select(v => (v.Name, Score: Shared(v.Name, named)))
            .Where(x => x.Score >= 3)
            .OrderByDescending(x => x.Score)
            .Take(3).Select(x => x.Name).ToList();

        if (near.Count > 0) Console.Error.WriteLine("did you mean: " + string.Join(", ", near));
        Console.Error.WriteLine("--help lists them all.");
        return 2;
    }

    /// <summary>How much of a leading run two names share, which is enough to spot a typo.</summary>
    private static int Shared(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i])) i++;
        return i;
    }
}
