using System.IO;
using Logger;
using Log = Logger.Logger;

namespace ReadSelectedTextTts;

public partial class App : System.Windows.Application
{
    private string? _logFilePath;
    private const string ConsoleLogEnvVar = "RSTTS_CONSOLE_LOG";

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        var appDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ReadSelectedTextTts");
        Directory.CreateDirectory(appDirectory);

        _logFilePath = Path.Combine(appDirectory, "app.log");
        Log.SetLogFile(_logFilePath, append: true);
        Log.AddDefaultRules();
#if DEBUG
        Log.SetMinLevel(LogLvl.TRC);
#else
        Log.SetMinLevel(LogLvl.INF);
#endif

        if (ShouldEnableConsoleLogging(e.Args))
        {
            var opened = Log.EnsureConsoleForGuiApp(
                attachToParent: true,
                allocateIfMissing: true,
                title: "ReadSelectedTextTts Logs"
            );

            if (opened)
            {
                // Console mode is explicit, so keep ANSI enabled for pretty log output.
                Log.EnableAnsiConsole(true);
                Log.Inf("Console logging enabled.");
            }
            else
                Log.Wrn("Console logging requested, but no console could be attached.");
        }

        Log.PrintHeader("Read Selected Text TTS");
        Log.Inf($"Logger initialized. File: {_logFilePath}");
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        Log.Inf("Application exiting.");
        Log.Close();
        base.OnExit(e);
    }

    private static bool ShouldEnableConsoleLogging(string[] args)
    {
        foreach (var arg in args)
        {
            if (
                string.Equals(arg, "--console", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--terminal", StringComparison.OrdinalIgnoreCase)
            )
            {
                return true;
            }
        }

        var envValue = Environment.GetEnvironmentVariable(ConsoleLogEnvVar);
        if (string.IsNullOrWhiteSpace(envValue))
            return false;

        return
            string.Equals(envValue, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(envValue, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(envValue, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(envValue, "on", StringComparison.OrdinalIgnoreCase);
    }
}
