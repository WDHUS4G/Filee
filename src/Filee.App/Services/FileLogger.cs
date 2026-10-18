// Minimal file logger: %APPDATA%\Filee\logs\filee-yyyyMMdd.log, keeps the last 7 days.

using Microsoft.Extensions.Logging;

namespace Filee.App.Services;

public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly object _lock = new();

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        foreach (var old in Directory.EnumerateFiles(directory, "filee-*.log")
                     .Where(f => File.GetLastWriteTime(f) < DateTime.Now.AddDays(-7)))
        {
            try { File.Delete(old); } catch (IOException) { }
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    private void Write(string line)
    {
        lock (_lock)
        {
            try
            {
                File.AppendAllText(Path.Combine(_directory, $"filee-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
            }
            catch (IOException) { }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var shortCategory = category[(category.LastIndexOf('.') + 1)..];
            var line = $"{DateTime.Now:HH:mm:ss.fff} {logLevel.ToString()[..4].ToUpperInvariant()} {shortCategory}: {formatter(state, exception)}";
            if (exception is not null)
                line += Environment.NewLine + exception;
            provider.Write(line);
        }
    }
}
