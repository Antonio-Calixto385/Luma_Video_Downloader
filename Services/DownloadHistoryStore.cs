using System.Text.Json;
using MP3LocalDownloader.Models;

namespace MP3LocalDownloader.Services;

public sealed record DownloadHistoryEntry(
    string Title,
    string FilePath,
    DownloadFormat Format,
    int BitrateKbps,
    DateTimeOffset CompletedAt,
    int? VideoHeight = null);

public sealed class DownloadHistoryStore
{
    private const int MaximumEntries = 2000;
    private readonly string _historyPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public DownloadHistoryStore(string localApplicationData)
    {
        var appData = Path.Combine(localApplicationData, "MP3LocalDownloader");
        Directory.CreateDirectory(appData);
        _historyPath = Path.Combine(appData, "download-history.json");
    }

    public async Task<IReadOnlyList<DownloadHistoryEntry>> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_historyPath))
                return [];
            var json = await File.ReadAllTextAsync(_historyPath, cancellationToken);
            return JsonSerializer.Deserialize<List<DownloadHistoryEntry>>(json, JsonOptions)?
                .OrderByDescending(entry => entry.CompletedAt)
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
            if (File.Exists(_historyPath))
                File.Delete(_historyPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddAsync(IEnumerable<DownloadResult> results, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var entries = new List<DownloadHistoryEntry>();
            if (File.Exists(_historyPath))
            {
                try
                {
                    var existingJson = await File.ReadAllTextAsync(_historyPath, cancellationToken);
                    entries = JsonSerializer.Deserialize<List<DownloadHistoryEntry>>(existingJson, JsonOptions) ?? [];
                }
                catch (JsonException) { }
            }

            entries.InsertRange(0, results.Select(result => new DownloadHistoryEntry(
                result.Title,
                result.FilePath,
                result.Format,
                result.BitrateKbps,
                result.CompletedAt,
                result.VideoHeight)));
            var tempPath = _historyPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(entries.Take(MaximumEntries), JsonOptions), cancellationToken);
                File.Move(tempPath, _historyPath, overwrite: true);
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
