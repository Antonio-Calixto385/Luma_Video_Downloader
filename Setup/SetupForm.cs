using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MP3LocalDownloader.Setup;

public sealed class SetupForm : Form
{
    private readonly TextBox _installPathBox = new();
    private readonly Button _browseButton = new();
    private readonly Button _installButton = new();
    private readonly Button _cancelButton = new();
    private readonly Label _statusLabel = new();
    private readonly ProgressBar _progressBar = new();
    private readonly InstallerService _installer = new();
    private CancellationTokenSource? _cancellation;

    public SetupForm()
    {
        Text = "Instalação do Luma Video Downloader";
        MinimumSize = new Size(630, 355);
        Size = new Size(720, 390);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;
        BuildLayout();

        _browseButton.Click += (_, _) => BrowseInstallLocation();
        _installButton.Click += async (_, _) => await InstallAsync();
        _cancelButton.Click += (_, _) => _cancellation?.Cancel();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            ColumnCount = 3,
            RowCount = 7
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var title = new Label
        {
            Text = "Luma Video Downloader",
            Font = new Font(Font.FontFamily, 18, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        root.Controls.Add(title, 0, 0);
        root.SetColumnSpan(title, 3);

        var intro = new Label
        {
            Text = "Este instalador inclui o aplicativo autocontido e baixa yt-dlp, FFmpeg e Deno das fontes oficiais. Os arquivos são verificados por SHA-256 antes da instalação.",
            Dock = DockStyle.Fill,
            AutoEllipsis = true
        };
        root.Controls.Add(intro, 0, 1);
        root.SetColumnSpan(intro, 3);

        root.Controls.Add(new Label { Text = "Pasta de instalação:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
        _installPathBox.Dock = DockStyle.Fill;
        _installPathBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "LumaVideoDownloader");
        root.Controls.Add(_installPathBox, 1, 2);
        _browseButton.Text = "Selecionar local";
        _browseButton.Dock = DockStyle.Fill;
        root.Controls.Add(_browseButton, 2, 2);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        _installButton.Text = "Instalar";
        _installButton.Width = 125;
        _installButton.Height = 32;
        _cancelButton.Text = "Cancelar";
        _cancelButton.Width = 105;
        _cancelButton.Height = 32;
        _cancelButton.Enabled = false;
        actions.Controls.Add(_installButton);
        actions.Controls.Add(_cancelButton);
        root.Controls.Add(actions, 1, 3);
        root.SetColumnSpan(actions, 2);

        root.Controls.Add(new Label { Text = "Status:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 4);
        _statusLabel.Text = "Pronto para instalar.";
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        root.Controls.Add(_statusLabel, 1, 4);
        root.SetColumnSpan(_statusLabel, 2);

        _progressBar.Dock = DockStyle.Fill;
        root.Controls.Add(_progressBar, 1, 5);
        root.SetColumnSpan(_progressBar, 2);

        var note = new Label
        {
            Text = "Requer Windows 10/11 x64 e conexão com a internet. A instalação fica na pasta do usuário e não precisa de privilégios de administrador.",
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            AutoEllipsis = true
        };
        root.Controls.Add(note, 0, 6);
        root.SetColumnSpan(note, 3);
        Controls.Add(root);
    }

    private void BrowseInstallLocation()
    {
        var initialPath = Path.GetDirectoryName(_installPathBox.Text);
        using var dialog = new FolderBrowserDialog
        {
            Description = "Escolha a pasta pai para instalar Luma Video Downloader",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(initialPath) ? initialPath! : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
            _installPathBox.Text = Path.Combine(dialog.SelectedPath, "LumaVideoDownloader");
    }

    private async Task InstallAsync()
    {
        var installPath = _installPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(installPath))
        {
            ShowError("Informe a pasta de instalação.");
            return;
        }

        SetBusy(true);
        _progressBar.Value = 0;
        _cancellation = new CancellationTokenSource();
        var progress = new Progress<InstallProgress>(update =>
        {
            _progressBar.Value = Math.Clamp(update.Percent, _progressBar.Minimum, _progressBar.Maximum);
            _statusLabel.Text = update.Message;
        });

        try
        {
            var installedPath = await _installer.InstallAsync(installPath, progress, _cancellation.Token);
            string shortcutPath;
            try
            {
                shortcutPath = CreateDesktopShortcut(Path.Combine(installedPath, "LumaVideoDownloader.exe"));
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "Instalação concluída, mas o atalho não foi criado.";
                ShowError(
                    $"O aplicativo foi instalado em:\n{installedPath}\n\n" +
                    $"Não foi possível criar o atalho na área de trabalho: {ex.Message}\n\n" +
                    "Você ainda pode abrir LumaVideoDownloader.exe nessa pasta.");
                return;
            }

            _statusLabel.Text = "Instalação concluída.";
            MessageBox.Show(this,
                $"O aplicativo e as ferramentas foram instalados em:\n{installedPath}\n\n" +
                $"Foi criado um único atalho na área de trabalho:\n{shortcutPath}",
                "Instalação concluída", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "Instalação cancelada. Os arquivos temporários foram removidos.";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Não foi possível concluir a instalação.";
            var message = ex switch
            {
                HttpRequestException => "Falha ao baixar um componente. Verifique a conexão e tente novamente.",
                IOException => "Falha ao gravar ou extrair arquivos. Verifique o espaço livre e as permissões da pasta.",
                _ => ex.Message
            };
            ShowError(message);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _installPathBox.Enabled = !busy;
        _browseButton.Enabled = !busy;
        _installButton.Enabled = !busy;
        _cancelButton.Enabled = busy;
    }

    private static string CreateDesktopShortcut(string targetPath)
    {
        var shortcutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "Luma Video Downloader.lnk");
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("O componente de atalhos do Windows não está disponível.");
        object? shell = null;
        object? shortcut = null;

        try
        {
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Não foi possível iniciar o componente de atalhos do Windows.");
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: [shortcutPath])
                ?? throw new InvalidOperationException("Não foi possível criar o atalho do aplicativo.");

            var shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [targetPath]);
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, [Path.GetDirectoryName(targetPath)!]);
            shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, [$"{targetPath},0"]);
            shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, ["Luma Video Downloader"]);
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            return shortcutPath;
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
                Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell))
                Marshal.FinalReleaseComObject(shell);
        }
    }

    private void ShowError(string message) => MessageBox.Show(this, message, "Instalador Luma Video Downloader", MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
