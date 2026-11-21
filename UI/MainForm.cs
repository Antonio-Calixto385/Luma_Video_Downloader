using System.Diagnostics;
using System.Text.Json;
using MP3LocalDownloader.Models;
using MP3LocalDownloader.Services;

namespace MP3LocalDownloader.UI;

public sealed class MainForm : Form
{
    private readonly TextBox _urlBox = new();
    private readonly TextBox _destinationBox = new();
    private readonly Button _pasteLinkButton = new();
    private readonly Button _browseButton = new();
    private readonly Button _downloadButton = new();
    private readonly Button _cancelButton = new();
    private readonly Button _refreshHistoryButton = new();
    private readonly Button _openHistoryButton = new();
    private readonly Button _clearHistoryButton = new();
    private readonly Button _refreshFailuresButton = new();
    private readonly Button _clearFailuresButton = new();
    private readonly ComboBox _formatBox = new();
    private readonly ComboBox _bitrateBox = new();
    private readonly Label _bitrateLabel = new();
    private readonly Label _qualityUnitLabel = new();
    private readonly CheckBox _rememberDestination = new();
    private readonly Label _statusLabel = new();
    private readonly Label _resultLabel = new();
    private readonly Label _historyCountLabel = new();
    private readonly Label _failureCountLabel = new();
    private readonly ProgressBar _progressBar = new();
    private readonly DataGridView _historyGrid = new();
    private readonly DataGridView _failuresGrid = new();
    private readonly DownloadService _downloadService = new();
    private readonly DownloadHistoryStore _historyStore;
    private readonly DownloadFailureStore _failureStore;
    private readonly string _applicationDirectory;
    private readonly string _settingsPath;
    private CancellationTokenSource? _operationCancellation;
    private IReadOnlyList<HistoryGridRow> _historyRows = [];
    private IReadOnlyList<FailureGridRow> _failureRows = [];
    private readonly System.Windows.Forms.Timer _operationTimer = new() { Interval = 1000 };
    private DateTime _operationStartedAt;
    private string _baseStatusMessage = "Aguardando…";
    private int _preferredVideoHeight = 720;

    public MainForm(string? applicationDirectory = null)
    {
        _applicationDirectory = applicationDirectory ?? AppContext.BaseDirectory;
        Text = "Luma Video Downloader";
        MinimumSize = new Size(760, 520);
        Size = new Size(900, 620);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Font;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appDataDirectory = Path.Combine(appData, "MP3LocalDownloader");
        Directory.CreateDirectory(appDataDirectory);
        _settingsPath = Path.Combine(appDataDirectory, "appsettings.json");
        _historyStore = new DownloadHistoryStore(appData);
        _failureStore = new DownloadFailureStore(appData);

        BuildLayout();
        _formatBox.SelectedValue = DownloadFormat.Mp3;
        LoadPreferences();
        ConfigureBitrateControl();

        _formatBox.SelectedValueChanged += (_, _) =>
        {
            ConfigureBitrateControl(resetValue: true);
            SavePreferences();
        };
        _bitrateBox.SelectedValueChanged += (_, _) =>
        {
            if (SelectedFormat == DownloadFormat.Mp4 && TryGetSelectedQualityValue() is int videoHeight)
                _preferredVideoHeight = videoHeight;
            SavePreferences();
        };
        _rememberDestination.CheckedChanged += (_, _) => SavePreferences();
        _downloadButton.Click += async (_, _) => await StartDownloadAsync();
        _cancelButton.Click += (_, _) => _operationCancellation?.Cancel();
        _browseButton.Click += (_, _) => ChooseDestination();
        _refreshHistoryButton.Click += async (_, _) => await RefreshHistoryAsync();
        _openHistoryButton.Click += (_, _) => OpenSelectedDownload();
        _clearHistoryButton.Click += async (_, _) => await ClearHistoryAsync();
        _historyGrid.SelectionChanged += (_, _) => _openHistoryButton.Enabled = _historyGrid.SelectedRows.Count > 0;
        _refreshFailuresButton.Click += async (_, _) => await RefreshFailuresAsync();
        _clearFailuresButton.Click += async (_, _) => await ClearFailuresAsync();
        _operationTimer.Tick += (_, _) => RefreshElapsedStatus();
        FormClosed += (_, _) => _operationTimer.Dispose();
        _tabs.SelectedIndexChanged += async (_, _) =>
        {
            if (_tabs.SelectedTab == _historyTab)
                await RefreshHistoryAsync();
            else if (_tabs.SelectedTab == _failuresTab)
                await RefreshFailuresAsync();
        };
    }

