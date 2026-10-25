// Setup's log: one line per step in %TEMP%\Filee-Setup-<time>.log (or the /LOG= file). The elevated Setup and the
// one that started it write into the same file.

using System.Globalization;
using System.Text;

namespace Filee.Setup.Platform;

public sealed class SetupLog
{
    private readonly object _lock = new();
    private readonly List<string> _lines = [];

    /// <param name="file">The log file; null keeps the lines in memory only (tests).</param>
    public SetupLog(string? file)
    {
        File = file;
    }

    public string? File { get; }

    /// <summary>Every line written so far (tests, and the error page's "details").</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lock)
                return _lines.ToList();
        }
    }

    public static string DefaultFile(string temp, string prefix) =>
        Path.Combine(temp, $"{prefix}-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.log");

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} [{Environment.ProcessId}] {level} {message}";
        lock (_lock)
        {
            _lines.Add(line);
            if (File is null)
                return;
            try
            {
                System.IO.File.AppendAllText(File, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The log must never stop Setup.
            }
        }
    }
}
