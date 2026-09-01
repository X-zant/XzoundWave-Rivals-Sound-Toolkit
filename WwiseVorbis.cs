using System.Diagnostics;
using System.IO;
using System.Text;

namespace MRAudioKit;

/// <summary>
/// Encodes WAV to a Wwise **Vorbis** .wem by driving Audiokinetic's own
/// WwiseConsole.exe. There is no way to produce Wwise Vorbis without it: the format
/// is Ogg Vorbis with the container stripped and the codebooks repacked, and every
/// community tool goes in the other direction (wem -> ogg) only.
///
/// Needs two things from the user, neither of which ships here:
///   * WwiseConsole.exe from a Wwise install (free via the Audiokinetic Launcher)
///   * any .wproj to hang the conversion off — their own, or soundKit's stub
///
/// UNVERIFIED ON THIS MACHINE: no Wwise install was present to test against, so this
/// path is written to the documented interface and reports failures verbatim rather
/// than pretending to have succeeded.
/// </summary>
public static class WwiseVorbis
{
    public static bool IsConfigured(Settings s) =>
        !string.IsNullOrWhiteSpace(s.WwiseConsolePath) && File.Exists(s.WwiseConsolePath) &&
        !string.IsNullOrWhiteSpace(s.WwiseProjectPath) && File.Exists(s.WwiseProjectPath);

    public static string WhyNot(Settings s)
    {
        if (string.IsNullOrWhiteSpace(s.WwiseConsolePath)) return "WwiseConsole.exe not set";
        if (!File.Exists(s.WwiseConsolePath)) return "WwiseConsole.exe not found at the configured path";
        if (string.IsNullOrWhiteSpace(s.WwiseProjectPath)) return "no .wproj set";
        if (!File.Exists(s.WwiseProjectPath)) return ".wproj not found at the configured path";
        return null;
    }

    /// <summary>
    /// Force the project's default conversion to Vorbis. Wwise recreates this work
    /// unit as PCM whenever it feels like it, so it is rewritten before every batch
    /// rather than trusted to still say what it said last time.
    /// </summary>
    public static void ForceVorbisConversion(string wprojPath)
    {
        var dir = Path.Combine(Path.GetDirectoryName(wprojPath)!, "Conversion Settings");
        Directory.CreateDirectory(dir);
        const string wwu = """
<?xml version="1.0" encoding="utf-8"?>
<WwiseDocument Type="WorkUnit" ID="{401A3BBC-4CB4-44CC-9977-96E97AA8EADC}" SchemaVersion="119">
	<Conversions>
		<WorkUnit Name="Default Work Unit" ID="{401A3BBC-4CB4-44CC-9977-96E97AA8EADC}" PersistMode="Standalone">
			<ChildrenList>
				<Conversion Name="Default Conversion Settings" ID="{6D1B890C-9826-4384-BF07-C15223E9FB56}">
					<PropertyList>
						<Property Name="SRConversionQuality" Type="int32">
							<ValueList><Value Platform="Windows">4</Value></ValueList>
						</Property>
					</PropertyList>
					<ReferenceList>
						<Reference Name="Conversion">
							<Custom>
								<AudioFileConversionProperties Name="" ID="{A69B7C10-DDD1-4A70-9A7A-8B7A73A05B44}">
									<PropertyList>
										<Property Name="AllowChannelUpmix" Type="bool" Value="False"/>
										<Property Name="Format" Type="string">
											<ValueList><Value Platform="Windows">Vorbis</Value></ValueList>
										</Property>
									</PropertyList>
								</AudioFileConversionProperties>
							</Custom>
						</Reference>
					</ReferenceList>
				</Conversion>
			</ChildrenList>
		</WorkUnit>
	</Conversions>
</WwiseDocument>
""";
        File.WriteAllText(Path.Combine(dir, "Default Work Unit.wwu"), wwu, new UTF8Encoding(false));
    }

    /// <summary>Convert one WAV. Returns null and fills <paramref name="error"/> on failure.</summary>
    public static byte[] Encode(Settings s, string wavPath, out string error)
    {
        error = WhyNot(s);
        if (error is not null) return null;

        var projDir = Path.GetDirectoryName(s.WwiseProjectPath)!;
        var stem = Path.GetFileNameWithoutExtension(wavPath);

        // convert-external-source drops output under GeneratedSoundBanks\<platform>
        // relative to the project.
        var outDir = Path.Combine(projDir, "GeneratedSoundBanks", "Windows");
        var expected = Path.Combine(outDir, stem + ".wem");
        if (File.Exists(expected)) File.Delete(expected);

        var xml = Path.Combine(projDir, "mrak_sources.xml");
        File.WriteAllText(xml, $"""
<?xml version="1.0" encoding="utf-8"?>
<ExternalSourcesList SchemaVersion="1" Root="">
  <Source Path="{wavPath.Replace('\\', '/')}" Conversion="Default Conversion Settings" />
</ExternalSourcesList>
""", new UTF8Encoding(false));

        try
        {
            var psi = new ProcessStartInfo(s.WwiseConsolePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("convert-external-source");
            psi.ArgumentList.Add(s.WwiseProjectPath);
            psi.ArgumentList.Add("--platform");
            psi.ArgumentList.Add("Windows");
            psi.ArgumentList.Add("--source-file");
            psi.ArgumentList.Add(xml);

            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEnd();
            var se = p.StandardError.ReadToEnd();
            p.WaitForExit();

            if (!File.Exists(expected))
            {
                error = $"WwiseConsole produced no .wem for {stem}. " +
                        (so + se).Trim().Split('\n').FirstOrDefault()?.Trim();
                return null;
            }
            var bytes = File.ReadAllBytes(expected);
            File.Delete(expected);

            // Sanity: it must actually be Vorbis, not a PCM fallback Wwise chose quietly.
            var info = BnkBuilder.Inspect(bytes);
            if (info.FormatTag != 0xFFFF)
                error = $"WwiseConsole returned {info.Codec}, not Vorbis — check the " +
                        "project's Conversion Settings.";
            return bytes;
        }
        catch (Exception ex) { error = ex.Message; return null; }
        finally { if (File.Exists(xml)) File.Delete(xml); }
    }
}
