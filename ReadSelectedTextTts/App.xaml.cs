using System.IO;
using Logger;
using Log = Logger.Logger;

namespace ReadSelectedTextTts;

public partial class App : System.Windows.Application
{
    private string? _logFilePath;

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

        Log.PrintHeader("Read Selected Text TTS");
        Log.Inf($"Logger initialized. File: {_logFilePath}");
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        Log.Inf("Application exiting.");
        Log.Close();
        base.OnExit(e);
    }
}
