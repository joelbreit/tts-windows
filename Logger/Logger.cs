using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Logger;

public enum LogLvl
{
	TRC,
	DBG,
	INF,
	WRN,
	ERR,
}

public static class Logger
{
	/// <summary>
	/// Prints a formatted header with a centered title and separator lines.
	/// </summary>
	/// <param name="title">The title to display in the header.</param>
	/// <param name="width">The total width of the header (default: 41).</param>
	[Conditional("ENABLE_LOGGING")]
	public static void PrintHeader(string title)
	{
		var width = Math.Min(41, title.Length + 4);
		if (string.IsNullOrWhiteSpace(title))
			title = "";
		string separator = new string('=', width);
		string centeredTitle =
			title.Length >= width
				? title
				: title.PadLeft((width + title.Length) / 2).PadRight(width);
		Inf("");
		Inf(separator);
		Inf(centeredTitle);
		Inf(separator);
		// Inf($"Last Tag:            {BuildInfo.LAST_TAG}");
		// Inf($"Commits Since Tag:   {BuildInfo.COMMITS_SINCE_LAST_TAG}");
		// Inf($"Branch:              {BuildInfo.BRANCH_NAME}");
		// Inf($"Git Commit:          {BuildInfo.GIT_COMMIT}");
		// Inf($"Uncommitted Changes: {BuildInfo.IS_DIRTY}");
		// Inf($"Build Date:          {BuildInfo.BUILD_DATE}");
		// Inf(separator);
		Inf("");
	}

	public static class Ansi
	{
		public const string Reset = "\u001b[0m";
		public const string Bold = "\u001b[1m";
		public const string Dim = "\u001b[2m";
		public const string Italic = "\u001b[3m";
		public const string Underline = "\u001b[4m";
		public const string Blink = "\u001b[5m";

		public const string Black = "\u001b[30m";
		public const string Red = "\u001b[31m";
		public const string Green = "\u001b[32m";
		public const string Yellow = "\u001b[33m";
		public const string Blue = "\u001b[34m";
		public const string Magenta = "\u001b[35m";
		public const string Cyan = "\u001b[36m";
		public const string White = "\u001b[37m";

		public const string BrightBlack = "\u001b[90m";
		public const string BrightRed = "\u001b[91m";
		public const string BrightGreen = "\u001b[92m";
		public const string BrightYellow = "\u001b[93m";
		public const string BrightBlue = "\u001b[94m";
		public const string BrightMagenta = "\u001b[95m";
		public const string BrightCyan = "\u001b[96m";
		public const string BrightWhite = "\u001b[97m";
	}

	private static readonly string[] _levelNames = { "TRC", "DBG", "INF", "WRN", "ERR" };
	private static readonly Dictionary<LogLvl, string> _levelColors = new()
	{
		[LogLvl.TRC] = Ansi.BrightBlack,
		[LogLvl.DBG] = Ansi.Cyan,
		[LogLvl.INF] = Ansi.Green,
		[LogLvl.WRN] = Ansi.Yellow,
		[LogLvl.ERR] = Ansi.Red,
	};
	private static string _timestampColor = Ansi.BrightBlue;

	private static LogLvl _minLevel = LogLvl.INF;

	public sealed record ColorRule(Regex Pattern, string Color, string Description);

	private static readonly List<ColorRule> _colorRules = new();
	private static readonly object _rulesLock = new();

	// Outputs
	private static TextWriter _consoleWriter = Console.Out;
	private static TextWriter? _fileWriter;
	private static string? _logFilePath;
	private static int _logLineCount = 0;
	private const int MAX_LOG_LINES = 10000; // Should keep the file to around 1 MB

	// Concurrency: single lock for atomic line writes to both outputs
	private static readonly object _writeLock = new();

	// Whether to emit ANSI to console; if false we strip them
	private static bool _useAnsiConsole = DetectAndEnableAnsi();

	// Lock for configuration fields that are read during logging
	private static readonly object _configLock = new();

	public static void SetMinLevel(LogLvl level)
	{
		lock (_configLock)
			_minLevel = level;
	}

	public static void SetConsoleWriter(TextWriter writer)
	{
		lock (_configLock)
			_consoleWriter = writer;
	}

