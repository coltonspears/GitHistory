using System.Diagnostics;
using System.IO;
using GitHistory.Core.Services;
using Microsoft.Win32;

namespace GitHistory.App.Services;

public sealed class DesktopIntegration(string dataDirectory) : IDesktopIntegration
{
    private readonly string dataRoot = LocalCheckoutPaths.NormalizeRoot(dataDirectory);

    public Task<string?> PickFolderAsync(string? initialFolder, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new OpenFolderDialog
        {
            Title = "Link an existing local Git checkout",
            Multiselect = false
        };
        if (initialFolder is not null && Directory.Exists(initialFolder)) dialog.InitialDirectory = initialFolder;
        bool? selected = System.Windows.Application.Current?.MainWindow is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(selected == true ? ValidateCheckout(dialog.FolderName) : null);
    }

    public void OpenBrowser(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0)
            throw new ArgumentException("Only HTTPS or HTTP source control links can be opened.", nameof(url));
        Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true, CreateNoWindow = true });
    }

    public Task OpenExplorerAsync(string localFolder, string? repositoryPath, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        string root = ValidateCheckout(localFolder);
        string target = ValidateTarget(root, repositoryPath, cancellationToken);
        bool selectFile = File.Exists(target);
        if (!selectFile) target = ExistingDirectory(root, target);
        var start = Explorer();
        if (selectFile) start.ArgumentList.Add("/select,");
        start.ArgumentList.Add(target);
        cancellationToken.ThrowIfCancellationRequested();
        Start(start);
    }, cancellationToken);

    public Task OpenCursorAsync(string localFolder, string? repositoryPath, string executable, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        string root = ValidateCheckout(localFolder);
        string target = ValidateTarget(root, repositoryPath, cancellationToken);
        var start = new ProcessStartInfo(ResolveCursorExecutable(executable))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = root
        };
        start.ArgumentList.Add("--reuse-window");
        start.ArgumentList.Add(root);
        if (!target.Equals(root, StringComparison.OrdinalIgnoreCase) && File.Exists(target)) start.ArgumentList.Add(target);
        cancellationToken.ThrowIfCancellationRequested();
        Start(start);
    }, cancellationToken);

    public void OpenDataFolder()
    {
        Directory.CreateDirectory(dataRoot);
        var start = Explorer();
        start.ArgumentList.Add(dataRoot);
        Start(start);
    }

    private string ValidateCheckout(string localFolder)
    {
        string root = LocalCheckoutPaths.NormalizeRoot(localFolder);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The linked checkout no longer exists. Link its current folder from the repository menu.");
        if (LocalCheckoutPaths.IsWithinRoot(dataRoot, root))
            throw new ArgumentException("Choose your working checkout, outside GitHistory's app-owned history cache.", nameof(localFolder));
        if (!Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, ".git")))
            throw new ArgumentException("Choose the root of an existing Git checkout, containing its .git directory or worktree file.", nameof(localFolder));
        return root;
    }

    private static string ValidateTarget(string root, string? repositoryPath, CancellationToken cancellationToken)
    {
        string target = LocalCheckoutPaths.Resolve(root, repositoryPath);
        string relative = Path.GetRelativePath(root, target);
        string current = root;
        CheckLink(root, current);
        if (relative == ".") return target;
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar))
        {
            cancellationToken.ThrowIfCancellationRequested();
            current = Path.Combine(current, segment);
            CheckLink(root, current);
        }
        return target;
    }

    private static void CheckLink(string root, string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        // LinkTarget also recognizes a dangling link, for which Exists is false.
        if (info.LinkTarget is not null)
        {
            var destination = info.ResolveLinkTarget(returnFinalTarget: true)
                ?? throw new IOException("The selected path contains a symbolic link that cannot be resolved.");
            LocalCheckoutPaths.EnsureWithinRoot(root, destination.FullName);
        }
        else if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The selected path contains an unsupported reparse point. Open its real checkout folder instead.");
    }

    private static string ExistingDirectory(string root, string path)
    {
        while (!Directory.Exists(path))
        {
            path = Path.GetDirectoryName(path) ?? root;
            LocalCheckoutPaths.EnsureWithinRoot(root, path);
        }
        return path;
    }

    private static ProcessStartInfo Explorer() => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
    {
        UseShellExecute = false,
        CreateNoWindow = true
    };

    private static string ResolveCursorExecutable(string executable)
    {
        string value = executable.Trim();
        if (!value.Equals("cursor", StringComparison.OrdinalIgnoreCase) && !value.Equals("cursor.exe", StringComparison.OrdinalIgnoreCase))
        {
            if (!Path.IsPathFullyQualified(value) || !Path.GetExtension(value).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Set the Cursor executable to 'cursor' or the full path to Cursor.exe, without command-line arguments.", nameof(executable));
            if (!File.Exists(value)) throw new FileNotFoundException("The configured Cursor executable was not found. Update its path in Settings.", value);
            return Path.GetFullPath(value);
        }

        List<string> candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "cursor", "Cursor.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "cursor", "Cursor.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "cursor", "Cursor.exe")
        ];
        foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string directory = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            candidates.Add(Path.Combine(directory, "Cursor.exe"));
            // Cursor's PATH entry normally points to its CLI wrapper. Resolve the desktop
            // executable next to that installation; never execute a .cmd through a shell.
            if (File.Exists(Path.Combine(directory, "cursor.cmd")))
                candidates.Add(Path.GetFullPath(Path.Combine(directory, "..", "..", "..", "Cursor.exe")));
        }
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("Cursor was not found. Install Cursor or set the full path to Cursor.exe in Settings.");
    }

    private static void Start(ProcessStartInfo start)
    {
        using var process = Process.Start(start);
        if (process is null && !start.UseShellExecute)
            throw new InvalidOperationException("Windows could not open the requested application.");
    }
}
