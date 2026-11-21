using System.Diagnostics;
using MP3LocalDownloader.Models;
using MP3LocalDownloader.Services;

namespace MP3LocalDownloader.SmokeTests;

internal static class Program
{
    private static string _ffmpeg = string.Empty;
    private static string _ffprobe = string.Empty;
    private static string _audioSource = string.Empty;
    private static string _videoSource = string.Empty;

    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--ignore-config", StringComparer.Ordinal))
            return await RunFakeYtDlpAsync(args);
        if (args.Contains("--version", StringComparer.Ordinal) || args.Contains("-version", StringComparer.Ordinal))
        {
            Console.WriteLine("smoke-fake 1.0");
            return 0;
        }

        try
        {
            await RunSmokeTestsAsync();
            Console.WriteLine("Todos os smoke tests passaram.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task RunSmokeTestsAsync()
    {
        var repositoryRoot = FindRepositoryRoot();
        var toolsDirectory = Path.Combine(repositoryRoot, "tools");
        _ffmpeg = Path.Combine(toolsDirectory, "ffmpeg.exe");
        _ffprobe = Path.Combine(toolsDirectory, "ffprobe.exe");
        Assert(File.Exists(_ffmpeg) && File.Exists(_ffprobe), "FFmpeg e ffprobe precisam estar em tools/ para os smoke tests.");

        var testRoot = Path.Combine(repositoryRoot, "artifacts", $"smoke-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        try
        {
            Console.WriteLine("Testando localização de ferramentas na execução local…");
            TestDevelopmentToolResolution(testRoot);

            _audioSource = Path.Combine(testRoot, "audio-source.wav");
            _videoSource = Path.Combine(testRoot, "video-source.mkv");
            await CreateFixturesAsync();
            Environment.SetEnvironmentVariable("MP3LD_TEST_AUDIO_SOURCE", _audioSource);
            Environment.SetEnvironmentVariable("MP3LD_TEST_VIDEO_SOURCE", _videoSource);
            Environment.SetEnvironmentVariable("MP3LD_TEST_FFMPEG", _ffmpeg);

            var destination = Path.Combine(testRoot, "downloads");
            Directory.CreateDirectory(destination);
            var fakeYtDlp = Environment.ProcessPath ?? throw new InvalidOperationException("Não foi possível localizar o executável do smoke test.");
            Console.WriteLine("Testando verificador de dependências e sintaxe de runtime…");
            var mockApplicationDirectory = Path.Combine(testRoot, "mock-app");
            var mockToolsDirectory = Path.Combine(mockApplicationDirectory, "tools");
            Directory.CreateDirectory(mockToolsDirectory);
            foreach (var name in new[] { "yt-dlp.exe", "ffmpeg.exe", "ffprobe.exe", "deno.exe" })
                File.Copy(fakeYtDlp, Path.Combine(mockToolsDirectory, name));
            foreach (var extension in new[] { ".dll", ".deps.json", ".runtimeconfig.json" })
            {
                var fileName = "MP3LocalDownloader.SmokeTests" + extension;
                File.Copy(Path.Combine(AppContext.BaseDirectory, fileName), Path.Combine(mockToolsDirectory, fileName));
            }
            await DependencyChecker.CheckAsync(mockApplicationDirectory, CancellationToken.None);
            await RunToolAsync(Path.Combine(toolsDirectory, "yt-dlp.exe"), ["--ignore-config", "--js-runtimes", $"deno:{fakeYtDlp}", "--version"]);

            var tools = new ToolPaths(toolsDirectory, fakeYtDlp, _ffmpeg, _ffprobe, fakeYtDlp);
            var service = new DownloadService();
            var progress = new InlineProgress<DownloadStatus>();

            Console.WriteLine("Testando playlist MP3 a 256 kbps…");
            var mp3Batch = await service.DownloadAsync(
                new DownloadRequest(new Uri("https://smoke.test/playlist-mp3"), destination, DownloadFormat.Mp3, 256),
                tools, progress, CancellationToken.None);
            var mp3Results = mp3Batch.Files;
            Assert(mp3Results.Count == 2, "A playlist MP3 simulada deveria produzir dois arquivos.");
            Assert(mp3Results.All(result => result.FilePath.Contains("Playlist de teste", StringComparison.Ordinal)), "Os itens MP3 devem ficar na pasta da playlist.");
            var mp3Rate = await ProbeBitrateAsync(_ffprobe, mp3Results[0].FilePath, audio: true);
            Assert(Math.Abs(mp3Rate - 256000) < 20000, $"Bitrate MP3 fora do esperado: {mp3Rate} bps.");
            Assert(progress.Items.Any(status => status.Message?.Contains("item 1/2", StringComparison.OrdinalIgnoreCase) == true),
                "O progresso deve informar o item atual e o total da playlist.");
            Assert(progress.Items.Any(status => status.Message?.Contains("Download recebido: 2/2 itens", StringComparison.OrdinalIgnoreCase) == true),
                "O progresso deve informar quantos itens já foram carregados. Mensagens: " +
                string.Join(" | ", progress.Items.Select(status => status.Message).Where(message => message is not null)));
            Assert(progress.Items.Any(status => status.Stage == DownloadStage.Converting),
                "O progresso deve indicar a etapa de conversão de áudio.");

            Console.WriteLine("Testando playlist MP4 a 320p…");
            progress.Clear();
            var mp4Batch = await service.DownloadAsync(
                new DownloadRequest(new Uri("https://smoke.test/playlist-mp4"), destination, DownloadFormat.Mp4, 192, 320),
                tools, progress, CancellationToken.None);
            var mp4Results = mp4Batch.Files;
            Assert(mp4Results.Count == 2, "A playlist MP4 simulada deveria produzir dois arquivos.");
            Assert(mp4Results.All(result => result.FilePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)), "A saída de vídeo precisa usar MP4.");
            Assert(mp4Results.All(result => result.FilePath.Contains("Playlist de teste", StringComparison.Ordinal)), "Os itens MP4 devem ficar na pasta da playlist.");
            var codec = await ProbeVideoCodecAsync(_ffprobe, mp4Results[0].FilePath);
            Assert(string.Equals(codec, "h264", StringComparison.OrdinalIgnoreCase), $"O codec de vídeo esperado era H.264, recebido: {codec}.");
            var (videoWidth, videoHeight) = await ProbeVideoDimensionsAsync(_ffprobe, mp4Results[0].FilePath);
            Assert(videoHeight == 320 && videoWidth <= 570, $"A saída MP4 deveria ser no máximo 320p mantendo proporção, recebida: {videoWidth}x{videoHeight}.");

            Console.WriteLine("Testando conclusão parcial de playlist…");
            var partialDestination = Path.Combine(testRoot, "partial");
            Directory.CreateDirectory(partialDestination);
            var partialBatch = await service.DownloadAsync(
                new DownloadRequest(new Uri("https://smoke.test/partial"), partialDestination, DownloadFormat.Mp3, 192),
                tools, progress, CancellationToken.None);
            Assert(partialBatch.WasPartial && partialBatch.Files.Count == 1, "Arquivos concluídos devem ser preservados quando outro item da playlist falha.");
            Assert(partialBatch.Failures.Count == 1 &&
                   partialBatch.Failures[0].Title.Contains("Outra faixa", StringComparison.OrdinalIgnoreCase) &&
                   partialBatch.Failures[0].Reason.Contains("indisponivel", StringComparison.OrdinalIgnoreCase),
                "A falha parcial deve registrar o item e um motivo legível. Registro: " +
                string.Join(" | ", partialBatch.Failures.Select(failure => $"{failure.Title}: {failure.Reason}")));

            Console.WriteLine("Testando histórico local…");
            var history = new DownloadHistoryStore(Path.Combine(testRoot, "userdata"));
            await history.AddAsync(mp3Results.Concat(mp4Results));
            var entries = await history.ReadAsync();
            Assert(entries.Count == 4, "O histórico deveria conter os quatro arquivos concluídos.");
            Assert(entries.Count(entry => entry.Format == DownloadFormat.Mp4 && entry.VideoHeight == 320) == 2, "O histórico deve preservar a resolução MP4.");

            Console.WriteLine("Testando persistência das falhas de download…");
            var failureStore = new DownloadFailureStore(Path.Combine(testRoot, "userdata"));
            await failureStore.AddAsync(partialBatch.Failures);
            var persistedFailures = await failureStore.ReadAsync();
            Assert(persistedFailures.Count == 1 &&
                   persistedFailures[0].Title == partialBatch.Failures[0].Title &&
                   persistedFailures[0].Reason == partialBatch.Failures[0].Reason,
                "A aba de falhas deve poder reabrir os motivos armazenados localmente.");

            await history.ClearAsync();
            Assert((await history.ReadAsync()).Count == 0, "Limpar Histórico deve remover os downloads registrados.");
            await failureStore.ClearAsync();
            Assert((await failureStore.ReadAsync()).Count == 0, "Limpar Histórico deve remover os downloads não concluídos registrados.");

            Console.WriteLine("Testando playlist em que todos os itens falham…");
            var totalFailure = await CaptureFailureAsync(() => service.DownloadAsync(
                new DownloadRequest(new Uri("https://smoke.test/allfail"), Path.Combine(testRoot, "all-fail"),
                    DownloadFormat.Mp3, 192),
                tools, progress, CancellationToken.None));
            Assert(totalFailure.Failures.Count == 2 &&
                   totalFailure.Failures.All(failure => failure.Reason.Contains("indisponivel", StringComparison.OrdinalIgnoreCase)),
                "Uma playlist totalmente malsucedida deve preservar o motivo de cada item para a nova aba.");
            await failureStore.AddAsync(totalFailure.Failures);
            Assert((await failureStore.ReadAsync()).Count == 3,
                "O registro persistente deve incluir as falhas parciais e os itens totalmente malsucedidos.");

            Console.WriteLine("Testando proteção contra sobrescrita…");
            var originalHash = await FileHashAsync(mp3Results[0].FilePath);
            await ExpectFailureAsync(() => service.DownloadAsync(
                new DownloadRequest(new Uri("https://smoke.test/playlist-mp3"), destination, DownloadFormat.Mp3, 256),
                tools, progress, CancellationToken.None), "Todos os arquivos já existem");
            Assert(originalHash == await FileHashAsync(mp3Results[0].FilePath), "Um download repetido não pode substituir um arquivo existente.");

            Console.WriteLine("Testando erro e cancelamento…");
            var failureDestination = Path.Combine(testRoot, "failure");
            Directory.CreateDirectory(failureDestination);
            await ExpectFailureAsync(() => service.DownloadAsync(
                new DownloadRequest(new Uri("https://smoke.test/fail"), failureDestination, DownloadFormat.Mp3, 192),
                tools, progress, CancellationToken.None), "Erro de teste da origem");
            Assert(!Directory.EnumerateDirectories(failureDestination, ".mp3local-*", SearchOption.TopDirectoryOnly).Any(), "Um erro precisa limpar a pasta temporária.");

            var cancelDestination = Path.Combine(testRoot, "cancel");
            Directory.CreateDirectory(cancelDestination);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
            try
            {
                await service.DownloadAsync(
                    new DownloadRequest(new Uri("https://smoke.test/slow"), cancelDestination, DownloadFormat.Mp3, 192),
                    tools, progress, cancellation.Token);
                throw new InvalidOperationException("A operação deveria ter sido cancelada.");
            }
            catch (OperationCanceledException) { }
            Assert(!Directory.EnumerateDirectories(cancelDestination, ".mp3local-*", SearchOption.TopDirectoryOnly).Any(), "O cancelamento precisa limpar a pasta temporária.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MP3LD_TEST_AUDIO_SOURCE", null);
            Environment.SetEnvironmentVariable("MP3LD_TEST_VIDEO_SOURCE", null);
            Environment.SetEnvironmentVariable("MP3LD_TEST_FFMPEG", null);
            try { Directory.Delete(testRoot, recursive: true); }
            catch (IOException) { }
        }
    }

    private static async Task CreateFixturesAsync()
    {
        await RunToolAsync(_ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-c:a", "pcm_s16le", _audioSource]);
        await RunToolAsync(_ffmpeg,
            ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=3", "-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-shortest", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", "-f", "matroska", _videoSource]);
    }

    private static void TestDevelopmentToolResolution(string testRoot)
    {
        var developmentRoot = Path.Combine(testRoot, "development-root");
        var developmentBaseDirectory = Path.Combine(developmentRoot, "bin", "Release", "net10.0-windows");
        var developmentToolsDirectory = Path.Combine(developmentRoot, "tools");
        var localApplicationData = Path.Combine(testRoot, "local-app-data");
        Directory.CreateDirectory(developmentBaseDirectory);
        Directory.CreateDirectory(developmentToolsDirectory);
        File.WriteAllText(Path.Combine(developmentRoot, "MP3LocalDownloader.csproj"), string.Empty);

        foreach (var name in new[] { "deno.exe", "ffmpeg.exe", "ffprobe.exe", "yt-dlp.exe" })
            File.WriteAllText(Path.Combine(developmentToolsDirectory, name), string.Empty);

        var localToolsResult = PortableToolsBundle.ResolveDevelopmentApplicationDirectory(
            developmentBaseDirectory,
            localApplicationData);
        Assert(localToolsResult == developmentRoot, "Uma execução local deve preferir as ferramentas completas do repositório.");

        File.Delete(Path.Combine(developmentToolsDirectory, "deno.exe"));
        var cachedApplicationDirectory = Path.Combine(localApplicationData, "MP3LocalDownloader", "portable", "test-bundle");
        var cachedToolsDirectory = Path.Combine(cachedApplicationDirectory, "tools");
        Directory.CreateDirectory(cachedToolsDirectory);
        foreach (var name in new[] { "deno.exe", "ffmpeg.exe", "ffprobe.exe", "yt-dlp.exe" })
            File.WriteAllText(Path.Combine(cachedToolsDirectory, name), string.Empty);

        var cacheResult = PortableToolsBundle.ResolveDevelopmentApplicationDirectory(
            developmentBaseDirectory,
            localApplicationData);
        Assert(cacheResult == cachedApplicationDirectory, "Sem ferramentas completas no repositório, a execução local deve usar o pacote portátil extraído.");

        var missingToolsResult = PortableToolsBundle.ResolveDevelopmentApplicationDirectory(
            developmentBaseDirectory,
            Path.Combine(testRoot, "empty-local-app-data"));
        Assert(missingToolsResult == developmentRoot, "Sem cache, a resolução deve manter a raiz do projeto para produzir um erro acionável.");
    }

    private static async Task<int> RunFakeYtDlpAsync(string[] args)
    {
        if (args.Contains("--version", StringComparer.Ordinal))
        {
            Console.WriteLine("smoke-fake 1.0");
            return 0;
        }
        if (!args.Contains("--yes-playlist", StringComparer.Ordinal))
        {
            Console.Error.WriteLine("The application did not enable full playlist downloads.");
            return 20;
        }
        var outputIndex = Array.IndexOf(args, "--output");
        if (outputIndex < 0 || !args[outputIndex + 1].Contains("%(playlist|Downloads)s", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("The application did not configure a playlist folder template.");
            return 21;
        }

        var url = args.LastOrDefault() ?? string.Empty;
        if (url.Contains("/fail", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("ERROR: Erro de teste da origem");
            return 1;
        }
        var partial = url.Contains("/partial", StringComparison.Ordinal);
        var failAll = url.Contains("/allfail", StringComparison.Ordinal);
        if (url.Contains("/slow", StringComparison.Ordinal))
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            return 0;
        }

        var playlistDirectory = Path.Combine(Environment.CurrentDirectory, "Playlist de teste");
        Directory.CreateDirectory(playlistDirectory);
        if (args.Contains("--extract-audio", StringComparer.Ordinal))
        {
            var audioSource = Environment.GetEnvironmentVariable("MP3LD_TEST_AUDIO_SOURCE")!;
            var ffmpeg = Environment.GetEnvironmentVariable("MP3LD_TEST_FFMPEG")!;
            var qualityIndex = Array.IndexOf(args, "--audio-quality");
            var bitrate = args[qualityIndex + 1];
            var names = new[] { "01 - Faixa teste.mp3", "02 - Outra faixa.mp3" };
            for (var index = 0; index < names.Length; index++)
            {
                Console.WriteLine($"[download] Downloading item {index + 1} of {names.Length}");
                var output = Path.Combine(playlistDirectory, names[index]);
                Console.Error.WriteLine($"[download] Destination: {output}");
                if (failAll || partial && index == 1)
                {
                    Console.Error.WriteLine($"ERROR: O item {index + 1} esta indisponivel nesta origem.");
                    continue;
                }

                await RunToolAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-i", audioSource, "-c:a", "libmp3lame", "-b:a", bitrate, output]);
                Console.WriteLine(output);
                Console.Error.WriteLine($"[download] 100.0% of {names[index]}");
            }
        }
        else
        {
            var videoSource = Environment.GetEnvironmentVariable("MP3LD_TEST_VIDEO_SOURCE")!;
            var names = new[] { "01 - Vídeo teste.mkv", "02 - Outro vídeo.mkv" };
            for (var index = 0; index < names.Length; index++)
            {
                Console.WriteLine($"[download] Downloading item {index + 1} of {names.Length}");
                var output = Path.Combine(playlistDirectory, names[index]);
                Console.Error.WriteLine($"[download] Destination: {output}");
                File.Copy(videoSource, output);
                Console.WriteLine(output);
                Console.Error.WriteLine($"[download] 100.0% of {names[index]}");
            }
        }
        return 0;
    }

    private static async Task<long> ProbeBitrateAsync(string ffprobe, string path, bool audio)
    {
        var selection = audio ? "a:0" : "v:0";
        var output = await RunToolAsync(ffprobe,
            ["-v", "error", "-select_streams", selection, "-show_entries", "stream=bit_rate", "-of", "default=noprint_wrappers=1:nokey=1", path]);
        return long.Parse(output.Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ProbeVideoCodecAsync(string ffprobe, string path)
    {
        return (await RunToolAsync(ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name", "-of", "default=noprint_wrappers=1:nokey=1", path])).Trim();
    }

    private static async Task<(int Width, int Height)> ProbeVideoDimensionsAsync(string ffprobe, string path)
    {
        var dimensions = (await RunToolAsync(ffprobe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=s=x:p=0", path])).Trim().Split('x');
        return (
            int.Parse(dimensions[0], System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(dimensions[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task<string> RunToolAsync(string executable, IEnumerable<string> arguments)
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
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start())
            throw new InvalidOperationException($"Não foi possível iniciar {Path.GetFileName(executable)}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(executable)} retornou {process.ExitCode}: {stderr.Result}");
        return stdout.Result;
    }

    private static async Task<string> FileHashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream));
    }

    private static async Task ExpectFailureAsync(Func<Task> operation, string expectedMessage)
    {
        try
        {
            await operation();
        }
        catch (DownloadFailedException ex) when (ex.Message.Contains(expectedMessage, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        throw new InvalidOperationException($"Esperava-se uma falha contendo: {expectedMessage}");
    }

    private static async Task<DownloadFailedException> CaptureFailureAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (DownloadFailedException exception)
        {
            return exception;
        }
        throw new InvalidOperationException("Esperava-se que o lote inteiro falhasse.");
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (Directory.Exists(Path.Combine(current.FullName, "tools")) && File.Exists(Path.Combine(current.FullName, "MP3LocalDownloader.csproj")))
                return current.FullName;
        throw new DirectoryNotFoundException("Não foi possível encontrar a pasta raiz do repositório.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<T> _items = new();
        public IReadOnlyCollection<T> Items => _items.ToArray();
        public void Report(T value) => _items.Enqueue(value);
        public void Clear()
        {
            while (_items.TryDequeue(out _)) { }
        }
    }
}
