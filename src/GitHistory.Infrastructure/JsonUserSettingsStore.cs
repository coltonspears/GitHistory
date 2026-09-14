using System.Text.Json;
using System.Text.Json.Serialization;
using GitHistory.Core.Models;
using GitHistory.Core.Services;

namespace GitHistory.Infrastructure;

public sealed class JsonUserSettingsStore(string dataDirectory) : IUserSettingsStore, IDisposable
{
    private readonly string _path = Path.Combine(dataDirectory, "settings.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<UserSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return new();
            await using var stream = File.OpenRead(_path);
            try
            {
                return await JsonSerializer.DeserializeAsync(stream, InfrastructureJsonContext.Default.UserSettings, cancellationToken).ConfigureAwait(false) ?? new();
            }
            catch (JsonException) { return new(); }
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, settings, InfrastructureJsonContext.Default.UserSettings, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}

[JsonSerializable(typeof(UserSettings))]
[JsonSerializable(typeof(RepositoryInfo))]
internal sealed partial class InfrastructureJsonContext : JsonSerializerContext;