	public static void SetLogFile(string path, bool append = false)
	{
		lock (_writeLock)
		{
			_fileWriter?.Dispose();
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
			_logFilePath = path;

			// Always start fresh unless explicitly appending
			if (append && File.Exists(path))
			{
				_logLineCount = File.ReadAllLines(path).Length;

				// Trim if file is too large
				if (_logLineCount > MAX_LOG_LINES)
				{
					TrimLogFile();
				}

				_fileWriter = new StreamWriter(path, true) { AutoFlush = true };
			}
			else
			{
				// Start with a fresh file
				_logLineCount = 0;
				_fileWriter = new StreamWriter(path, false) { AutoFlush = true };
			}
		}
	}

	public static void Close()
	{
		lock (_writeLock)
		{
			_fileWriter?.Dispose();
			_fileWriter = null;
			_logFilePath = null;
			_logLineCount = 0;
		}
	}

	public static void EnableAnsiConsole(bool enable)
	{
		lock (_configLock)
			_useAnsiConsole = enable;
	}

	// Map a source string to an RGB color via SHA1 + constrained hue ranges.
	public static (int r, int g, int b) SourceToRgb(string source)
	{
		// SHA1 hash (big-endian) → BigInteger for stable modulo ops
		byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(source));
		var H = new BigInteger(hash, isUnsigned: true, isBigEndian: true);

		(int start, int end)[] hueRanges =
		{
            // 0-30 is too red
            (30, 90),
            // 30-90 is close to orange
            // 90-150 is close to green
            (150, 330),
		};

		int rangeIndex = (int)(H % hueRanges.Length);
		var (hStart, hEnd) = hueRanges[rangeIndex];
		int hWidth = hEnd - hStart;

		int hue = hStart + (int)(H % hWidth);
		double s = 0.8;
		double v = 0.9;

