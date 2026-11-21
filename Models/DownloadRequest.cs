namespace MP3LocalDownloader.Models;

public enum DownloadFormat
{
    Mp3,
    Mp4
}

public sealed record DownloadRequest(
    Uri Url,
    string Destination,
    DownloadFormat Format,
    int BitrateKbps,
    int VideoHeight = 720);

public sealed record DownloadResult(
    string Title,
    string FilePath,
    DownloadFormat Format,
    int BitrateKbps,
    DateTimeOffset CompletedAt,
    int? VideoHeight = null);

public sealed record DownloadFailure(
    string Title,
    string Reason,
    DownloadFormat Format,
    DateTimeOffset FailedAt);

public sealed record DownloadBatchResult(
    IReadOnlyList<DownloadResult> Files,
    bool WasPartial,
    IReadOnlyList<DownloadFailure> Failures);
