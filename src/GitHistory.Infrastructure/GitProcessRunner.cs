using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace GitHistory.Infrastructure;

/// <summary>Runs Git without a shell, drains both pipes, and kills the entire process tree on cancellation.</summary>
internal sealed class GitProcessRunner
{
    private const int ErrorLimit = 16 * 1024;

    public Task<string> RunAsync(string? gitDirectory, IEnumerable<string> arguments, CancellationToken cancellationToken) =>
        RunAsync(gitDirectory, arguments, async (stream, token) =>
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            return await reader.ReadToEndAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    public async Task<T> RunAsync<T>(string? gitDirectory, IEnumerable<string> arguments,
        Func<Stream, CancellationToken, Task<T>> consumeOutput, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("--no-pager");
        foreach (var config in new[] { "core.quotepath=false", "color.ui=false", "i18n.logOutputEncoding=utf-8", "log.showSignature=false", "diff.suppressBlankEmpty=false", "diff.external=", "core.hooksPath=", "fetch.fsckObjects=true", "protocol.ext.allow=never" })
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(config);
        }
        if (gitDirectory is not null)
        {
            start.ArgumentList.Add("--git-dir");
            start.ArgumentList.Add(gitDirectory);
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // GCM can still show its normal sign-in window; unavailable terminal prompts fail promptly.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_PAGER"] = "cat";
        start.Environment["LC_ALL"] = "C.UTF-8";
        // Preserve Git's SSH selection, including core.sshCommand, GIT_SSH and GIT_SSH_COMMAND.
        // Configure SSH keys/agent and trust the host in a terminal before opening an SSH remote.
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Win32Exception ex) { throw new InvalidOperationException("Git could not be started. Install Git for Windows and make git available on PATH.", ex); }
        process.StandardInput.Close();

        using var registration = cancellationToken.Register(() => Kill(process));
        var errorTask = ReadErrorAsync(process.StandardError, cancellationToken);
        try
        {
            var result = await consumeOutput(process.StandardOutput.BaseStream, cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
                throw new GitCommandException(process.ExitCode, string.IsNullOrWhiteSpace(error) ? "Git could not complete the operation." : error.Trim());
            return result;
        }
        catch
        {
            Kill(process);
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch (InvalidOperationException) { }
            try { await errorTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
    }

    private static async Task<string> ReadErrorAsync(StreamReader reader, CancellationToken token)
    {
        var output = new StringBuilder();
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            if (output.Length < ErrorLimit) output.Append(buffer, 0, Math.Min(count, ErrorLimit - output.Length));
        return output.ToString();
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    public static async IAsyncEnumerable<string> ReadNulTokensAsync(Stream stream, [EnumeratorCancellation] CancellationToken token)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024, leaveOpen: true);
        var buffer = new char[64 * 1024];
        var fragment = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            var start = 0;
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] != '\0') continue;
                fragment.Append(buffer, start, i - start);
                yield return fragment.ToString();
                fragment.Clear();
                start = i + 1;
            }
            fragment.Append(buffer, start, count - start);
        }
        if (fragment.Length > 0) yield return fragment.ToString();
    }
}

public sealed class GitCommandException(int exitCode, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}
