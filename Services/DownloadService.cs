using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using MP3LocalDownloader.Models;

namespace MP3LocalDownloader.Services;

public sealed class DownloadService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".webm", ".mp4", ".m4v", ".mov", ".avi", ".flv", ".ts", ".mpg", ".mpeg", ".3gp"
    };

    public async Task<DownloadBatchResult> DownloadAsync(
        DownloadRequest request,
        ToolPaths tools,
        IProgress<DownloadStatus> progress,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var destination = Path.GetFullPath(request.Destination);
        Directory.CreateDirectory(destination);
        var workDirectory = Path.Combine(destination, $".mp3local-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);

        try
        {
            progress.Report(new DownloadStatus(DownloadStage.Analyzing, Message: "Analisando o link e a playlist…"));
            var messages = new ConcurrentQueue<string>();
            var progressReporter = new YtDlpProgressReporter(progress, request.Url, request.Format);
            var arguments = BuildYtDlpArguments(request, tools);
            var ytDlpResult = await RunProcessAsync(
                tools.YtDlp,
                workDirectory,
                arguments,
                line =>
                {
                    messages.Enqueue(line);
                    progressReporter.ReportLine(line);
                },
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            var failures = progressReporter.Failures.ToList();
            var wasPartial = ytDlpResult.ExitCode != 0 || failures.Count > 0;
            if (wasPartial && failures.Count == 0)
                failures.Add(new DownloadFailure("Playlist ou mídia", MakeYtDlpFailure(ytDlpResult, messages).Message, request.Format, DateTimeOffset.Now));

            IReadOnlyList<string> candidates;
            if (request.Format == DownloadFormat.Mp3)
            {
                candidates = Directory.EnumerateFiles(workDirectory, "*.mp3", SearchOption.AllDirectories)
                    .Where(IsCompletedFile)
                    .ToArray();
                progressReporter.ReportLoadedItems(candidates.Count);
                if (candidates.Count == 0)
                {
                    if (failures.Count > 0)
                    {
                        var failureMessage = ytDlpResult.ExitCode != 0
                            ? MakeYtDlpFailure(ytDlpResult, messages).Message
                            : "Nenhum item da playlist pôde ser baixado.";
                        throw new DownloadFailedException(failureMessage, failures);
                    }
                    if (ytDlpResult.ExitCode != 0)
                        throw MakeYtDlpFailure(ytDlpResult, messages);
                    throw new DownloadFailedException("O processo terminou, mas não foi possível confirmar arquivos MP3 concluídos.");
                }
            }
            else
            {
                var sources = Directory.EnumerateFiles(workDirectory, "*", SearchOption.AllDirectories)
                    .Where(path => VideoExtensions.Contains(Path.GetExtension(path)) && IsCompletedFile(path))
                    .ToArray();
                progressReporter.ReportLoadedItems(sources.Length);
                if (sources.Length == 0)
                {
                    if (failures.Count > 0)
                    {
                        var failureMessage = ytDlpResult.ExitCode != 0
                            ? MakeYtDlpFailure(ytDlpResult, messages).Message
                            : "Nenhum item da playlist pôde ser baixado.";
                        throw new DownloadFailedException(failureMessage, failures);
                    }
                    if (ytDlpResult.ExitCode != 0)
                        throw MakeYtDlpFailure(ytDlpResult, messages);
                    throw new DownloadFailedException("O processo terminou, mas não foi possível localizar vídeos para converter em MP4.");
                }

                var convertedDirectory = Path.Combine(workDirectory, "converted");
                Directory.CreateDirectory(convertedDirectory);
                var converted = new List<string>();
                for (var index = 0; index < sources.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var source = sources[index];
                    var relativePath = Path.GetRelativePath(workDirectory, source);
                    var convertedPath = Path.Combine(convertedDirectory, Path.ChangeExtension(relativePath, ".mp4"));
                    Directory.CreateDirectory(Path.GetDirectoryName(convertedPath)!);

                    try
                    {
                        var probe = await RunProcessAsync(
                            tools.Ffprobe,
                            workDirectory,
                            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_type", "-of", "default=noprint_wrappers=1:nokey=1", source],
                            null,
                            cancellationToken);
                        if (probe.ExitCode != 0 || !probe.StandardOutput.Contains("video", StringComparison.OrdinalIgnoreCase))
                            throw new DownloadFailedException("Um item da playlist não contém uma faixa de vídeo compatível com MP4.");

                        progress.Report(new DownloadStatus(
                            DownloadStage.Converting,
                            (int)Math.Clamp(index * 100d / sources.Length, 0, 99),
                            Message: $"Convertendo vídeo {index + 1}/{sources.Length} — {Path.GetFileNameWithoutExtension(source)} " +
                                     $"({FormatResolution(request.VideoHeight)}). Concluídos: {converted.Count}/{sources.Length}."));

                        var ffmpegResult = await RunProcessAsync(
                            tools.Ffmpeg,
                            workDirectory,
                            [
                                "-hide_banner", "-loglevel", "error", "-nostdin", "-n",
                                "-i", source,
                                "-map", "0:v:0", "-map", "0:a:0?",
                                "-vf", $"scale=-2:min(ih\\,{request.VideoHeight})",
                                "-c:v", "libx264",
                                "-preset", "veryfast", "-pix_fmt", "yuv420p",
                                "-c:a", "aac", "-b:a", "192k",
                                "-movflags", "+faststart", "-f", "mp4", convertedPath
                            ],
                            line => messages.Enqueue(line),
                            cancellationToken);
                        if (ffmpegResult.ExitCode != 0 || !IsCompletedFile(convertedPath))
                        {
                            var detail = ffmpegResult.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                            throw new DownloadFailedException(string.IsNullOrWhiteSpace(detail)
                                ? "Não foi possível converter um item para MP4."
                                : CleanError(detail));
                        }
                        converted.Add(convertedPath);
                    }
                    catch (DownloadFailedException exception)
                    {
                        wasPartial = true;
                        failures.Add(new DownloadFailure(
                            Path.GetFileNameWithoutExtension(source),
                            exception.Message,
                            request.Format,
                            DateTimeOffset.Now));
                    }
                }
                candidates = converted;
                if (candidates.Count == 0)
                {
                    if (ytDlpResult.ExitCode != 0)
                        throw MakeYtDlpFailure(ytDlpResult, messages);
                    if (failures.Count > 0)
                    {
                        var details = string.Join(" | ", failures.Select(failure => $"{failure.Title}: {failure.Reason}"));
                        throw new DownloadFailedException($"Nenhum item pôde ser convertido. {details}", failures);
                    }
                    throw new DownloadFailedException("Nenhum item da playlist pôde ser convertido em MP4.");
                }
            }

            var candidateRoot = request.Format == DownloadFormat.Mp4
                ? Path.Combine(workDirectory, "converted")
                : workDirectory;
            var results = CommitFiles(candidates, candidateRoot, destination, request, cancellationToken);
            if (results.Count == 0)
                throw new DownloadFailedException("Todos os arquivos já existem no destino. Nenhum arquivo foi substituído.");

            var message = wasPartial
                ? $"Concluído parcialmente: {results.Count} arquivos. Alguns itens não puderam ser processados."
                : results.Count == 1 ? "Download concluído." : $"Concluído: {results.Count} arquivos salvos.";
            progress.Report(new DownloadStatus(DownloadStage.Completed, 100, message));
            return new DownloadBatchResult(results, wasPartial, failures);
        }
        catch (OperationCanceledException)
        {
            progress.Report(new DownloadStatus(DownloadStage.Cancelled, Message: "Download cancelado."));
            throw;
        }
        finally
        {
            await TryDeleteDirectoryAsync(workDirectory);
        }
    }

    private static void ValidateRequest(DownloadRequest request)
    {
        if (!request.Url.IsAbsoluteUri || (request.Url.Scheme != Uri.UriSchemeHttp && request.Url.Scheme != Uri.UriSchemeHttps))
            throw new DownloadFailedException("Informe uma URL HTTP ou HTTPS válida.");

        if (request.Format == DownloadFormat.Mp3 && request.BitrateKbps is < 64 or > 320)
            throw new DownloadFailedException("O bitrate MP3 deve ficar entre 64 e 320 kbps.");
        if (request.Format == DownloadFormat.Mp4 && request.VideoHeight is not (320 or 720 or 1080 or 2160))
            throw new DownloadFailedException("Selecione uma resolução MP4 válida: 320p, 720p, 1080p ou 4K.");
    }

    private static string FormatResolution(int height) => height == 2160 ? "4K (2160p)" : $"{height}p";

    private static string[] BuildYtDlpArguments(DownloadRequest request, ToolPaths tools)
    {
        var arguments = new List<string>
        {
            "--ignore-config",
            "--yes-playlist",
            "--windows-filenames",
            "--newline",
            "--no-color",
            "--ignore-errors",
            "--no-overwrites",
            "--socket-timeout", "30",
            "--retries", "3",
            "--fragment-retries", "3",
            "--extractor-retries", "3",
            "--ffmpeg-location", tools.Directory,
            "--js-runtimes", $"deno:{tools.Deno}",
            "--output", "%(playlist|Downloads)s/%(playlist_index&{} - |)s%(title)s [%(id)s].%(ext)s",
            "--print", "after_move:filepath"
        };

        if (request.Format == DownloadFormat.Mp3)
        {
            arguments.AddRange(["--format", "bestaudio/best", "--extract-audio", "--audio-format", "mp3", "--audio-quality", $"{request.BitrateKbps}K"]);
        }
        else
        {
            arguments.AddRange(["--format", "bestvideo+bestaudio/best", "--merge-output-format", "mkv"]);
        }
        arguments.Add(request.Url.AbsoluteUri);
        return [.. arguments];
    }

    private static IReadOnlyList<DownloadResult> CommitFiles(
        IReadOnlyList<string> candidates,
        string workDirectory,
        string destination,
        DownloadRequest request,
        CancellationToken cancellationToken)
    {
        var results = new List<DownloadResult>();
        var committedPaths = new List<string>();
        try
        {
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(workDirectory, candidate);
                var finalPath = Path.GetFullPath(Path.Combine(destination, relativePath));
                if (!IsWithinDirectory(finalPath, destination))
                    throw new DownloadFailedException("O caminho retornado pela origem não é seguro para gravar no destino.");

                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                if (File.Exists(finalPath))
                    continue;

                try
                {
                    File.Move(candidate, finalPath, overwrite: false);
                    committedPaths.Add(finalPath);
                    results.Add(new DownloadResult(
                        Path.GetFileNameWithoutExtension(finalPath),
                        finalPath,
                        request.Format,
                        request.BitrateKbps,
                        DateTimeOffset.Now,
                        request.Format == DownloadFormat.Mp4 ? request.VideoHeight : null));
                }
                catch (IOException) when (File.Exists(finalPath))
                {
                    // Outro processo criou o arquivo após a verificação; preserve-o.
                }
            }
            return results;
        }
        catch
        {
            foreach (var path in committedPaths)
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string executable,
        string workingDirectory,
        IEnumerable<string> arguments,
        Action<string>? onLine,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        var stdout = new ConcurrentQueue<string>();
        var stderr = new ConcurrentQueue<string>();
        try
        {
            if (!process.Start())
                throw new DownloadFailedException($"Não foi possível iniciar {Path.GetFileName(executable)}.");
            var stdoutTask = ReadLinesAsync(process.StandardOutput, stdout, onLine);
            var stderrTask = ReadLinesAsync(process.StandardError, stderr, onLine);
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdoutTask, stderrTask);
            return new ProcessResult(process.ExitCode, string.Join(Environment.NewLine, stdout), string.Join(Environment.NewLine, stderr));
        }
        catch
        {
            TryKill(process);
            await WaitForExitQuietlyAsync(process);
            throw;
        }
    }

    private static async Task ReadLinesAsync(StreamReader reader, ConcurrentQueue<string> lines, Action<string>? onLine)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            lines.Enqueue(line);
            onLine?.Invoke(line);
        }
    }

    private static bool IsCompletedFile(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length > 0; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool IsWithinDirectory(string filePath, string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return filePath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    internal static string CleanError(string message)
    {
        if (message.Contains("Failed to establish a new connection", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("WinError 10061", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return "Não foi possível conectar à origem. Verifique sua conexão e se o link está acessível, depois tente novamente.";
        if (message.Contains("HTTP Error 404", StringComparison.OrdinalIgnoreCase))
            return "A mídia não foi encontrada (HTTP 404). Verifique se o link está correto e disponível.";
        if (message.Contains("HTTP Error 429", StringComparison.OrdinalIgnoreCase))
            return "A origem limitou as requisições (HTTP 429). Aguarde um pouco e tente novamente.";
        if (message.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase))
            return "A origem recusou o acesso (HTTP 403). O aplicativo não tentará contornar essa restrição.";

        var cleaned = message.Replace("\0", string.Empty).Trim();
        if (cleaned.StartsWith("ERROR: ", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned[7..];
        return cleaned.Length <= 500 ? cleaned : cleaned[..500] + "…";
    }

    private static DownloadFailedException MakeYtDlpFailure(ProcessResult result, ConcurrentQueue<string> messages)
    {
        var detail = messages.LastOrDefault(line => line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            ?? messages.LastOrDefault(line => !string.IsNullOrWhiteSpace(line))
            ?? result.StandardError;
        return new DownloadFailedException(string.IsNullOrWhiteSpace(detail)
            ? $"O processamento terminou com código {result.ExitCode}."
            : CleanError(detail));
    }

    private static async Task TryDeleteDirectoryAsync(string path)
    {
        if (!Directory.Exists(path))
            return;

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException)
            {
                await Task.Delay(100);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(100);
            }
        }

        if (Directory.Exists(path))
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static async Task WaitForExitQuietlyAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (InvalidOperationException) { }
        catch (TimeoutException) { }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}

public sealed class DownloadFailedException(string message, IReadOnlyList<DownloadFailure>? failures = null) : Exception(message)
{
    public IReadOnlyList<DownloadFailure> Failures { get; } = failures ?? [];
}
