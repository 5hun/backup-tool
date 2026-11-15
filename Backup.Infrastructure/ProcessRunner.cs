using System.Diagnostics;
using System.Text;

namespace Backup.Infrastructure;

public static class ProcessRunner
{
    public static async Task<int> RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory,
        string logPath, CancellationToken ct, Encoding? outputEncoding = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = outputEncoding ?? Encoding.UTF8, StandardErrorEncoding = outputEncoding ?? Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var log = new StreamWriter(logPath, true, Encoding.UTF8);
        await log.WriteLineAsync($"{DateTimeOffset.Now:O} {executable} {string.Join(" ", start.ArgumentList)}");
        ct.ThrowIfCancellationRequested();
        process.Start();
        using var registration = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var gate = new SemaphoreSlim(1);
        async Task Drain(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                await gate.WaitAsync(CancellationToken.None);
                try { await log.WriteLineAsync(line); } finally { gate.Release(); }
            }
        }
        var stdout = Drain(process.StandardOutput); var stderr = Drain(process.StandardError);
        await process.WaitForExitAsync(CancellationToken.None);
        await Task.WhenAll(stdout, stderr);
        await log.WriteLineAsync($"終了コード: {process.ExitCode}");
        gate.Dispose();
        ct.ThrowIfCancellationRequested();
        return process.ExitCode;
    }
}