		return HsvToRgb(hue, s, v);
	}

	// HSV (h:0–360) → RGB (0–255), same logic as your Python.
	public static (int r, int g, int b) HsvToRgb(double h, double s, double v)
	{
		double c = v * s;
		double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
		double m = v - c;

		double r1,
			g1,
			b1;
		if (h < 60)
			(r1, g1, b1) = (c, x, 0);
		else if (h < 120)
			(r1, g1, b1) = (x, c, 0);
		else if (h < 180)
			(r1, g1, b1) = (0, c, x);
		else if (h < 240)
			(r1, g1, b1) = (0, x, c);
		else if (h < 300)
			(r1, g1, b1) = (x, 0, c);
		else
			(r1, g1, b1) = (c, 0, x);

		int r = (int)Math.Round((r1 + m) * 255);
		int g = (int)Math.Round((g1 + m) * 255);
		int b = (int)Math.Round((b1 + m) * 255);
		return (Clamp255(r), Clamp255(g), Clamp255(b));
	}

	// Approximate RGB→ANSI 256 color cube (like your Python).
	public static int RgbToAnsi(int r, int g, int b)
	{
		int rr = (int)(r / 255.0 * 5);
		int gg = (int)(g / 255.0 * 5);
		int bb = (int)(b / 255.0 * 5);
		return 16 + 36 * rr + 6 * gg + bb;
	}

	private static int Clamp255(int x) => Math.Max(0, Math.Min(255, x));

	public static void AddColorRule(string pattern, string color, string description)
	{
		var rx = new Regex(pattern, RegexOptions.Compiled);
		lock (_rulesLock)
			_colorRules.Add(new ColorRule(rx, color, description));
	}

	public static void ClearColorRules()
	{
		lock (_rulesLock)
			_colorRules.Clear();
	}

	public static void AddDefaultRules()
	{
		// Communication
		AddColorRule(
			@"\[[A-Z0-9\s_-]+\]\s*->\s*\[[A-Z0-9\s_-]+\]",
			"FROM_SOURCE",
			"Communication arrows"
		);
		AddColorRule(
			@"\[[A-Z0-9\s_-]+\]\s*<-\s*\[[A-Z0-9\s_-]+\]",
			"FROM_SOURCE",
			"Communication arrows"
		);

		// Errors / success / warnings (case-insensitive via inline (?i))
		AddColorRule(
			@"(?i)\b(failed?|error|exception|panic|fatal|disconnected)\b",
			Ansi.BrightRed,
			"Error keywords"
		);
		AddColorRule(
			@"(?i)\b(success|successful|complete|completed|ok|ready|connected)\b",
			Ansi.BrightGreen,
			"Success keywords"
		);
		AddColorRule(
			@"(?i)\b(warning|warn|deprecated|timeout)\b",
			Ansi.BrightYellow,
			"Warning keywords"
		);

		// Network
		AddColorRule(
			@"(?i)\b(connect|disconnect|listen|accept|bind)\b",
			Ansi.BrightCyan,
			"Network terms"
		);

		// Units / sizes
		AddColorRule(@"\b\d+\.?\d*\s*(MB|KB|GB|bytes?)\b", Ansi.Magenta, "File sizes");
		AddColorRule(@"\b\d+\.?\d*\s*(ms)\b", Ansi.Magenta, "Milliseconds");

		// IP:Port and COM
		AddColorRule(@"\b\d{1,3}(\.\d{1,3}){3}:\d+\b", Ansi.Cyan, "IP:Port");
		AddColorRule(@"\bCOM\d+\b", Ansi.Cyan, "COM ports");

		// Date / time
		AddColorRule(@"\b\d{4}-\d{2}-\d{2}\b", Ansi.BrightBlue, "Date YYYY-MM-DD");
		AddColorRule(
			@"\b\d{2}:\d{2}:\d{2}(?:\.\d{1,3})?\b",
			Ansi.BrightBlue,
			"Time HH:MM:SS(.fff)"
		);
		AddColorRule(@"\b(AM|PM)\b", Ansi.BrightBlue, "Time AM/PM");
		AddColorRule(@"\b\d{2}\/\d{2}\/\d{4}\b", Ansi.BrightBlue, "Date MM/DD/YYYY");

		// File paths (Windows + Unix + relative)
		AddColorRule(@"\s[A-Za-z]:[\\/](?:[\w.-]+[\\/])*[\w.-]+/?\s", Ansi.Yellow, "Windows paths");
		AddColorRule(
			@"\s(?:\.\.?/|/)(?:[\w.-]+/)*[\w.-]{4,}/?\s",
			Ansi.Yellow,
			"Unix/relative paths"
		);
		AddColorRule(@"\s(/[A-Za-z0-9._-]{4,})+/?\s", Ansi.Yellow, "Unix paths");

		// Percentages
		AddColorRule(@"\d+(\.\d+)?%", Ansi.BrightMagenta, "Percentages");

		// Numbers preceded by whitespace
		AddColorRule(@"\s(\-)?\d+(\.\d+)*\b", Ansi.BrightMagenta, "Numbers");

		// [Things in brackets]
		AddColorRule(@"\[[\w\s-]+\]", "FROM_SOURCE", "Brackets");

		// Literals
		AddColorRule(@"""[^""]*""", Ansi.BrightBlue, "Double-quoted literals");
		AddColorRule(@"'[^']*'", Ansi.BrightBlue, "Single-quoted literals");
		AddColorRule(@"(?i)\b(NaN|Inf|-Inf|True|False)\b", Ansi.BrightBlue, "Literals");
	}

	public static void LogCore(LogLvl level, string message, string file, int line)
	{
		// Snapshot configuration values under lock to avoid races
		LogLvl minLevel;
		bool useAnsiConsole;
		TextWriter consoleWriter;
		lock (_configLock)
		{
			minLevel = _minLevel;
			useAnsiConsole = _useAnsiConsole;
			consoleWriter = _consoleWriter;
		}

		if (level < minLevel)
			return;

		var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
		var fileName = file is null ? "?" : Path.GetFileName(file);
		var lvl = _levelNames[(int)level];
		var lvlColor = _levelColors[level];

		var coloredMsg = ApplyColorRules(message);

		var lineText =
			$"{_timestampColor}{timestamp}{Ansi.Reset} "
			+ $"{lvlColor}{lvl}{Ansi.Reset} "
			+ $"{Ansi.BrightYellow}{fileName}:{line}{Ansi.Reset}: {coloredMsg}";

		lock (_writeLock)
		{
			if (consoleWriter is not null)
			{
				var consoleText = useAnsiConsole ? lineText : StripAnsi(lineText);
				consoleWriter.WriteLine(consoleText);
			}

			if (_fileWriter is not null)
			{
				// Check if we need to trim the log file before writing
				if (_logLineCount >= MAX_LOG_LINES)
				{
					TrimLogFile();
				}

				_fileWriter.WriteLine(StripAnsi(lineText));
				_logLineCount++;
			}
		}
	}

	[Conditional("ENABLE_LOGGING")]
	[Conditional("DEBUG")]
	public static void Trc(
		string msg,
		[CallerFilePath] string file = "",
		[CallerLineNumber] int line = 0
	) => LogCore(LogLvl.TRC, msg, file, line);

	[Conditional("ENABLE_LOGGING")]
	[Conditional("DEBUG")]
	public static void Dbg(
		string msg,
		[CallerFilePath] string file = "",
		[CallerLineNumber] int line = 0
	) => LogCore(LogLvl.DBG, msg, file, line);

	[Conditional("ENABLE_LOGGING")]
	public static void Inf(
		string msg,
		[CallerFilePath] string file = "",
		[CallerLineNumber] int line = 0
	) => LogCore(LogLvl.INF, msg, file, line);

	[Conditional("ENABLE_LOGGING")]
	public static void Wrn(
		string msg,
		[CallerFilePath] string file = "",
		[CallerLineNumber] int line = 0
	) => LogCore(LogLvl.WRN, msg, file, line);

	[Conditional("ENABLE_LOGGING")]
	public static void Err(
		string msg,
		[CallerFilePath] string file = "",
		[CallerLineNumber] int line = 0
	) => LogCore(LogLvl.ERR, msg, file, line);

	private static string ApplyColorRules(string text)
	{
		string result = text;
		List<ColorRule> snapshot;
		lock (_rulesLock)
			snapshot = new List<ColorRule>(_colorRules);

		foreach (var rule in snapshot)
		{
			var color = rule.Color;
			if (color == "FROM_SOURCE")
			{
				result = rule.Pattern.Replace(
					result,
					m =>
					{
						var (r, g, b) = SourceToRgb(m.Value);
						int ansiCode = RgbToAnsi(r, g, b);
						var sourceColor = $"\u001b[38;5;{ansiCode}m";
						return sourceColor + m.Value + Ansi.Reset;
					}
				);
			}
			else
			{
				result = rule.Pattern.Replace(result, m => color + m.Value + Ansi.Reset);
			}
		}
		return result;
	}

	private static readonly Regex _ansiRx = new(@"\x1b\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

	private static string StripAnsi(string s) => _ansiRx.Replace(s, "");

	/// <summary>
	/// Removes the first half of the log lines when the file gets too large,
	/// keeping the most recent entries.
	/// </summary>
	private static void TrimLogFile()
	{
		if (_logFilePath == null || !File.Exists(_logFilePath))
			return;

		try
		{
			var lines = File.ReadAllLines(_logFilePath);
			var linesToKeep = MAX_LOG_LINES / 2;

			if (lines.Length <= linesToKeep)
				return;

			// Keep the most recent half of the lines
			var trimmedLines = new string[linesToKeep];
			Array.Copy(lines, lines.Length - linesToKeep, trimmedLines, 0, linesToKeep);

			// Close current writer, rewrite file, reopen writer
			_fileWriter?.Dispose();
			File.WriteAllLines(_logFilePath, trimmedLines);
			_fileWriter = new StreamWriter(_logFilePath, true) { AutoFlush = true };
			_logLineCount = linesToKeep;
		}
		catch (Exception ex)
		{
			// If trimming fails, just continue logging
			Console.WriteLine($"Warning: Failed to trim log file: {ex.Message}");
		}
	}

	private static bool DetectAndEnableAnsi()
	{
		try
		{
			if (Console.IsOutputRedirected)
				return false; // raw ANSI in files is ugly
			if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
				return true;

			// Windows: try to enable Virtual Terminal processing once
			var handle = GetStdHandle(STD_OUTPUT_HANDLE);
			if (!GetConsoleMode(handle, out int mode))
				return false;
			if ((mode & ENABLE_VIRTUAL_TERMINAL_PROCESSING) != 0)
				return true;
			return SetConsoleMode(handle, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
		}
		catch
		{
			return false;
		}
	}

	private const int STD_OUTPUT_HANDLE = -11;
	private const int ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr GetStdHandle(int nStdHandle);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out int lpMode);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetConsoleMode(IntPtr hConsoleHandle, int dwMode);
}
