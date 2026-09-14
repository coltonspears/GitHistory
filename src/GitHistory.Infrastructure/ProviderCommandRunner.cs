using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace GitHistory.Infrastructure;

public interface IProviderCommandRunner
{
    Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, string failureMessage, CancellationToken cancellationToken);
}

/// <summary>CLI output can contain credentials; it is never logged or included in error messages.</summary>
public sealed class ProviderCommandRunner : IProviderCommandRunner
{
    public async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, string failureMessage, CancellationToken cancellationToken)
    {
        if (executable is not ("gh" or "az")) throw new ArgumentException("Unsupported provider CLI.", nameof(executable));
        cancellationToken.ThrowIfCancellationRequested();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(TimeSpan.FromSeconds(90));
        var info = new ProcessStartInfo
        {
            FileName = ResolveExecutable(executable), UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        // Azure CLI is distributed as az.cmd on Windows. Only this fixed token command is routed through cmd.
        if (OperatingSystem.IsWindows() && Path.GetExtension(info.FileName).Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            if (executable != "az" || arguments.Any(a => a.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '.')))) throw new ArgumentException("Invalid Azure CLI arguments.");
            var commandFile = info.FileName;
            if (commandFile.IndexOfAny(['"', '%', '&', '|', '<', '>', '^', '\r', '\n']) >= 0) throw new InvalidOperationException(failureMessage);
            info.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            info.Arguments = "/d /s /c \"\"" + commandFile + "\" " + string.Join(' ', arguments) + "\"";
        }
        else foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["GH_PROMPT_DISABLED"] = "1";
        info.Environment["GH_PAGER"] = "cat";
        info.Environment.Remove("GH_DEBUG");
        using var process = new Process { StartInfo = info };
        try { if (!process.Start()) throw new InvalidOperationException(failureMessage); }
        catch (Win32Exception) { throw new InvalidOperationException(failureMessage); }
        process.StandardInput.Close();
        using var registration = operation.Token.Register(() => Kill(process));
        var outputTask = ReadAsync(process.StandardOutput, operation, keepOutput: true);
        var errorTask = ReadAsync(process.StandardError, operation, keepOutput: false);
        try
        {
            await Task.WhenAll(outputTask, errorTask, process.WaitForExitAsync(operation.Token)).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException(failureMessage);
            return await outputTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("The provider sign-in command timed out. Check your CLI sign-in and try again.");
        }
        finally
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadAsync(StreamReader reader, CancellationTokenSource operation, bool keepOutput)
    {
        var output = new StringBuilder();
        var buffer = new char[8192];
        var total = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer, operation.Token).ConfigureAwait(false)) != 0)
        {
            total += read;
            if (total > 16 * 1024 * 1024)
            {
                await operation.CancelAsync().ConfigureAwait(false);
                throw new InvalidOperationException("The provider CLI response was too large. Narrow repository access and try again.");
            }
            if (keepOutput) output.Append(buffer, 0, read);
        }
        return output.ToString();
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    private static string ResolveExecutable(string executable)
    {
        if (!OperatingSystem.IsWindows()) return executable;
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in new[] { ".exe", ".cmd" })
            {
                var path = Path.Combine(folder.Trim('"'), executable + extension);
                if (File.Exists(path)) return path;
            }
        }
        return executable;
    }
}
