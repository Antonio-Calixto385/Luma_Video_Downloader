namespace MP3LocalDownloader.Models;

public enum DownloadStage
{
    Waiting,
    Analyzing,
    Downloading,
    Converting,
    Completed,
    Error,
    Cancelled
}

public sealed record DownloadStatus(DownloadStage Stage, int? Percent = null, string? Message = null);
