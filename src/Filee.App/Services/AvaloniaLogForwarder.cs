// Avalonia writes why a graphics path failed to start (ANGLE, WinUI composition, DirectComposition) to its own log,
// which only reaches a debugger. These few areas go to Filee's log as well, so a report of a window drawn the wrong way
// shows the reason. Everything still goes to the sink that was there before.

using Avalonia.Logging;
using Microsoft.Extensions.Logging;

namespace Filee.App.Services;

internal sealed class AvaloniaLogForwarder(ILogSink? inner, Microsoft.Extensions.Logging.ILogger log) : ILogSink
{
    /// <summary>A failing render loop can repeat its error every frame: the log gets the first few lines.</summary>
    internal const int MaxLines = 20;

    private int _lines;

    /// <summary>Wraps Avalonia's current sink (set it up after <c>LogToTrace</c>).</summary>
    public static void Install(Microsoft.Extensions.Logging.ILogger log) =>
        Logger.Sink = new AvaloniaLogForwarder(Logger.Sink, log);

    /// <summary>Graphics start-up areas at Warning and above; Win32Platform only for errors (it warns about clipboard formats).</summary>
    public static bool Forwards(LogEventLevel level, string area) => area switch
    {
        "OpenGL" or "WinUIComposition" or "Vulkan" => level >= LogEventLevel.Warning,
        LogArea.Win32Platform => level >= LogEventLevel.Error,
        _ => false,
    };

    public bool IsEnabled(LogEventLevel level, string area) =>
        Forwards(level, area) || inner?.IsEnabled(level, area) == true;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
        Log(level, area, source, messageTemplate, []);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        if (inner?.IsEnabled(level, area) == true)
            inner.Log(level, area, source, messageTemplate, propertyValues);
        if (!Forwards(level, area) || Interlocked.Increment(ref _lines) > MaxLines)
            return;
        log.Log(level >= LogEventLevel.Error ? LogLevel.Error : LogLevel.Warning, "Avalonia [{Area}]: {Message}",
            area, Format(messageTemplate, propertyValues));
    }

    /// <summary>Fills "{0}" or "{Name}" holes in order, as Avalonia's templates use either.</summary>
    public static string Format(string template, IReadOnlyList<object?> values)
    {
        var text = new System.Text.StringBuilder();
        var next = 0;
        for (var i = 0; i < template.Length; i++)
        {
            var end = template[i] == '{' ? template.IndexOf('}', i + 1) : -1;
            if (end < 0)
            {
                text.Append(template[i]);
                continue;
            }
            text.Append(next < values.Count ? values[next] : template[i..(end + 1)]);
            next++;
            i = end;
        }
        return text.ToString();
    }
}
