using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MP3LocalDownloader.Setup;

public sealed record InstallProgress(int Percent, string Message);

public sealed class InstallerService
{
    private const string EmbeddedPayloadName = "MP3LocalDownloader.Setup.MP3LocalDownloader-AppPayload.zip";
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly Regex HashRegex = new(@"(?i)(?:sha256:\s*)?([0-9a-f]{64})", RegexOptions.Compiled);

    public static void ValidateEmbeddedApplicationPayload()
    {
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedPayloadName)
            ?? throw new InvalidOperationException("O instalador não contém o pacote do aplicativo.");
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        if (!archive.Entries.Any(entry => string.Equals(Path.GetFileName(entry.FullName), "LumaVideoDownloader.exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("O pacote autocontido não contém o executável do aplicativo.");
    }

    public async Task<string> InstallAsync(
        string installationDirectory,
        IProgress<InstallProgress> progress,
        CancellationToken cancellationToken)
    {
        var target = Path.GetFullPath(installationDirectory);
        if (Directory.Exists(target) || File.Exists(target))
            throw new InvalidOperationException("A pasta escolhida já existe. Escolha uma pasta nova para instalar.");

        var parent = Path.GetDirectoryName(target) ?? throw new InvalidOperationException("Escolha uma pasta de instalação válida.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".mp3local-install-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        try
        {
            progress.Report(new InstallProgress(3, "Preparando o aplicativo autocontido…"));
            ExtractEmbeddedApplication(staging);
            var toolsDirectory = Path.Combine(staging, "tools");
            Directory.CreateDirectory(toolsDirectory);

            var packagesDirectory = Path.Combine(staging, ".packages");
            Directory.CreateDirectory(packagesDirectory);

            progress.Report(new InstallProgress(10, "Localizando a versão oficial mais recente do yt-dlp…"));
            var ytDlpRelease = await GetLatestAssetAsync("yt-dlp", "yt-dlp", "yt-dlp.exe", cancellationToken);
            var ytDlpArchivePath = Path.Combine(packagesDirectory, "yt-dlp.exe");
            await DownloadVerifiedAssetAsync(ytDlpRelease, ytDlpArchivePath, "yt-dlp.exe", 12, 27, progress, cancellationToken);
            File.Move(ytDlpArchivePath, Path.Combine(toolsDirectory, "yt-dlp.exe"));

            progress.Report(new InstallProgress(30, "Baixando FFmpeg e ffprobe…"));
            var ffmpegZip = Path.Combine(packagesDirectory, "ffmpeg-release-essentials.zip");
            const string ffmpegUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
            const string ffmpegChecksumUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip.sha256";
            await DownloadFileAsync(ffmpegUrl, ffmpegZip, 30, 57, "Baixando FFmpeg…", progress, cancellationToken);
            var ffmpegChecksum = await DownloadTextAsync(ffmpegChecksumUrl, cancellationToken);
            var expectedFfmpegHash = ParseHash(ffmpegChecksum);
            await VerifySha256Async(ffmpegZip, expectedFfmpegHash, "FFmpeg", cancellationToken);
            ExtractFfmpegTools(ffmpegZip, toolsDirectory);

            progress.Report(new InstallProgress(62, "Baixando o runtime JavaScript Deno para suporte ao YouTube…"));
            var denoRelease = await GetLatestAssetAsync("denoland", "deno", "deno-x86_64-pc-windows-msvc.zip", cancellationToken);
            var denoZip = Path.Combine(packagesDirectory, "deno-x86_64-pc-windows-msvc.zip");
            await DownloadVerifiedAssetAsync(denoRelease, denoZip, "deno-x86_64-pc-windows-msvc.zip", 64, 85, progress, cancellationToken);
            ExtractDeno(denoZip, toolsDirectory);

            progress.Report(new InstallProgress(90, "Gravando avisos de licenças…"));
            await File.WriteAllTextAsync(Path.Combine(staging, "THIRD_PARTY_NOTICES.md"), BuildThirdPartyNotices(), cancellationToken);
            Directory.Delete(packagesDirectory, recursive: true);

            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new InstallProgress(96, "Finalizando a instalação…"));
            Directory.Move(staging, target);
            progress.Report(new InstallProgress(100, "Instalação concluída."));
            return target;
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LumaVideoDownloaderSetup", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static void ExtractEmbeddedApplication(string targetDirectory)
    {
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedPayloadName)
            ?? throw new InvalidOperationException("O instalador foi compilado sem o pacote do aplicativo. Recompile pelo script de publicação.");
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        ExtractArchiveSafely(archive, targetDirectory, entry => true);
    }

    private static async Task<GithubAsset> GetLatestAssetAsync(
        string owner,
        string repository,
        string assetName,
        CancellationToken cancellationToken)
    {
        var apiUrl = $"https://api.github.com/repos/{owner}/{repository}/releases/latest";
        using var response = await Http.GetAsync(apiUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Não foi possível consultar a última versão de {repository} no GitHub (HTTP {(int)response.StatusCode}).");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var assets = json.RootElement.GetProperty("assets").EnumerateArray().ToArray();
        var assetElement = assets.FirstOrDefault(asset => string.Equals(
            asset.GetProperty("name").GetString(), assetName, StringComparison.OrdinalIgnoreCase));
        if (assetElement.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException($"A versão mais recente de {repository} não contém o arquivo esperado {assetName}.");

        var downloadUrl = new Uri(assetElement.GetProperty("browser_download_url").GetString()!);
        if (downloadUrl.Scheme != Uri.UriSchemeHttps || downloadUrl.Host != "github.com")
            throw new InvalidOperationException($"O endereço de download publicado por {repository} não é confiável.");

        var digest = assetElement.TryGetProperty("digest", out var digestElement)
            ? digestElement.GetString()
            : null;
        string? checksumUrl = null;
        if (string.IsNullOrWhiteSpace(digest))
        {
            var checksumAsset = assets.FirstOrDefault(asset =>
            {
                var name = asset.GetProperty("name").GetString() ?? string.Empty;
                return string.Equals(name, assetName + ".sha256sum", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(name, assetName + ".sha256", StringComparison.OrdinalIgnoreCase) ||
                       (repository == "yt-dlp" && string.Equals(name, "SHA2-256SUMS", StringComparison.OrdinalIgnoreCase));
            });
            if (checksumAsset.ValueKind != JsonValueKind.Undefined)
                checksumUrl = checksumAsset.GetProperty("browser_download_url").GetString();
        }

        return new GithubAsset(repository, assetName, downloadUrl, digest, checksumUrl);
    }

    private static async Task DownloadVerifiedAssetAsync(
        GithubAsset asset,
        string destination,
        string expectedFileName,
        int startPercent,
        int endPercent,
        IProgress<InstallProgress> progress,
        CancellationToken cancellationToken)
    {
        await DownloadFileAsync(asset.DownloadUrl.AbsoluteUri, destination, startPercent, endPercent,
            $"Baixando {expectedFileName}…", progress, cancellationToken);

        string expectedHash;
        if (!string.IsNullOrWhiteSpace(asset.Digest))
            expectedHash = ParseHash(asset.Digest);
        else if (!string.IsNullOrWhiteSpace(asset.ChecksumUrl))
        {
            var checksum = await DownloadTextAsync(asset.ChecksumUrl, cancellationToken);
            expectedHash = ParseNamedHash(checksum, expectedFileName);
        }
        else
        {
            throw new InvalidOperationException($"Não há hash SHA-256 publicado para validar {expectedFileName}.");
        }

        await VerifySha256Async(destination, expectedHash, expectedFileName, cancellationToken);
    }

    private static async Task DownloadFileAsync(
        string url,
        string destination,
        int startPercent,
        int endPercent,
        string label,
        IProgress<InstallProgress> progress,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("O instalador recusou um endereço sem HTTPS.");
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Falha ao baixar um componente (HTTP {(int)response.StatusCode}).");

        var total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true);
        var buffer = new byte[1024 * 128];
        long downloaded = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            if (total is > 0)
            {
                var ratio = Math.Clamp((double)downloaded / total.Value, 0, 1);
                var percent = startPercent + (int)Math.Round((endPercent - startPercent) * ratio);
                progress.Report(new InstallProgress(percent, label));
            }
        }
        await output.FlushAsync(cancellationToken);
        progress.Report(new InstallProgress(endPercent, label));
    }

    private static async Task<string> DownloadTextAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Não foi possível obter o hash publicado para validar o componente (HTTP {(int)response.StatusCode}).");
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static string ParseNamedHash(string contents, string fileName)
    {
        foreach (var line in contents.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = HashRegex.Match(line);
            if (!match.Success)
                continue;
            var name = line[(match.Index + match.Length)..].Trim().TrimStart('*').Trim();
            if (name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
                return match.Groups[1].Value.ToLowerInvariant();
        }
        throw new InvalidOperationException($"O arquivo de hashes não contém uma entrada SHA-256 para {fileName}.");
    }

    private static string ParseHash(string contents)
    {
        var match = HashRegex.Match(contents);
        if (!match.Success)
            throw new InvalidOperationException("O hash SHA-256 publicado tem um formato inválido.");
        return match.Groups[1].Value.ToLowerInvariant();
    }

    private static async Task VerifySha256Async(string filePath, string expectedHash, string componentName, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        var actualBytes = await SHA256.HashDataAsync(stream, cancellationToken);
        var expectedBytes = Convert.FromHexString(expectedHash);
        if (actualBytes.Length != expectedBytes.Length || !CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes))
            throw new InvalidOperationException($"A validação SHA-256 de {componentName} falhou. O arquivo foi descartado.");
    }

    private static void ExtractFfmpegTools(string archivePath, string targetDirectory)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        ExtractRequiredTool(archive, targetDirectory, "ffmpeg.exe");
        ExtractRequiredTool(archive, targetDirectory, "ffprobe.exe");
    }

    private static void ExtractRequiredTool(ZipArchive archive, string targetDirectory, string fileName)
    {
        var entry = archive.Entries.FirstOrDefault(candidate =>
            string.Equals(Path.GetFileName(candidate.FullName), fileName, StringComparison.OrdinalIgnoreCase) &&
            candidate.FullName.Replace('\\', '/').Contains("/bin/", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            throw new InvalidOperationException($"O pacote do FFmpeg não contém {fileName}.");
        var target = Path.Combine(targetDirectory, fileName);
        using var input = entry.Open();
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    private static void ExtractDeno(string archivePath, string targetDirectory)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.Entries.FirstOrDefault(candidate =>
            string.Equals(Path.GetFileName(candidate.FullName), "deno.exe", StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            throw new InvalidOperationException("O pacote oficial do Deno não contém deno.exe.");
        using var input = entry.Open();
        using var output = new FileStream(Path.Combine(targetDirectory, "deno.exe"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    private static void ExtractArchiveSafely(ZipArchive archive, string targetDirectory, Func<ZipArchiveEntry, bool> include)
    {
        var root = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries.Where(include))
        {
            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(targetDirectory, relative));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("O pacote autocontido contém um caminho inválido.");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }

    private static string BuildThirdPartyNotices() => """
        # Avisos de componentes de terceiros

        Esta instalação baixa os componentes indicados abaixo de seus projetos ou distribuidores oficiais. Consulte os termos completos nas páginas oficiais antes de redistribuir o instalador ou os componentes.

        - **yt-dlp** — Unlicense. Código, licenças de componentes incluídos e versões: https://github.com/yt-dlp/yt-dlp
        - **Deno** — MIT. Código e avisos: https://github.com/denoland/deno
        - **FFmpeg Essentials (gyan.dev)** — distribuição Windows estática identificada pelo mantenedor como GPLv3. Bibliotecas incluídas incluem libx264 e libmp3lame. Página da distribuição e builds: https://www.gyan.dev/ffmpeg/builds/ . Código-fonte do FFmpeg: https://ffmpeg.org/download.html

        A aplicação não contorna DRM, login, conteúdo privado ou controles de acesso da origem. Confirme que você tem autorização para baixar o conteúdo.
        """;

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record GithubAsset(string Repository, string Name, Uri DownloadUrl, string? Digest, string? ChecksumUrl);
}
