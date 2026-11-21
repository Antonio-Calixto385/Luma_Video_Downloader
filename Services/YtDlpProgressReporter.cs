using System.Globalization;
using System.Text.RegularExpressions;
using MP3LocalDownloader.Models;

namespace MP3LocalDownloader.Services;

internal sealed class YtDlpProgressReporter(
    IProgress<DownloadStatus> progress,
    Uri source,
    DownloadFormat format)
{
    private static readonly Regex PercentRegex = new(@"\[download\]\s+(\d+(?:\.\d+)?)%", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ItemRegex = new(@"Downloading item\s+(\d+)\s+of\s+(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DestinationRegex = new(@"^\[(?:download|ExtractAudio)\]\s+Destination:\s*(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly object _gate = new();
    private readonly List<DownloadFailure> _failures = [];
    private readonly HashSet<int> _failedItems = [];
    private readonly HashSet<int> _completedItems = [];
    private int _currentItem;
    private int? _totalItems;
    private string? _currentTitle;
    private int _loadedItems;

    public IReadOnlyList<DownloadFailure> Failures
    {
        get
        {
            lock (_gate)
                return _failures.ToArray();
        }
    }

    public void ReportLine(string line)
    {
        lock (_gate)
        {
            var itemMatch = ItemRegex.Match(line);
            if (itemMatch.Success)
            {
                var nextItem = int.Parse(itemMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                if (_currentItem > 0 && nextItem > _currentItem && !_failedItems.Contains(_currentItem))
                {
                    _completedItems.Add(_currentItem);
                    _loadedItems = _completedItems.Count;
                }
                _currentItem = nextItem;
                _totalItems = int.Parse(itemMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                ReportCurrentItem();
            }

            var destinationMatch = DestinationRegex.Match(line);
            if (destinationMatch.Success)
            {
                _currentTitle = Path.GetFileNameWithoutExtension(destinationMatch.Groups[1].Value.Trim().Trim('"'));
                ReportCurrentItem();
            }

            if (line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            {
                if (_currentItem > 0)
                {
                    _failedItems.Add(_currentItem);
                    _completedItems.Remove(_currentItem);
                    _loadedItems = _completedItems.Count;
                }
                var reason = DownloadService.CleanError(line);
                var title = CurrentTitle();
                if (!_failures.Any(failure =>
                        string.Equals(failure.Title, title, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(failure.Reason, reason, StringComparison.OrdinalIgnoreCase)))
                {
                    _failures.Add(new DownloadFailure(title, reason, format, DateTimeOffset.Now));
                }
            }

            var percentMatch = PercentRegex.Match(line);
            if (percentMatch.Success &&
                double.TryParse(percentMatch.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var percent))
            {
                var boundedPercent = Math.Clamp((int)percent, 0, 99);
                if (_totalItems is { } total && _currentItem > 0)
                {
                    var overallPercent = (int)Math.Clamp(
                        ((_currentItem - 1d + boundedPercent / 100d) / total) * 100,
                        0,
                        99);
                    progress.Report(new DownloadStatus(
                        DownloadStage.Downloading,
                        overallPercent,
                        $"Baixando item {_currentItem}/{total} — {CurrentTitle()} ({boundedPercent}%). " +
                        $"Itens carregados: {_loadedItems}/{total}."));
                }
                else
                {
                    progress.Report(new DownloadStatus(
                        DownloadStage.Downloading,
                        boundedPercent,
                        $"Baixando mídia ({boundedPercent}%)…"));
                }
            }

            if (line.Contains("ExtractAudio", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("[ffmpeg]", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Post-process", StringComparison.OrdinalIgnoreCase))
            {
                var message = _totalItems is { } total
                    ? $"Convertendo item {_currentItem}/{total} — {CurrentTitle()}…"
                    : $"Convertendo {CurrentTitle()}…";
                progress.Report(new DownloadStatus(DownloadStage.Converting, Message: message));
            }
        }
    }

    public void ReportLoadedItems(int loadedItems)
    {
        lock (_gate)
        {
            _loadedItems = loadedItems;
            var message = _totalItems is { } total
                ? $"Download recebido: {loadedItems}/{total} itens. Preparando a conversão…"
                : $"Download recebido: {loadedItems} item(ns). Preparando a conversão…";
            progress.Report(new DownloadStatus(DownloadStage.Converting, Message: message));
        }
    }

    private void ReportCurrentItem()
    {
        if (_currentItem <= 0 || _totalItems is null)
            return;

        var stage = format == DownloadFormat.Mp3 ? DownloadStage.Converting : DownloadStage.Downloading;
        var process = format == DownloadFormat.Mp3 ? "Preparando áudio" : "Baixando vídeo";
        progress.Report(new DownloadStatus(
            stage,
            Message: $"{process} {_currentItem}/{_totalItems} — {CurrentTitle()}… " +
                     $"Itens carregados: {_loadedItems}/{_totalItems}."));
    }

    private string CurrentTitle() =>
        string.IsNullOrWhiteSpace(_currentTitle)
            ? _currentItem > 0 ? $"item {_currentItem}" : $"mídia de {source.Host}"
            : _currentTitle;
}
