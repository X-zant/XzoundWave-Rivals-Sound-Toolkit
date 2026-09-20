using System.IO;

namespace MRAudioKit;

/// <summary>
/// Opening a mod bank BEFORE the game is loaded must not poison the name lookup for
/// the rest of the session.
///
/// The name index is cached in a static, keyed on the session object and the bank
/// name. The window mounts one GameSession in place rather than replacing it, so a
/// lookup made before the game was loaded cached an empty index against that same
/// object -- and every later lookup for the same bank matched the key and got the
/// empty result back. The symptom is a bank that reports "0 matched to shipped
/// events" for ever, while the changed/unchanged diff beside it works fine, because
/// the diff does not go through this cache.
/// </summary>
public static class OpenOrderSelfTest
{
    /// <summary>XzoundWave.exe --openorder &lt;bnk&gt;</summary>
    public static int Run(string path)
    {
        void W(string s) => Console.WriteLine(s);
        var fails = 0;
        void Check(string what, bool ok, string detail = null)
        {
            W($"  {(ok ? "ok  " : "FAIL")}  {what}" + (detail is null ? "" : $"   {detail}"));
            if (!ok) fails++;
        }

        var settings = Settings.Load();
        settings.AutoDetect();

        ModBank.Opened opened;
        try { opened = ModBank.Open(path, settings); }
        catch (Exception ex) { W("OPEN FAILED: " + ex.Message); return 1; }
        var bank = opened.Banks.FirstOrDefault();
        if (bank is null) { W("no bank in " + path); return 1; }
        W($"bank: {bank.Name}");

        // The order the window actually does it in when someone forgets to load first.
        W("");
        W("opened before the game was loaded");
        var session = new GameSession();
        var before = ModBank.ToSounds(session, bank);
        var namedBefore = before.Rows.Count(r => r.EventName is not null);
        W($"  {before.Rows.Count} media, {namedBefore} named  (nothing to name them from yet)");

        W("");
        W("same session, now mounted, opened again");
        session.Mount(settings, _ => { });
        var after = ModBank.ToSounds(session, bank);
        var namedAfter = after.Rows.Count(r => r.EventName is not null);
        W($"  {after.Rows.Count} media, {namedAfter} named");

        Check("names resolve once the game is loaded", namedAfter > 0,
              namedAfter == 0 ? "still 0 — the empty index was cached against this session"
                              : $"{namedAfter} of {after.Rows.Count}");

        // A freshly mounted session is the control: if this names them and the one
        // above does not, the difference is the cache and nothing else.
        var fresh = new GameSession();
        fresh.Mount(settings, _ => { });
        var control = ModBank.ToSounds(fresh, bank).Rows.Count(r => r.EventName is not null);
        W($"  control, a session mounted before its first lookup: {control} named");
        Check("the two agree", namedAfter == control, $"{namedAfter} vs {control}");

        W("");
        W(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }
}