    private async Task ClearHistoryAsync()
    {
        if (_historyRows.Count == 0)
            return;

        var result = MessageBox.Show(
            this,
            "Deseja limpar todo o histórico de downloads feitos?",
            "Limpar Histórico",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (result != DialogResult.Yes)
            return;

        try
        {
            await _historyStore.ClearAsync();
            await RefreshHistoryAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Não foi possível limpar o histórico de downloads.");
        }
    }

    private async Task ClearFailuresAsync()
    {
        if (_failureRows.Count == 0)
            return;

        var result = MessageBox.Show(
            this,
            "Deseja limpar todo o histórico de downloads não concluídos?",
            "Limpar Histórico",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (result != DialogResult.Yes)
            return;

        try
        {
            await _failureStore.ClearAsync();
            await RefreshFailuresAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Não foi possível limpar o histórico de downloads não concluídos.");
        }
    }

    private async Task RefreshFailuresAsync()
    {
        try
        {
            var entries = await _failureStore.ReadAsync();
            _failureRows = entries.Select(entry => new FailureGridRow(entry)).ToArray();
            _failuresGrid.DataSource = null;
            _failuresGrid.DataSource = _failureRows;
            _failureCountLabel.Text = $"{_failureRows.Count} downloads não concluídos registrados";
            _clearFailuresButton.Enabled = _failureRows.Count > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _clearFailuresButton.Enabled = false;
            ShowError("Não foi possível ler o registro de downloads não concluídos.");
        }
    }

    private readonly TabControl _tabs = new();
    private readonly TabPage _downloadTab = new("Baixar");
    private readonly TabPage _historyTab = new("Downloads feitos");
    private readonly TabPage _failuresTab = new("Downloads não concluídos");

    private void BuildLayout()
    {
        _tabs.Dock = DockStyle.Fill;
        _tabs.TabPages.Add(_downloadTab);
        _tabs.TabPages.Add(_historyTab);
        _tabs.TabPages.Add(_failuresTab);
        BuildDownloadTab();
        BuildHistoryTab();
        BuildFailuresTab();
        Controls.Add(_tabs);
    }

    private void BuildDownloadTab()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            ColumnCount = 3,
            RowCount = 11
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
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

        _urlBox.Dock = DockStyle.Fill;
        _urlBox.PlaceholderText = "Cole um link de áudio, vídeo, playlist ou mix";
        root.Controls.Add(MakeFieldLabel("Link:", _urlBox.PreferredHeight), 0, 1);
        root.Controls.Add(_urlBox, 1, 1);
        _pasteLinkButton.Text = "Colar link";
        _pasteLinkButton.Height = _urlBox.PreferredHeight;
        _pasteLinkButton.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _pasteLinkButton.Click += (_, _) => PasteLinkFromClipboard();
        root.Controls.Add(_pasteLinkButton, 2, 1);

        _destinationBox.Dock = DockStyle.Fill;
        root.Controls.Add(MakeFieldLabel("Destino:", _destinationBox.PreferredHeight), 0, 2);
        root.Controls.Add(_destinationBox, 1, 2);
        _browseButton.Text = "Selecionar pasta";
        _browseButton.Height = _destinationBox.PreferredHeight;
        _browseButton.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        root.Controls.Add(_browseButton, 2, 2);

        _formatBox.Dock = DockStyle.Left;
        _formatBox.Width = 190;
        _formatBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _formatBox.DataSource = new[]
        {
            new FormatChoice("MP3 (áudio)", DownloadFormat.Mp3),
            new FormatChoice("MP4 (vídeo)", DownloadFormat.Mp4)
        };
        _formatBox.DisplayMember = nameof(FormatChoice.Label);
        _formatBox.ValueMember = nameof(FormatChoice.Value);
        root.Controls.Add(MakeFieldLabel("Formato:", _formatBox.PreferredHeight), 0, 3);
        root.Controls.Add(_formatBox, 1, 3);

        _bitrateBox.Width = 140;
        _bitrateBox.DropDownStyle = ComboBoxStyle.DropDownList;
        root.Controls.Add(MakeFieldLabel("Bitrate:", _bitrateBox.PreferredHeight), 0, 4);
        var qualityPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0),
            Padding = Padding.Empty
        };
        qualityPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        qualityPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50));
        qualityPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        qualityPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _bitrateBox.Dock = DockStyle.Fill;
        qualityPanel.Controls.Add(_bitrateBox, 0, 0);
        _qualityUnitLabel.Text = "kbps";
        _qualityUnitLabel.Dock = DockStyle.Fill;
        _qualityUnitLabel.TextAlign = ContentAlignment.MiddleLeft;
        _qualityUnitLabel.Padding = new Padding(3, 0, 10, 0);
        qualityPanel.Controls.Add(_qualityUnitLabel, 1, 0);
        _bitrateLabel.Text = "Qualidade de áudio MP3 (64–320 kbps)";
        _bitrateLabel.Dock = DockStyle.Fill;
        _bitrateLabel.TextAlign = ContentAlignment.MiddleLeft;
        _bitrateLabel.Padding = new Padding(5, 0, 0, 0);
        qualityPanel.Controls.Add(_bitrateLabel, 2, 0);
        root.Controls.Add(qualityPanel, 1, 4);
        root.SetColumnSpan(qualityPanel, 2);

        _rememberDestination.Text = "Salvar preferência de pasta";
        _rememberDestination.AutoSize = true;
        root.Controls.Add(_rememberDestination, 1, 5);
        root.SetColumnSpan(_rememberDestination, 2);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        _downloadButton.Text = "Baixar";
        _downloadButton.Width = 130;
        _downloadButton.Height = 34;
        _cancelButton.Text = "Cancelar";
        _cancelButton.Width = 105;
        _cancelButton.Height = 34;
        _cancelButton.Enabled = false;
        actions.Controls.Add(_downloadButton);
        actions.Controls.Add(_cancelButton);
        root.Controls.Add(actions, 1, 6);
        root.SetColumnSpan(actions, 2);

        root.Controls.Add(MakeLabel("Status:"), 0, 7);
        _statusLabel.Text = "Aguardando…";
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        root.Controls.Add(_statusLabel, 1, 7);
        root.SetColumnSpan(_statusLabel, 2);

        _progressBar.Dock = DockStyle.Fill;
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 100;
        root.Controls.Add(_progressBar, 1, 8);
        root.SetColumnSpan(_progressBar, 2);

        root.Controls.Add(MakeLabel("Resultado:"), 0, 9);
        _resultLabel.Dock = DockStyle.Fill;
        _resultLabel.AutoEllipsis = true;
        _resultLabel.TextAlign = ContentAlignment.MiddleLeft;
        root.Controls.Add(_resultLabel, 1, 9);
        root.SetColumnSpan(_resultLabel, 2);

        _downloadTab.Controls.Add(root);
    }

    private void BuildHistoryTab()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 2
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 2, 0, 0)
        };
        _historyCountLabel.Text = "Downloads registrados neste computador";
        _historyCountLabel.AutoSize = true;
        _historyCountLabel.Padding = new Padding(0, 8, 12, 0);
        toolbar.Controls.Add(_historyCountLabel);
        _refreshHistoryButton.Text = "Atualizar";
        _refreshHistoryButton.Width = 90;
        toolbar.Controls.Add(_refreshHistoryButton);
        _openHistoryButton.Text = "Abrir arquivo";
        _openHistoryButton.Width = 105;
        _openHistoryButton.Enabled = false;
        toolbar.Controls.Add(_openHistoryButton);
        _clearHistoryButton.Text = "Limpar Histórico";
        _clearHistoryButton.Width = 125;
        _clearHistoryButton.Enabled = false;
        toolbar.Controls.Add(_clearHistoryButton);
        root.Controls.Add(toolbar, 0, 0);

        _historyGrid.Dock = DockStyle.Fill;
        _historyGrid.ReadOnly = true;
        _historyGrid.AllowUserToAddRows = false;
        _historyGrid.AllowUserToDeleteRows = false;
        _historyGrid.AllowUserToResizeRows = false;
        _historyGrid.MultiSelect = false;
        _historyGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _historyGrid.AutoGenerateColumns = false;
        _historyGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _historyGrid.RowHeadersVisible = false;
        _historyGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(HistoryGridRow.Title), HeaderText = "Arquivo", FillWeight = 42 });
        _historyGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(HistoryGridRow.Format), HeaderText = "Formato", FillWeight = 12 });
        _historyGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(HistoryGridRow.Quality), HeaderText = "Qualidade", FillWeight = 12 });
        _historyGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(HistoryGridRow.CompletedAt),
            HeaderText = "Concluído em",
            FillWeight = 20,
            DefaultCellStyle = new DataGridViewCellStyle { Format = "dd/MM/yyyy HH:mm" }
        });
        root.Controls.Add(_historyGrid, 0, 1);
        _historyTab.Controls.Add(root);
    }

    private void BuildFailuresTab()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 2
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 2, 0, 0)
        };
        _failureCountLabel.Text = "Tentativas não concluídas neste computador";
        _failureCountLabel.AutoSize = true;
        _failureCountLabel.Padding = new Padding(0, 8, 12, 0);
        toolbar.Controls.Add(_failureCountLabel);
        _refreshFailuresButton.Text = "Atualizar";
        _refreshFailuresButton.Width = 90;
        toolbar.Controls.Add(_refreshFailuresButton);
        _clearFailuresButton.Text = "Limpar Histórico";
        _clearFailuresButton.Width = 125;
        _clearFailuresButton.Enabled = false;
        toolbar.Controls.Add(_clearFailuresButton);
        root.Controls.Add(toolbar, 0, 0);

        _failuresGrid.Dock = DockStyle.Fill;
        _failuresGrid.ReadOnly = true;
        _failuresGrid.AllowUserToAddRows = false;
        _failuresGrid.AllowUserToDeleteRows = false;
        _failuresGrid.AllowUserToResizeRows = false;
        _failuresGrid.MultiSelect = false;
        _failuresGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _failuresGrid.AutoGenerateColumns = false;
        _failuresGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _failuresGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCells;
        _failuresGrid.RowHeadersVisible = false;
        _failuresGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(FailureGridRow.Title),
            HeaderText = "Vídeo / mídia",
            FillWeight = 22
        });
        _failuresGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(FailureGridRow.Format),
            HeaderText = "Formato",
            FillWeight = 9
        });
        _failuresGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(FailureGridRow.Reason),
            HeaderText = "Motivo",
            FillWeight = 49,
            DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True }
        });
        _failuresGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(FailureGridRow.FailedAt),
            HeaderText = "Data",
            FillWeight = 20,
            DefaultCellStyle = new DataGridViewCellStyle { Format = "dd/MM/yyyy HH:mm" }
        });
        root.Controls.Add(_failuresGrid, 0, 1);
        _failuresTab.Controls.Add(root);
    }

    private static Label MakeFieldLabel(string text, int height) => new()
    {
        Text = text,
        AutoSize = false,
        Height = height,
        Anchor = AnchorStyles.Top | AnchorStyles.Left,
        TextAlign = ContentAlignment.MiddleLeft
    };

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft
    };

    private async Task StartDownloadAsync()
    {
        if (!Uri.TryCreate(_urlBox.Text.Trim(), UriKind.Absolute, out var url) ||
            (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            ShowError("Informe uma URL válida iniciada por http:// ou https://.");
            return;
        }

        var destination = _destinationBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(destination) || !Directory.Exists(destination))
        {
            ShowError("Escolha uma pasta de destino existente.");
            return;
        }

        try
        {
            var probe = Path.Combine(destination, $".mp3local-write-{Guid.NewGuid():N}.tmp");
            using (File.Create(probe)) { }
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("Não foi possível gravar na pasta escolhida. Selecione outro destino.");
            return;
        }

        SetBusy(true);
        _progressBar.Value = 0;
        _resultLabel.Text = string.Empty;
        _operationCancellation = new CancellationTokenSource();
        SavePreferences();

        var progress = new Progress<DownloadStatus>(UpdateStatus);
        try
        {
            UpdateStatus(new DownloadStatus(DownloadStage.Analyzing, Message: "Verificando ferramentas e preparando a solicitação…"));
            var tools = await DependencyChecker.CheckAsync(_applicationDirectory, _operationCancellation.Token);
            var format = SelectedFormat;
            var qualityValue = GetSelectedQualityValue();
            var batch = await _downloadService.DownloadAsync(
                new DownloadRequest(
                    url,
                    destination,
                    format,
                    format == DownloadFormat.Mp3 ? qualityValue : 192,
                    format == DownloadFormat.Mp4 ? qualityValue : _preferredVideoHeight),
                tools,
                progress,
                _operationCancellation.Token);
            var results = batch.Files;
            try
            {
                await _historyStore.AddAsync(results, CancellationToken.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowError("O download terminou, mas não foi possível gravar o histórico local.");
            }
            if (batch.Failures.Count > 0)
            {
                try
                {
                    await _failureStore.AddAsync(batch.Failures, CancellationToken.None);
                    if (_tabs.SelectedTab == _failuresTab)
                        await RefreshFailuresAsync();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ShowError("O download foi concluído parcialmente, mas não foi possível salvar o registro dos itens que falharam.");
                }
            }
            _resultLabel.Text = batch.WasPartial
                ? $"{results.Count} arquivos salvos parcialmente em {destination}. Consulte o status para detalhes."
                : results.Count == 1
                    ? results[0].FilePath
                    : $"{results.Count} arquivos salvos em {destination}";
            _statusLabel.Text = batch.WasPartial
                ? $"Concluído parcialmente: {results.Count} arquivo(s). Alguns itens não puderam ser processados."
                : $"Concluído: {results.Count} arquivo(s).";
            _baseStatusMessage = _statusLabel.Text;
            _progressBar.Value = 100;
            if (batch.Failures.Count > 0)
            {
                _baseStatusMessage += $" {batch.Failures.Count} item(ns) com falha — veja a aba correspondente.";
                _statusLabel.Text = _baseStatusMessage;
            }
            if (_tabs.SelectedTab == _historyTab)
                await RefreshHistoryAsync();
        }
        catch (OperationCanceledException)
        {
            UpdateStatus(new DownloadStatus(DownloadStage.Cancelled, Message: "Cancelado pelo usuário."));
            _progressBar.Value = 0;
        }
        catch (Exception ex)
        {
            UpdateStatus(new DownloadStatus(DownloadStage.Error, Message: "Falha no processo. Registrando o motivo…"));
            var failureRecorded = false;
            try
            {
                var failureTitle = Uri.TryCreate(_urlBox.Text.Trim(), UriKind.Absolute, out var failedUrl)
                    ? $"Mídia de {failedUrl.Host}"
                    : "Solicitação de download";
                var failures = ex is DownloadFailedException { Failures.Count: > 0 } downloadException
                    ? downloadException.Failures
                    : [new DownloadFailure(failureTitle, DownloadService.CleanError(ex.Message), SelectedFormat, DateTimeOffset.Now)];
                await _failureStore.AddAsync(failures, CancellationToken.None);
                failureRecorded = true;
                if (_tabs.SelectedTab == _failuresTab)
                    await RefreshFailuresAsync();
            }
            catch (Exception storageException) when (storageException is IOException or UnauthorizedAccessException)
            {
                ShowError($"O download falhou: {ex.Message}\n\nTambém não foi possível registrar a falha: {storageException.Message}");
            }
            if (failureRecorded)
            {
                UpdateStatus(new DownloadStatus(DownloadStage.Error, Message: "Falha registrada na aba Downloads não concluídos."));
                ShowError(ex.Message);
            }
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            SetBusy(false);
        }
    }

    private DownloadFormat SelectedFormat =>
        _formatBox.SelectedItem is FormatChoice choice ? choice.Value : DownloadFormat.Mp3;

    private void ConfigureBitrateControl(bool resetValue = false)
    {
        var format = SelectedFormat;
        var isMp3 = format == DownloadFormat.Mp3;
        var defaultValue = isMp3 ? 192 : _preferredVideoHeight;
        var choices = isMp3
            ? new[] { 64, 96, 128, 160, 192, 224, 256, 320 }
                .Select(value => new QualityChoice(value.ToString(), value))
                .ToArray()
            : new[]
            {
                new QualityChoice("320p", 320),
                new QualityChoice("720p", 720),
                new QualityChoice("1080p", 1080),
                new QualityChoice("4K (2160p)", 2160)
            };
        var previousValue = resetValue ? defaultValue : TryGetSelectedQualityValue() ?? defaultValue;
        _bitrateBox.DataSource = choices;
        _bitrateBox.DisplayMember = nameof(QualityChoice.Label);
        _bitrateBox.ValueMember = nameof(QualityChoice.Value);
        _bitrateBox.MaxDropDownItems = choices.Length;
        _bitrateBox.SelectedValue = choices.Any(choice => choice.Value == previousValue) ? previousValue : defaultValue;
        _qualityUnitLabel.Visible = isMp3;

        _bitrateLabel.Text = format == DownloadFormat.Mp3
            ? "MP3: bitrate de áudio (64–320 kbps)"
            : "Resolução máxima (sem aumentar vídeos menores)";
        _downloadButton.Text = format == DownloadFormat.Mp3 ? "Baixar MP3" : "Baixar MP4";
    }

    private void UpdateStatus(DownloadStatus status)
    {
        _baseStatusMessage = status.Message ?? status.Stage switch
        {
            DownloadStage.Waiting => "Aguardando…",
            DownloadStage.Analyzing => "Analisando…",
            DownloadStage.Downloading => "Baixando…",
            DownloadStage.Converting => "Convertendo…",
            DownloadStage.Completed => "Concluído.",
            DownloadStage.Cancelled => "Cancelado.",
            _ => "Erro."
        };
        RefreshElapsedStatus();

        if (status.Percent is { } percent)
            _progressBar.Value = Math.Clamp(percent, _progressBar.Minimum, 99);
    }

    private void RefreshElapsedStatus()
    {
        if (!_operationTimer.Enabled)
        {
            _statusLabel.Text = _baseStatusMessage;
            return;
        }

        var elapsed = DateTime.Now - _operationStartedAt;
        var elapsedText = elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}"
            : $"{elapsed.Seconds}s";
        _statusLabel.Text = $"{_baseStatusMessage} ({elapsedText})";
    }

    private void SetBusy(bool busy)
    {
        if (busy)
        {
            _operationStartedAt = DateTime.Now;
            _operationTimer.Start();
            RefreshElapsedStatus();
        }
        else
        {
            _operationTimer.Stop();
            RefreshElapsedStatus();
        }

        _urlBox.Enabled = !busy;
        _pasteLinkButton.Enabled = !busy;
        _destinationBox.Enabled = !busy;
        _browseButton.Enabled = !busy;
        _formatBox.Enabled = !busy;
        _bitrateBox.Enabled = !busy;
        _rememberDestination.Enabled = !busy;
        _downloadButton.Enabled = !busy;
        _cancelButton.Enabled = busy;
    }

    private void ChooseDestination()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Selecione a pasta para salvar os arquivos MP3 e MP4",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_destinationBox.Text)
                ? _destinationBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _destinationBox.Text = dialog.SelectedPath;
            SavePreferences();
        }
    }

    private void PasteLinkFromClipboard()
    {
        try
        {
            if (!Clipboard.ContainsText())
            {
                ShowError("A área de transferência não contém um link de texto.");
                return;
            }

            var copiedLink = Clipboard.GetText().Trim();
            if (copiedLink.Length == 0)
            {
                ShowError("A área de transferência não contém um link de texto.");
                return;
            }

            _urlBox.Text = copiedLink;
            _urlBox.SelectionStart = _urlBox.TextLength;
            _urlBox.Focus();
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            ShowError($"Não foi possível acessar a área de transferência. Tente novamente.\n{ex.Message}");
        }
    }

    private int GetSelectedQualityValue() =>
        TryGetSelectedQualityValue() ?? throw new InvalidOperationException("Selecione uma opção de qualidade.");

    private int? TryGetSelectedQualityValue() =>
        _bitrateBox.SelectedItem is QualityChoice choice ? choice.Value : null;

    private void LoadPreferences()
    {
        var defaultDestination = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Luma Video Downloader");
        try
        {
            if (File.Exists(_settingsPath))
            {
                var settings = JsonSerializer.Deserialize<LocalSettings>(File.ReadAllText(_settingsPath));
                if (settings is not null)
                {
                    _rememberDestination.Checked = settings.RememberDestination;
                    if (settings.RememberDestination && settings.Destination is not null && Directory.Exists(settings.Destination))
                        _destinationBox.Text = settings.Destination;
                    else
                        _destinationBox.Text = defaultDestination;

                    if (settings.VideoHeight is 320 or 720 or 1080 or 2160)
                        _preferredVideoHeight = settings.VideoHeight;
                    ConfigureBitrateControl();
                    var choices = _bitrateBox.Items.Cast<QualityChoice>().ToArray();
                    _bitrateBox.SelectedValue = choices.Any(choice => choice.Value == settings.BitrateKbps)
                        ? settings.BitrateKbps
                        : 192;
                    return;
                }
            }

            _destinationBox.Text = defaultDestination;
            Directory.CreateDirectory(defaultDestination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _destinationBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        }
    }

    private void SavePreferences()
    {
        try
        {
            var settings = new LocalSettings(
                _rememberDestination.Checked,
                _rememberDestination.Checked ? _destinationBox.Text : null,
                SelectedFormat,
                SelectedFormat == DownloadFormat.Mp3 ? TryGetSelectedQualityValue() ?? 192 : 192,
                SelectedFormat == DownloadFormat.Mp4 ? TryGetSelectedQualityValue() ?? _preferredVideoHeight : _preferredVideoHeight);
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException) { }
    }

    private async Task RefreshHistoryAsync()
    {
        try
        {
            var entries = await _historyStore.ReadAsync();
            _historyRows = entries.Select(entry => new HistoryGridRow(entry)).ToArray();
            _historyGrid.DataSource = null;
            _historyGrid.DataSource = _historyRows;
            _historyCountLabel.Text = $"{_historyRows.Count} downloads registrados neste computador";
            _openHistoryButton.Enabled = _historyGrid.SelectedRows.Count > 0;
            _clearHistoryButton.Enabled = _historyRows.Count > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _clearHistoryButton.Enabled = false;
            ShowError("Não foi possível ler o histórico local.");
        }
    }

    private void OpenSelectedDownload()
    {
        if (_historyGrid.SelectedRows.Count == 0 || _historyGrid.SelectedRows[0].DataBoundItem is not HistoryGridRow row)
            return;
        if (!File.Exists(row.Entry.FilePath))
        {
            ShowError("O arquivo não está mais nessa localização.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{row.Entry.FilePath}\"") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            ShowError("Não foi possível abrir a localização do arquivo.");
        }
    }

    private void ShowError(string message) => MessageBox.Show(this, message, "Luma Video Downloader", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private sealed record FormatChoice(string Label, DownloadFormat Value);
    private sealed record QualityChoice(string Label, int Value);
    private sealed record LocalSettings(
        bool RememberDestination,
        string? Destination,
        DownloadFormat Format = DownloadFormat.Mp3,
        int BitrateKbps = 192,
        int VideoHeight = 720);
    private sealed record HistoryGridRow(DownloadHistoryEntry Entry)
    {
        public string Title => Entry.Title;
        public string Format => Entry.Format.ToString().ToUpperInvariant();
        public string Quality => Entry.Format == DownloadFormat.Mp4 && Entry.VideoHeight is int height
            ? height == 2160 ? "4K (2160p)" : $"{height}p"
            : $"{Entry.BitrateKbps} kbps";
        public DateTimeOffset CompletedAt => Entry.CompletedAt.ToLocalTime();
    }

    private sealed record FailureGridRow(DownloadFailure Entry)
    {
        public string Title => Entry.Title;
        public string Format => Entry.Format.ToString().ToUpperInvariant();
        public string Reason => Entry.Reason.Length <= 220 ? Entry.Reason : Entry.Reason[..217] + "…";
        public DateTimeOffset FailedAt => Entry.FailedAt.ToLocalTime();
    }
}
