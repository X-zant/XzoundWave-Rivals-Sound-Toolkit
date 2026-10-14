using System.Reflection;
using System.Runtime.InteropServices;

namespace MRAudioKit;

/// <summary>
/// PortAudio's C API, only the blocking parts of it.
///
/// The blocking API is used rather than the callback one because a callback would run
/// on a thread PortAudio owns, and reading an <c>ISampleProvider</c> chain there means
/// no allocation, no exception and no managed lock for the whole call. Writing from our
/// own thread costs a little latency, which nothing here can hear, and buys ordinary
/// managed code.
/// </summary>
internal static class PortAudioNative
{
    /// <summary>
    /// The name every DllImport below is written against. Nothing on disk has to be
    /// called this: <see cref="Resolve"/> is what turns it into a real file.
    /// </summary>
    private const string Lib = "portaudio";

    static PortAudioNative()
    {
        // A static constructor runs before the first call into any method of this type,
        // so the resolver is always registered before the first P/Invoke.
        //
        // It is needed because the default probe never tries a versioned soname: with
        // only libportaudio.so.2 installed, which is what a package manager gives you,
        // "portaudio" resolves to nothing. libportaudio.so is the development symlink
        // and most machines running the app will not have it.
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(PortAudioNative).Assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // Something else in this assembly registered one first. Only one is allowed
            // per assembly, and losing the race costs the extra names rather than the
            // whole backend, so it is not worth failing over.
        }
    }

    /// <summary>The file names worth trying, most specific first.</summary>
    private static string[] Candidates() =>
        OperatingSystem.IsWindows() ? ["portaudio.dll", "portaudio_x64.dll", "libportaudio.dll"]
        : OperatingSystem.IsMacOS() ? ["libportaudio.2.dylib", "libportaudio.dylib"]
                                    : ["libportaudio.so.2", "libportaudio.so"];

    /// <summary>
    /// Where a copy that ships with the app would be. Checked before the system's own,
    /// so a bundled library wins over whatever version happens to be installed.
    /// </summary>
    private static IEnumerable<string> OurDirs()
    {
        string beside = null, tools = null;
        try { beside = AppContext.BaseDirectory; } catch { }
        try { tools = Bundled.ToolsDir; } catch { }
        if (!string.IsNullOrEmpty(beside)) yield return beside;
        if (!string.IsNullOrEmpty(tools)) yield return tools;
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;

        foreach (var dir in OurDirs())
            foreach (var file in Candidates())
            {
                string full;
                try { full = Path.Combine(dir, file); } catch { continue; }
                if (File.Exists(full) && NativeLibrary.TryLoad(full, out var ours)) return ours;
            }

        foreach (var file in Candidates())
            if (NativeLibrary.TryLoad(file, assembly, path, out var system)) return system;

        // Zero means "I have nothing", which hands the name back to the default probe.
        return IntPtr.Zero;
    }

    public const int NoError = 0;

    /// <summary>
    /// The device could not keep up. A warning from Pa_WriteStream rather than a
    /// failure: the samples were accepted, a moment of them will not be heard, and
    /// treating it as an error would end a clip over one late buffer.
    /// </summary>
    public const int OutputUnderflowed = -9980;

    /// <summary>paFloat32. The one format this app ever hands over.</summary>
    public const nuint Float32 = 0x00000001;

    /// <summary>paFramesPerBufferUnspecified: let PortAudio pick its own buffer size.</summary>
    public const nuint FramesUnspecified = 0;

    /// <summary>paNoDevice, which is what "no default output" comes back as.</summary>
    public const int NoDevice = -1;

    /// <summary>
    /// PaDeviceInfo. The layout is the library's, so the fields are in its order and
    /// none may be left out.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceInfo
    {
        public int StructVersion;
        public IntPtr Name;
        public int HostApi;
        public int MaxInputChannels;
        public int MaxOutputChannels;
        public double DefaultLowInputLatency;
        public double DefaultLowOutputLatency;
        public double DefaultHighInputLatency;
        public double DefaultHighOutputLatency;
        public double DefaultSampleRate;
    }

    /// <summary>PaHostApiInfo, for naming which of WASAPI, ALSA or CoreAudio is in use.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HostApiInfo
    {
        public int StructVersion;
        public int Type;
        public IntPtr Name;
        public int DeviceCount;
        public int DefaultInputDevice;
        public int DefaultOutputDevice;
    }

    // PaSampleFormat and the frame counts are C unsigned long, which is four bytes on
    // Windows and eight everywhere else that matters. nuint is the only managed type
    // that is right on both: where the library wants four, the callee reads the low
    // half of the register and ignores the rest.

    [DllImport(Lib, EntryPoint = "Pa_Initialize", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Initialize();

    [DllImport(Lib, EntryPoint = "Pa_Terminate", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Terminate();

    [DllImport(Lib, EntryPoint = "Pa_GetErrorText", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr GetErrorTextPtr(int error);

    [DllImport(Lib, EntryPoint = "Pa_GetVersionText", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr GetVersionTextPtr();

    [DllImport(Lib, EntryPoint = "Pa_GetDeviceCount", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetDeviceCount();

    [DllImport(Lib, EntryPoint = "Pa_GetDefaultOutputDevice", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetDefaultOutputDevice();

    [DllImport(Lib, EntryPoint = "Pa_GetDeviceInfo", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr GetDeviceInfoPtr(int device);

    [DllImport(Lib, EntryPoint = "Pa_GetHostApiInfo", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr GetHostApiInfoPtr(int hostApi);

    [DllImport(Lib, EntryPoint = "Pa_OpenDefaultStream", CallingConvention = CallingConvention.Cdecl)]
    public static extern int OpenDefaultStream(out IntPtr stream, int inputChannels,
                                               int outputChannels, nuint sampleFormat,
                                               double sampleRate, nuint framesPerBuffer,
                                               IntPtr callback, IntPtr userData);

    [DllImport(Lib, EntryPoint = "Pa_StartStream", CallingConvention = CallingConvention.Cdecl)]
    public static extern int StartStream(IntPtr stream);

    [DllImport(Lib, EntryPoint = "Pa_WriteStream", CallingConvention = CallingConvention.Cdecl)]
    public static extern int WriteStream(IntPtr stream, float[] buffer, nuint frames);

    [DllImport(Lib, EntryPoint = "Pa_StopStream", CallingConvention = CallingConvention.Cdecl)]
    public static extern int StopStream(IntPtr stream);

    [DllImport(Lib, EntryPoint = "Pa_AbortStream", CallingConvention = CallingConvention.Cdecl)]
    public static extern int AbortStream(IntPtr stream);

    [DllImport(Lib, EntryPoint = "Pa_CloseStream", CallingConvention = CallingConvention.Cdecl)]
    public static extern int CloseStream(IntPtr stream);

    /// <summary>What the library calls this error. Safe before Pa_Initialize.</summary>
    public static string ErrorText(int error)
    {
        try { return Text(GetErrorTextPtr(error)) ?? $"error {error}"; }
        catch { return $"error {error}"; }
    }

    /// <summary>
    /// Which PortAudio this is, for the diagnostics line. Trimmed at the first comma:
    /// the library appends its source revision, which is "revision unknown" in every
    /// binary anyone ships.
    /// </summary>
    public static string VersionText()
    {
        try
        {
            var full = Text(GetVersionTextPtr());
            if (string.IsNullOrEmpty(full)) return null;
            var comma = full.IndexOf(',');
            return comma > 0 ? full[..comma] : full;
        }
        catch { return null; }
    }

    /// <summary>The default output device and its details, or false when there is none.</summary>
    public static bool TryDefaultOutput(out DeviceInfo info, out string name)
    {
        info = default;
        name = null;
        try
        {
            var index = GetDefaultOutputDevice();
            if (index < 0) return false;
            var p = GetDeviceInfoPtr(index);
            if (p == IntPtr.Zero) return false;
            info = Marshal.PtrToStructure<DeviceInfo>(p);
            name = Text(info.Name);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Which host API a device belongs to, named, or null.</summary>
    public static string HostApiName(int hostApi)
    {
        try
        {
            if (hostApi < 0) return null;
            var p = GetHostApiInfoPtr(hostApi);
            if (p == IntPtr.Zero) return null;
            return Text(Marshal.PtrToStructure<HostApiInfo>(p).Name);
        }
        catch { return null; }
    }

    /// <summary>
    /// Device names are whatever the host API handed PortAudio. UTF-8 is right on Linux
    /// and macOS and right for ASCII everywhere, and a name that decodes oddly is a
    /// cosmetic problem in one diagnostics line, so it is not worth guessing further.
    /// </summary>
    private static string Text(IntPtr p) =>
        p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
}
