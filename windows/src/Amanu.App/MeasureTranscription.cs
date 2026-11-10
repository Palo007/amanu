using System.IO;
using System.Net.Http;
using Amanu.Core.Configuration;

namespace Amanu.App;

/// <summary>
/// `Amanu.exe --measure-transcription &lt;recording&gt; &lt;output&gt; [engine]`, for
/// windows\scripts\Measure-Transcription.ps1 (roadmap-speed step 0). Copies the
/// recording's audio into `output` and transcribes the copy there on this
/// computer's model; timings land in output\transcribe.log, errors in
/// output\measure.error. Close Amanu first: two processes would share the CPU.
/// </summary>
internal static class MeasureTranscription
{
    public static int Run(string[] arguments)
    {
        var output = arguments.Length > 2 ? Path.GetFullPath(arguments[2]) : null;
        try
        {
            if (output is null) throw new ArgumentException("usage: --measure-transcription <recording> <output> [engine]");
            var recording = Path.GetFullPath(arguments[1]);
            if (string.Equals(recording.TrimEnd('\\'), output.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The output folder must not be the recording itself.");
            var engine = arguments.Length > 3 ? arguments[3] : null;

            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dataDirectory = Path.Combine(localData, "Amanu Data");
#if DEBUG
            if (Environment.GetEnvironmentVariable("AMANU_TEST_DATA") is { Length: > 0 } testDirectory)
                dataDirectory = Path.GetFullPath(testDirectory);
#endif
            var settings = new AppSettingsStore(Path.Combine(dataDirectory, "config.json"), home).Load().Settings;
            // Measurement only: try level normalization on or off without changing config.json.
            if (Environment.GetEnvironmentVariable("AMANU_MEASURE_LEVELS") is { Length: > 0 } levels)
                settings.Transcription.LevelNormalization = levels;
            // Measurement only: the quality suite runs one recording through every echo
            // remover, and "off" gives the baseline of no echo removal, without touching config.json.
            if (Environment.GetEnvironmentVariable("AMANU_MEASURE_ECHO") is { Length: > 0 } echo)
            {
                settings.OfflineEchoCancellation = echo != "off";
                if (echo != "off") settings.EchoModel = echo;
            }
            // The same limit as the running app: one Gemini request took six minutes in a test,
            // and the default of 100 s would fail a cloud engine that the app itself would let finish.
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            var models = new ModelManager(dataDirectory, http);
            // Analytics never sends from here: it is given no permission.
            var analytics = new AnalyticsService(dataDirectory, () => settings, () => false, http);
            var processing = new ProcessingCoordinator(() => settings, () => true, new SecretStore(), models, http, analytics);
            processing.MeasureAsync(recording, output, engine, CancellationToken.None).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception exception)
        {
            if (output is not null)
            {
                try
                {
                    Directory.CreateDirectory(output);
                    File.WriteAllText(Path.Combine(output, "measure.error"), exception.ToString());
                }
                catch (Exception logging) when (logging is IOException or UnauthorizedAccessException) { }
            }
            return 1;
        }
    }
}
