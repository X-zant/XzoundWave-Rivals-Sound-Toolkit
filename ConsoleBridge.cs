using System.IO;
using System.Runtime.InteropServices;

namespace MRAudioKit;

/// <summary>
/// Gets console output out of a windowed application.
///
/// This is a WinExe, which means Windows gives it no console. Every headless verb
/// here writes with Console.WriteLine, and that goes nowhere when the program is
/// started from Explorer or from cmd: the person sees the command return instantly
/// and nothing printed, which looks exactly like a crash. It only ever appeared to
/// work because a shell that pipes our output supplies a handle to write into.
///
/// So: attach to whatever console launched us if there is one, and let callers know
/// when there is not, so they can put the text somewhere a person will find it.
/// </summary>
public static class ConsoleBridge
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int which);

    private const int StdOutputHandle = -11;

    /// <summary>
    /// Is there a real place for stdout to go? Console.IsOutputRedirected alone cannot
    /// answer this: a windowed process with no console at all has a null stdout handle,
    /// and .NET reports that as "redirected", which is how this class first came to
    /// claim a console that did not exist and swallow the output it was written to save.
    /// </summary>
    private static bool StdOutIsReal()
    {
        var h = GetStdHandle(StdOutputHandle);
        return h != IntPtr.Zero && h != new IntPtr(-1);
    }

    /// <summary>True once output will actually land somewhere visible.</summary>
    public static bool HasConsole { get; private set; }

    /// <summary>
    /// Call before a verb writes anything. Safe to call more than once, and harmless
    /// when the process was started from a shell that already redirects our output.
    /// </summary>
    public static void Attach()
    {
        if (HasConsole) return;

        // A genuinely redirected stdout (a pipe, a file) is already usable -- do not
        // disturb it. The handle test is what separates that from having none at all.
        if (StdOutIsReal() && Console.IsOutputRedirected) { HasConsole = true; return; }

        if (GetConsoleWindow() != IntPtr.Zero || AttachConsole(AttachParentProcess))
        {
            try
            {
                // .NET cached a writer for the handle we did not have yet.
                var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                Console.SetOut(stdout);
                HasConsole = true;
            }
            catch { HasConsole = false; }
        }
    }
}
