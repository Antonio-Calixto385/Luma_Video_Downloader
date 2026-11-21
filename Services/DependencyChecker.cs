using System.Diagnostics;

namespace MP3LocalDownloader.Services;

public sealed record ToolPaths(string Directory, string YtDlp, string Ffmpeg, string Ffprobe, string Deno);

public static class DependencyChecker
{
    public static async Task<ToolPaths> CheckAsync(string applicationDirectory, CancellationToken cancellationToken)
    {
        var toolsDirectory = Path.Combine(applicationDirectory, "tools");
        var tools = new ToolPaths(
            toolsDirectory,
            Path.Combine(toolsDirectory, "yt-dlp.exe"),
            Path.Combine(toolsDirectory, "ffmpeg.exe"),
            Path.Combine(toolsDirectory, "ffprobe.exe"),
            Path.Combine(toolsDirectory, "deno.exe"));

        var missing = new[] { tools.YtDlp, tools.Ffmpeg, tools.Ffprobe, tools.Deno }
            .Where(path => !File.Exists(path))
            .Select(Path.GetFileName)
            .ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Dependências ausentes na pasta '{toolsDirectory}': {string.Join(", ", missing)}. " +
                "Abra o LumaVideoDownloader.exe portátil para preparar as ferramentas automaticamente, " +
                "ou configure as ferramentas oficiais conforme o README.");
        }

        await CheckExecutableAsync(tools.YtDlp, "--version", cancellationToken);
        await CheckExecutableAsync(tools.Ffmpeg, "-version", cancellationToken);
        await CheckExecutableAsync(tools.Ffprobe, "-version", cancellationToken);
        await CheckExecutableAsync(tools.Deno, "--version", cancellationToken);
        return tools;
    }

    private static async Task CheckExecutableAsync(string executable, string argument, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add(argument);

        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Não foi possível iniciar {Path.GetFileName(executable)}.");

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(outputTask, errorTask);

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"A verificação de {Path.GetFileName(executable)} falhou.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new InvalidOperationException($"A verificação de {Path.GetFileName(executable)} excedeu o tempo limite.");
        }
        catch
        {
            TryKill(process);
            throw;
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
}
