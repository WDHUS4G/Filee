// Runs command line engines (LibreOffice, rhwp, ...) with a timeout, cancellation and captured output.

using System.Diagnostics;
using System.Text;

namespace Filee.Engines.Infrastructure;

/// <summary>Result of an external process.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Helper for starting external tools safely.</summary>
public static class ProcessRunner
{
    /// <summary>
    /// Starts <paramref name="executable"/> with the given arguments (no shell, arguments are escaped),
    /// waits for it to exit and kills the whole process tree on timeout or cancellation.
    /// </summary>
    /// <param name="onOutput">Called with every line of standard output and error as it arrives (e.g. to parse progress).</param>
    public static async Task<ProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? workingDirectory = null,
        IDictionary<string, string>? environment = null,
        Action<string>? onOutput = null)
    {
        var psi = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executable) ?? "",
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);
        if (environment is not null)
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (stdout) stdout.AppendLine(e.Data); onOutput?.Invoke(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (stderr) stderr.AppendLine(e.Data); onOutput?.Invoke(e.Data); } };

        if (!process.Start())
            throw new InvalidOperationException($"Could not start {executable}.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(executable)} did not finish within {timeout.TotalSeconds:0} s.");
        }

        // Make sure the async output readers have drained.
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
