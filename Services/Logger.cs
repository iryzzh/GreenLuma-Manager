using System.IO;
using System.Runtime.CompilerServices;

namespace GreenLuma_Manager.Services;

public static class Logger
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GLM_Manager");

    private static readonly string LogPath = Path.Combine(LogDir, "GreenLuma-Manager.log");
    private static readonly string PrevLogPath = Path.Combine(LogDir, "GreenLuma-Manager.prev.log");
    private static readonly object Lock = new();

    static Logger()
    {
        try
        {
            if (!Directory.Exists(LogDir))
                Directory.CreateDirectory(LogDir);

            if (File.Exists(LogPath))
                File.Move(LogPath, PrevLogPath, true);
        }
        catch
        {
        }
    }

    public static void Info(
        string message,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        Write("Info", message, memberName, filePath, lineNumber);
    }

    public static void Debug(
        string message,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        Write("Debug", message, memberName, filePath, lineNumber);
    }

    public static void Warn(
        string message,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        Write("Warn", message, memberName, filePath, lineNumber);
    }

    public static void Error(
        string message,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        Write("Error", message, memberName, filePath, lineNumber);
    }

    public static void Error(
        Exception ex,
        string message,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        Write("Error", $"{message}: {ex.GetType().Name}: {ex.Message}", memberName, filePath, lineNumber);
    }

    public static void Perf(
        string message,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        Write("Perf", message, memberName, filePath, lineNumber);
    }

    public static IDisposable Measure(
        string operationName,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
#if DEBUG
        return new PerfTimer(operationName, memberName, filePath, lineNumber);
#else
        return NoopDisposable.Instance;
#endif
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class PerfTimer : IDisposable
    {
        private readonly string _operation;
        private readonly string _memberName;
        private readonly string _filePath;
        private readonly int _lineNumber;
        private readonly long _start;

        public PerfTimer(string operation, string memberName, string filePath, int lineNumber)
        {
            _operation = operation;
            _memberName = memberName;
            _filePath = filePath;
            _lineNumber = lineNumber;
            _start = System.Diagnostics.Stopwatch.GetTimestamp();
            Write("Perf", $"START: {_operation}", _memberName, _filePath, _lineNumber);
        }

        public void Dispose()
        {
            var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            Write("Perf", $"END: {_operation} took {elapsedMs:F2} ms", _memberName, _filePath, _lineNumber);
        }
    }

    private static void Write(string level, string message, string memberName, string filePath, int lineNumber)
    {
        try
        {
            var fileName = Path.GetFileName(filePath);
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var entry = $"[{timestamp}] [{level}] [{fileName}:{memberName}:{lineNumber}] {message}";

            lock (Lock)
            {
                File.AppendAllText(LogPath, entry + Environment.NewLine);
            }
        }
        catch
        {
        }
    }
}
