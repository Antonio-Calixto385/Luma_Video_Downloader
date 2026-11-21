using System.Text.Json;
using MP3LocalDownloader.Models;

namespace MP3LocalDownloader.Services;

public sealed class DownloadFailureStore
{
    private const int MaximumEntries = 2000;
    private readonly string _failurePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public DownloadFailureStore(string localApplicationData)
    {
        var appData = Path.Combine(localApplicationData, "MP3LocalDownloader");
        Directory.CreateDirectory(appData);
        _failurePath = Path.Combine(appData, "download-failures.json");
    }

    public async Task<IReadOnlyList<DownloadFailure>> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_failurePath))
                return [];
            var json = await File.ReadAllTextAsync(_failurePath, cancellationToken);
            return JsonSerializer.Deserialize<List<DownloadFailure>>(json, JsonOptions)?
                .OrderByDescending(entry => entry.FailedAt)
                .ToArray() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_failurePath))
                File.Delete(_failurePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddAsync(IEnumerable<DownloadFailure> failures, CancellationToken cancellationToken = default)
    {
        var newFailures = failures.ToArray();
        if (newFailures.Length == 0)
            return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entries = new List<DownloadFailure>();
            if (File.Exists(_failurePath))
            {
                try
                {
                    var existingJson = await File.ReadAllTextAsync(_failurePath, cancellationToken);
                    entries = JsonSerializer.Deserialize<List<DownloadFailure>>(existingJson, JsonOptions) ?? [];
                }
                catch (JsonException) { }
            }

            entries.InsertRange(0, newFailures);
            var tempPath = _failurePath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(entries.Take(MaximumEntries), JsonOptions), cancellationToken);
                File.Move(tempPath, _failurePath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                catch (IOException) { }
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
