using System.IO.Compression;
using System.Security.Cryptography;

namespace MP3LocalDownloader.Services;

public static class PortableToolsBundle
{
    private const string ResourceName = "MP3LocalDownloader.PortableTools.zip";
    private static readonly string[] RequiredTools = ["deno.exe", "ffmpeg.exe", "ffprobe.exe", "yt-dlp.exe"];

    public static async Task<string> PrepareAsync(CancellationToken cancellationToken = default)
    {
        using var resource = typeof(PortableToolsBundle).Assembly.GetManifestResourceStream(ResourceName);
        if (resource is null)
            return ResolveDevelopmentApplicationDirectory(
                AppContext.BaseDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) ?? AppContext.BaseDirectory;

        var hash = Convert.ToHexString(await SHA256.HashDataAsync(resource, cancellationToken));
        var applicationDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MP3LocalDownloader",
            "portable",
            hash);
        var toolsDirectory = Path.Combine(applicationDirectory, "tools");
        if (ContainsRequiredTools(toolsDirectory))
            return applicationDirectory;

        var parentDirectory = Path.GetDirectoryName(applicationDirectory)!;
        Directory.CreateDirectory(parentDirectory);
        var stagingDirectory = Path.Combine(parentDirectory, $".{hash}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            resource.Position = 0;
            using var archive = new ZipArchive(resource, ZipArchiveMode.Read);
            var entries = archive.Entries
                .Where(entry => !string.IsNullOrEmpty(entry.Name))
                .ToArray();
            if (entries.Length != RequiredTools.Length ||
                archive.Entries.Count != RequiredTools.Length ||
                entries.Any(entry => !string.Equals(entry.FullName, entry.Name, StringComparison.Ordinal)) ||
                entries.Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != RequiredTools.Length ||
                RequiredTools.Any(name => !entries.Any(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))))
            {
                throw new InvalidDataException("O pacote portátil não contém exatamente as quatro ferramentas esperadas.");
            }

            var stagingToolsDirectory = Path.Combine(stagingDirectory, "tools");
            Directory.CreateDirectory(stagingToolsDirectory);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.Combine(stagingToolsDirectory, entry.Name);
                await using var input = entry.Open();
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await input.CopyToAsync(output, cancellationToken);
            }

            if (!ContainsRequiredTools(stagingToolsDirectory))
                throw new InvalidDataException("Não foi possível extrair todas as ferramentas do pacote portátil.");

            try
            {
                Directory.Move(stagingDirectory, applicationDirectory);
            }
            catch (IOException) when (ContainsRequiredTools(toolsDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }

            return applicationDirectory;
        }
        catch
        {
            if (Directory.Exists(stagingDirectory))
                Directory.Delete(stagingDirectory, recursive: true);
            throw;
        }
    }

    public static void WriteThirdPartyNotices(string applicationDirectory)
    {
        using var resource = typeof(PortableToolsBundle).Assembly.GetManifestResourceStream("MP3LocalDownloader.THIRD_PARTY_NOTICES.md");
        using var bundle = typeof(PortableToolsBundle).Assembly.GetManifestResourceStream(ResourceName);
        if (resource is null || bundle is null)
            return;

        var noticesPath = Path.Combine(applicationDirectory, "THIRD_PARTY_NOTICES.md");
        if (File.Exists(noticesPath))
            return;

        using var reader = new StreamReader(resource);
        File.WriteAllText(noticesPath, reader.ReadToEnd());
    }

    private static bool ContainsRequiredTools(string directory) =>
        RequiredTools.All(name => File.Exists(Path.Combine(directory, name)));

    internal static string? ResolveDevelopmentApplicationDirectory(
        string applicationBaseDirectory,
        string localApplicationDataDirectory)
    {
        var current = new DirectoryInfo(applicationBaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MP3LocalDownloader.csproj")))
            {
                var developmentToolsDirectory = Path.Combine(current.FullName, "tools");
                if (ContainsRequiredTools(developmentToolsDirectory))
                    return current.FullName;

                var cachedApplication = FindCachedApplicationDirectory(localApplicationDataDirectory);
                if (cachedApplication is not null)
                    return cachedApplication;

                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string? FindCachedApplicationDirectory(string localApplicationDataDirectory)
    {
        var cacheRoot = Path.Combine(
            localApplicationDataDirectory,
            "MP3LocalDownloader",
            "portable");
        if (!Directory.Exists(cacheRoot))
            return null;

        return Directory.EnumerateDirectories(cacheRoot)
            .Select(path => new DirectoryInfo(path))
            .OrderByDescending(directory => directory.LastWriteTimeUtc)
            .FirstOrDefault(directory => ContainsRequiredTools(Path.Combine(directory.FullName, "tools")))
            ?.FullName;
    }
}
