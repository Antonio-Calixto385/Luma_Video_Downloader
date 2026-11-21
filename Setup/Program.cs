namespace MP3LocalDownloader.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if ((args.Length == 2 || args.Length == 3) &&
            string.Equals(args[0], "--prepare-portable-bundle", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var progress = new Progress<InstallProgress>(_ => { });
                new InstallerService().InstallAsync(args[1], progress, CancellationToken.None).GetAwaiter().GetResult();
                if (args.Length == 3)
                    File.WriteAllText(args[2], "Portable tools downloaded and verified.");
                return 0;
            }
            catch (Exception exception)
            {
                if (args.Length == 3)
                    File.WriteAllText(args[2], exception.ToString());
                return 1;
            }
        }

        if (args.Any(argument => string.Equals(argument, "--check-package", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                InstallerService.ValidateEmbeddedApplicationPayload();
                return 0;
            }
            catch
            {
                return 2;
            }
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new SetupForm());
        return 0;
    }
}
