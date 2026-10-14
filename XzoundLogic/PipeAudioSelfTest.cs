using System.Diagnostics;
using System.IO;
using NAudio.Wave;
using Player = MRAudioKit.PipeAudioOutput.Player;

namespace MRAudioKit;

/// <summary>
/// The system-player output, checked on a machine with no sound card.
///
/// Almost none of what can go wrong here is about sound. Which player is picked when a
/// machine has two is a decision; a Play that plays on the caller's thread freezes the
/// window; a Finished raised twice steps an autoplay run on by two rows and one never
/// raised stalls it; and a Stop that raises Finished does both at once. Every one of
/// those is observable without anything being audible, and a test that needed a device
/// could not run on the machine the build is made on.
///
/// So the contract is checked against a stand-in player: a command that swallows bytes,
/// and a command that reads nothing at all so that a stop happens while a write is
/// blocked. That is the case that matters, because a killed player leaves the writing
/// side looking at a broken pipe, which is the normal path through Stop and must not
/// surface as a failure. Anything that genuinely needs a device says it was skipped.
/// </summary>
public static class PipeAudioSelfTest
{
    /// <summary>XzoundWave.exe --pipeaudio</summary>
    public static int Run()
    {
        void W(string s) => Console.WriteLine(s);
        var fails = 0;
        void Check(string what, bool ok, string detail = null)
        {
            W($"  {(ok ? "ok  " : "FAIL")}  {what}" + (detail is null ? "" : $"   {detail}"));
            if (!ok) fails++;
        }
        void Skip(string what, string why) => W($"  --    {what}   skipped: {why}");

        var path = PipeAudioOutput.SearchPath ?? "";
        var dirs = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        W($"host: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        W($"looking for: {string.Join(", ", PipeAudioOutput.Candidates.Select(c => c.ToString()))}");
        W($"in {dirs.Length} place(s) named by PATH: {string.Join(", ", dirs.Take(6))}" +
          (dirs.Length > 6 ? ", ..." : ""));

        // ---- which player this host has ------------------------------------
        W("");
        W("what the search found");
        var found = PipeAudioOutput.Found;
        var why = PipeAudioOutput.LoadProblem;
        W(found is null ? $"  {why}" : $"  {found} at {found.Path}");

        // One or the other, never both and never neither: a null player with no reason
        // is a silent app with nothing to read, and a reason beside a working player
        // would put a false explanation in the diagnostics.
        Check("the search gives either a player or a reason", (found is null) != (why is null));
        if (found is not null)
        {
            Check("the player named is one of the three it looks for",
                  PipeAudioOutput.Candidates.Any(c => c.Command == found.Command), found.Command);
            Check("...and the file it resolved to is really there",
                  File.Exists(found.Path), found.Path);
        }

        // ---- choosing between them -----------------------------------------
        //
        // Against directories built here rather than against whatever this machine
        // happens to have, because the rule is an ordering and a machine with one player
        // cannot show an ordering at all.
        W("");
        W("choosing when a machine has more than one");
        var root = Path.Combine(Path.GetTempPath(), "xzw-pipeaudio-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var a = Fake(root, "a", "aplay");
            Check("the last resort is used when it is all there is",
                  PipeAudioOutput.Find(a)?.Command == "aplay", PipeAudioOutput.Find(a)?.Command);

            var ap = Fake(root, "ap", "aplay", "paplay");
            Check("PulseAudio is preferred to ALSA",
                  PipeAudioOutput.Find(ap)?.Command == "paplay", PipeAudioOutput.Find(ap)?.Command);

            var all = Fake(root, "all", "aplay", "paplay", "pw-play");
            Check("PipeWire is preferred to both",
                  PipeAudioOutput.Find(all)?.Command == "pw-play", PipeAudioOutput.Find(all)?.Command);

            // The rule is "the best player anywhere on PATH", not "whatever the first
            // directory holds". A machine with alsa-utils in /usr/local/bin and PipeWire
            // in /usr/bin is the ordinary case, and picking aplay there would give up a
            // working sound server for no reason.
            Check("a better player later on PATH beats a worse one earlier",
                  PipeAudioOutput.Find(a + Path.PathSeparator + all)?.Command == "pw-play",
                  PipeAudioOutput.Find(a + Path.PathSeparator + all)?.Command);

            var none = Fake(root, "none");
            Check("a path with none of them gives nothing", PipeAudioOutput.Find(none) is null);
            Check("an empty path gives nothing", PipeAudioOutput.Find("") is null);
            Check("no path at all gives nothing, rather than throwing",
                  PipeAudioOutput.Find(null) is null);

            if (OperatingSystem.IsWindows())
            {
                Skip("a name nobody may run is not a player", "execute bits do not apply on this host");
            }
            else
            {
                // A name on PATH that cannot be run is not a player. Picking it would
                // give a start that fails, which reads as the machine having no sound.
                var mixed = Fake(root, "mixed", "aplay");
                var unreadable = Path.Combine(mixed, "pw-play");
                File.WriteAllText(unreadable, "#!/bin/sh\n");
                File.SetUnixFileMode(unreadable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                Check("a name nobody may run is not a player",
                      PipeAudioOutput.Find(mixed)?.Command == "aplay",
                      PipeAudioOutput.Find(mixed)?.Command);
            }

            // ---- the contract, against a stand-in --------------------------
            W("");
            W("the contract, with a stand-in player and no device");
            var sink = StandIn("cat");
            var deaf = StandIn("sleep", "30");

            if (sink is null || deaf is null)
            {
                Skip("Play, Stop, Finished and Dispose",
                     $"no stand-in on this host ({(sink is null ? "cat" : "sleep")} is not on PATH)");
            }
            else
            {
                // A player that reads nothing: the pipe fills, the write blocks, and the
                // stop lands in the middle of one. That is the case a stop has to
                // survive, and a player that swallows everything would never show it.
                var stopped = new Watch(deaf);
                var started = Stopwatch.StartNew();
                stopped.Out.Play(Clip(20));
                started.Stop();

                // The caller is the UI thread. A Play that waits for the clip is a
                // window that stops redrawing for as long as the sound lasts.
                Check("Play hands back at once instead of playing the clip",
                      started.ElapsedMilliseconds < 500, $"{started.ElapsedMilliseconds} ms");
                Check("...and reports that it is playing", stopped.Out.IsPlaying);

                stopped.Out.Stop();
                var quiet = Wait(() => !stopped.Out.IsPlaying, 4000);
                Thread.Sleep(300);          // the events are raised after playing clears
                Check("Stop stops it", quiet && !stopped.Out.IsPlaying);
                Check("Stop does not raise Finished", stopped.Finished == 0,
                      $"{stopped.Finished} time(s)");
                // The point of the whole exercise: killing the player breaks the pipe
                // under a blocked write, and that is the normal way a stop ends.
                Check("a killed player is not a failure", stopped.Failed == 0, stopped.Said);
                stopped.Out.Dispose();

                var ran = new Watch(sink);
                ran.Out.Play(Clip(0.25));
                var ended = Wait(() => ran.Finished > 0, 15000);
                Check("a clip that runs out raises Finished", ended);
                Thread.Sleep(500);          // long enough for a second one to show up
                Check("...exactly once", ran.Finished == 1, $"{ran.Finished} time(s)");
                Check("...and nothing is reported as failed", ran.Failed == 0, ran.Said);
                Check("...and it is not playing any more", !ran.Out.IsPlaying);
                ran.Out.Dispose();

                // Back to back through one output, because the per-clip state is there
                // to stop an old pump answering for the clip that replaced it.
                var twice = new Watch(sink);
                twice.Out.Play(Clip(0.15));
                Wait(() => twice.Finished > 0, 15000);
                twice.Out.Play(Clip(0.15));
                var second = Wait(() => twice.Finished > 1, 15000);
                Thread.Sleep(500);
                Check("two clips in a row raise it once each", second && twice.Finished == 2,
                      $"{twice.Finished} time(s)");
                twice.Out.Dispose();

                // Starting something else is a stop as much as Stop is, and must not
                // advance a run either: the clip that was dropped did not reach its end.
                // The deaf stand-in for both, so neither clip can end by itself and the
                // only thing that could raise Finished is the replacement itself.
                var cut = new Watch(deaf);
                cut.Out.Play(Clip(20));
                Wait(() => cut.Out.IsPlaying, 2000);
                cut.Out.Play(Clip(20));
                Thread.Sleep(500);
                Check("replacing a clip does not raise Finished for the one dropped",
                      cut.Finished == 0, $"{cut.Finished} time(s)");
                Check("...and the one that replaced it is what is playing now",
                      cut.Out.IsPlaying);
                cut.Out.Dispose();

                var held = new Watch(deaf);
                held.Out.Play(Clip(20));
                Wait(() => held.Out.IsPlaying, 2000);
                held.Out.Dispose();
                held.Out.Dispose();
                Check("disposing during playback is safe and stops it", !held.Out.IsPlaying);
                Check("...twice is safe as well", true);
                Check("...and does not raise Finished", held.Finished == 0,
                      $"{held.Finished} time(s)");
            }

            // A player that will not start at all. The clip cannot be heard, but the run
            // it belongs to still has to move on, or one bad row stalls it for ever.
            var absent = new Player("xzw-not-a-player", "stand-in", (_, _) => [])
            {
                Path = Path.Combine(root, "xzw-not-a-player"),
            };
            var broken = new Watch(absent);
            broken.Out.Play(Clip(0.2));
            Check("a player that cannot start is reported", Wait(() => broken.Failed > 0, 4000),
                  broken.Said);
            Check("...and the run still advances", Wait(() => broken.Finished == 1, 4000),
                  $"{broken.Finished} time(s)");
            broken.Out.Dispose();

            var nothing = new Watch(absent);
            nothing.Out.Play(null);
            Check("a clip with nothing in it advances rather than hanging",
                  Wait(() => nothing.Finished == 1, 4000), $"{nothing.Finished} time(s)");
            nothing.Out.Dispose();
        }
        finally { try { Directory.Delete(root, true); } catch { } }

        // ---- against the player this host really has -------------------------
        W("");
        W("the player this host really has");
        if (found is null)
        {
            Skip("a short clip through the real player", why);
        }
        else
        {
            using var real = PipeAudioOutput.Using(found);
            var raised = 0;
            string said = null;
            real.Finished += () => Interlocked.Increment(ref raised);
            real.Failed += m => said ??= m;
            real.Play(Clip(0.15));
            var over = Wait(() => Volatile.Read(ref raised) > 0, 15000);
            Thread.Sleep(500);

            // Silence, so a machine that does have a device is not made to squeak. The
            // check is the contract and not the sound: a machine with no server running
            // reports a failure AND finishes, because a clip that cannot play must still
            // let the run move on.
            Check("a short silent clip ends and raises Finished once",
                  over && raised == 1, $"{raised} time(s)");
            W(said is null
                  ? $"  {found.Command} reported nothing, so the clip was accepted"
                  : $"  {found.Command} reported: {said}");
            Check("what it says about the device, if anything, is readable",
                  said is null || said.Length > 0, said);
        }

        // ---- what the app would actually get --------------------------------
        W("");
        W("which output AudioOut picks here");
        using (var picked = AudioOut.Create())
        {
            var got = picked.GetType().Name;
#if WINDOWS
            // This target is the one the window is built against, and it keeps WaveOut.
            var want = nameof(WaveOutAudio);
#else
            var want = PortAudioOutput.LoadProblem is null ? nameof(PortAudioOutput)
                     : found is not null ? nameof(PipeAudioOutput)
                     : nameof(NoAudio);
#endif
            Check($"the chain gives {want}", got == want, got);
            W($"  problem: {picked.Problem() ?? "none"}");
            W($"  summary: {picked.Summary()}");

            if (picked is NoAudio silent)
            {
                // Both attempts, because "PortAudio is missing" on its own sends the
                // reader to install a library that was never the only way to make a
                // sound here.
                Check("silence explains both attempts",
                      silent.Reason is { } r && r.Contains("PortAudio") && r.Contains("pw-play"),
                      silent.Reason);
            }
            else if (picked is PipeAudioOutput pipe)
            {
                Check("the summary names the player it found",
                      pipe.Summary().Contains(pipe.Driving.Command) &&
                      pipe.Summary().Contains(pipe.Driving.What), pipe.Summary());
            }
        }

        W("");
        W(fails == 0 ? "ALL CHECKS PASSED" : $"{fails} CHECK(S) FAILED");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// A directory holding runnable files by these names, for the search to find. The
    /// contents do not matter: nothing here is ever started, and what is being checked
    /// is which name wins.
    /// </summary>
    private static string Fake(string root, string name, params string[] commands)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        foreach (var command in commands)
        {
            var file = Path.Combine(dir, AppPaths.Exe(command));
            File.WriteAllText(file, "#!/bin/sh\nexit 0\n");
            AppPaths.MakeExecutable(file);
        }
        return dir;
    }

    /// <summary>
    /// A player standing in for a real one, or null when this host has no such command.
    /// Deliberately a program that exists everywhere rather than one shipped for the
    /// purpose, so that the test stays true on a machine that has nothing installed.
    /// </summary>
    private static Player StandIn(string command, params string[] arguments)
    {
        var full = PipeAudioOutput.OnPath(command, PipeAudioOutput.SearchPath);
        return full is null
            ? null
            : new Player(command, "stand-in", (_, _) => arguments) { Path = full };
    }

    /// <summary>An output with its two events counted, which is the whole contract.</summary>
    private sealed class Watch
    {
        private int _finished;
        private int _failed;
        private string _said;

        public Watch(Player player)
        {
            Out = PipeAudioOutput.Using(player);
            Out.Finished += () => Interlocked.Increment(ref _finished);
            Out.Failed += m => { Interlocked.Increment(ref _failed); _said ??= m; };
        }

        public PipeAudioOutput Out { get; }
        public int Finished => Volatile.Read(ref _finished);
        public int Failed => Volatile.Read(ref _failed);
        public string Said => _said;
    }

    /// <summary>
    /// A clip of a known length with nothing in it. Silence because these checks are
    /// about when a clip ends, and the one machine in the fleet that does have a device
    /// should not be made to squeal for a test of the plumbing.
    /// </summary>
    private sealed class Silence : ISampleProvider
    {
        private int _left;

        public Silence(double seconds, int rate = 44100, int channels = 2)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);
            _left = (int)(seconds * rate) * channels;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(Span<float> buffer)
        {
            var n = Math.Min(buffer.Length, _left);
            buffer[..n].Clear();
            _left -= n;
            return n;
        }
    }

    private static ISampleProvider Clip(double seconds) => new Silence(seconds);

    /// <summary>
    /// Poll rather than wait on the event itself: every one of these checks is about
    /// whether something happened at all and how often, so nothing here may block on the
    /// thing it is counting.
    /// </summary>
    private static bool Wait(Func<bool> until, int ms)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < ms)
        {
            if (until()) return true;
            Thread.Sleep(20);
        }
        return until();
    }
}
