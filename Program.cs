using MP3LocalDownloader.Services;

namespace MP3LocalDownloader;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var checkBundle = args.Any(argument =>
            string.Equals(argument, "--check-bundle", StringComparison.OrdinalIgnoreCase));
        try
        {
            var applicationDirectory = PortableToolsBundle.PrepareAsync().GetAwaiter().GetResult();
            PortableToolsBundle.WriteThirdPartyNotices(applicationDirectory);
            if (checkBundle)
            {
                DependencyChecker.CheckAsync(applicationDirectory, CancellationToken.None).GetAwaiter().GetResult();
                return 0;
            }

            ApplicationConfiguration.Initialize();
            Application.Run(new UI.MainForm(applicationDirectory));
            return 0;
        }
        catch (Exception exception)
        {
            if (checkBundle)
                Console.Error.WriteLine(exception.Message);
            else
                MessageBox.Show(exception.Message, "Luma Video Downloader", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
