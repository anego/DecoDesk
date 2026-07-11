using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DecoDesk.Configuration;
using DecoDesk.Database;
using Microsoft.Extensions.Logging;

namespace DecoDesk.ViewModels;

/// <summary>
/// 設定画面 (MainWindow) のデータバインディングとコマンド処理を行う ViewModel です。
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    private static readonly ILogger Logger = DecoDesk.OS.AppLoggerFactory.CreateLogger<MainWindowViewModel>();

    private readonly SettingsManager _settingsManager;
    private readonly DatabaseManager _dbManager;
    private readonly ImageBulkIndexer _indexer;

    private string _targetDirectory = string.Empty;
    private int _updateIntervalSeconds = 900;
    private string _processingMode = "None";
    private bool _autoUpdateEnabled;
    private int _wallpaperImageCount = 6;
    private int _wallpaperImageSize = 350;
    private double _wallpaperDensity = 1.0;
    private string _wallpaperBackgroundColor = "#2F4F4F";

    private bool _isIndexing;
    private double _progressValue;
    private string _progressText = "未スキャン";
    private CancellationTokenSource? _indexingCts;

    /// <summary>
    /// 設定項目「対象ディレクトリ」のバインド用プロパティ
    /// </summary>
    public string TargetDirectory
    {
        get => _targetDirectory;
        set
        {
            if (SetProperty(ref _targetDirectory, value))
            {
                // ディレクトリが空でなくなったらインデックスボタンを活性化させるため再評価
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>
    /// 設定項目「更新間隔（秒）」のバインド用プロパティ
    /// </summary>
    public int UpdateIntervalSeconds
    {
        get => _updateIntervalSeconds;
        set
        {
            if (SetProperty(ref _updateIntervalSeconds, value))
            {
                Logger.LogDebug("UpdateIntervalSeconds が変更されました: {NewValue}秒", value);
            }
        }
    }

    /// <summary>
    /// 自動更新の選択間隔オプションを表すデータモデルです。
    /// </summary>
    public record IntervalOption(string DisplayText, int Seconds);

    /// <summary>
    /// 設定画面の ComboBox に表示する更新間隔オプションのリストを取得します。
    /// </summary>
    public List<IntervalOption> IntervalOptions { get; } = new()
    {
        new IntervalOption("10秒", 10),
        new IntervalOption("15秒", 15),
        new IntervalOption("30秒", 30),
        new IntervalOption("1分", 60),
        new IntervalOption("2分", 120),
        new IntervalOption("3分", 180),
        new IntervalOption("5分", 300),
        new IntervalOption("15分", 900),
        new IntervalOption("30分", 1800),
        new IntervalOption("1時間", 3600),
        new IntervalOption("3時間", 10800),
        new IntervalOption("12時間", 43200),
        new IntervalOption("1日", 86400),
        new IntervalOption("2日", 172800),
        new IntervalOption("7日", 604800)
    };

    /// <summary>
    /// 設定項目「画像加工モード」のバインド用プロパティ
    /// </summary>
    public string ProcessingMode
    {
        get => _processingMode;
        set
        {
            if (SetProperty(ref _processingMode, value))
            {
                OnPropertyChanged(nameof(IsProcessingModeAdvanced));
                OnPropertyChanged(nameof(IsPostcardModeSelected));
            }
        }
    }

    /// <summary>
    /// 設定項目「自動更新有効フラグ」のバインド用プロパティ
    /// </summary>
    public bool AutoUpdateEnabled
    {
        get => _autoUpdateEnabled;
        set => SetProperty(ref _autoUpdateEnabled, value);
    }

    /// <summary>
    /// 合成に使用する画像数のバインド用プロパティ
    /// </summary>
    public int WallpaperImageCount
    {
        get => _wallpaperImageCount;
        set => SetProperty(ref _wallpaperImageCount, value);
    }

    /// <summary>
    /// 合成する画像の長辺のピクセルサイズを取得または設定します。
    /// </summary>
    public int WallpaperImageSize
    {
        get => _wallpaperImageSize;
        set => SetProperty(ref _wallpaperImageSize, value);
    }

    /// <summary>
    /// ポストカード加工時の背景カラーコードを取得または設定します。
    /// </summary>
    public string WallpaperBackgroundColor
    {
        get => _wallpaperBackgroundColor;
        set
        {
            if (SetProperty(ref _wallpaperBackgroundColor, value))
            {
                OnPropertyChanged(nameof(BackgroundColorBrush));
            }
        }
    }

    /// <summary>
    /// 設定画面のスライダーやTextBox背景色プレビューにバインドするためのブラシを取得します。
    /// </summary>
    public System.Windows.Media.Brush BackgroundColorBrush
    {
        get
        {
            try
            {
                var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_wallpaperBackgroundColor);
                return new System.Windows.Media.SolidColorBrush(color);
            }
            catch
            {
                return System.Windows.Media.Brushes.DarkSlateGray; // 解析失敗時のフォールバック
            }
        }
    }

    /// <summary>
    /// 合成の配置密度のバインド用プロパティ (0.5 〜 1.5 倍率)
    /// </summary>
    public double WallpaperDensity
    {
        get => _wallpaperDensity;
        set
        {
            if (SetProperty(ref _wallpaperDensity, value))
            {
                OnPropertyChanged(nameof(WallpaperDensityPercent));
            }
        }
    }

    /// <summary>
    /// 設定画面のスライダーバインド用のプロパティ (50 〜 150 パーセント値)
    /// </summary>
    public double WallpaperDensityPercent
    {
        get => _wallpaperDensity * 100.0;
        set
        {
            if (SetProperty(ref _wallpaperDensity, value / 100.0))
            {
                OnPropertyChanged(nameof(WallpaperDensity));
            }
        }
    }

    /// <summary>
    /// カラーピッカーを起動するコマンドを取得します。
    /// </summary>
    public ICommand SelectColorCommand { get; }

    /// <summary>
    /// ポストカード風モードが選択されているかどうかを示すプロパティ
    /// </summary>
    public bool IsPostcardModeSelected => ProcessingMode == "Postcard";

    /// <summary>
    /// ポストカード/コルクボードなどの高度な加工モードが選択されているかどうかを示すプロパティ
    /// </summary>
    public bool IsProcessingModeAdvanced => ProcessingMode == "Postcard" || ProcessingMode == "Corkboard";

    /// <summary>
    /// インデックス作成を実行中かどうかを示すバインド用プロパティ
    /// </summary>
    public bool IsIndexing
    {
        get => _isIndexing;
        private set
        {
            if (SetProperty(ref _isIndexing, value))
            {
                OnPropertyChanged(nameof(IsNotIndexing));
                // コマンドの実行可能状態を再評価
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>
    /// インデックス作成中でないことを示すヘルパープロパティ（ボタンの有効化状態に使用）
    /// </summary>
    public bool IsNotIndexing => !IsIndexing;

    /// <summary>
    /// プログレスバーに表示する進捗率（パーセント値）
    /// </summary>
    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    /// <summary>
    /// 進捗状態を文字で表示するプロパティ（例: "3020ファイル登録完了"）
    /// </summary>
    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    /// <summary>
    /// 設定を保存するコマンドを取得します。
    /// </summary>
    public ICommand SaveCommand { get; }

    /// <summary>
    /// インデックススキャンを開始するコマンドを取得します。
    /// </summary>
    public ICommand StartIndexingCommand { get; }

    /// <summary>
    /// 実行中のインデックススキャンをキャンセルするコマンドを取得します。
    /// </summary>
    public ICommand CancelIndexingCommand { get; }

    /// <summary>
    /// ビューのクローズを要求するイベントです。
    /// </summary>
    public event Action? RequestClose;

    /// <summary>
    /// デフォルトのインスタンスを初期化します。
    /// </summary>
    public MainWindowViewModel() : this(new SettingsManager(), new DatabaseManager())
    {
    }

    /// <summary>
    /// 依存オブジェクトを指定して ViewModel を初期化します。
    /// </summary>
    /// <param name="settingsManager">設定ファイルマネージャー</param>
    /// <param name="dbManager">データベースマネージャー</param>
    public MainWindowViewModel(SettingsManager settingsManager, DatabaseManager dbManager)
    {
        _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
        _dbManager = dbManager ?? throw new ArgumentNullException(nameof(dbManager));
        _indexer = new ImageBulkIndexer(_dbManager);

        SaveCommand = new RelayCommand(SaveSettings, () => !IsIndexing);
        SelectColorCommand = new RelayCommand(SelectColor);
        StartIndexingCommand = new RelayCommand(async () => await StartIndexingAsync(), () => !IsIndexing && !string.IsNullOrWhiteSpace(TargetDirectory));
        CancelIndexingCommand = new RelayCommand(CancelIndexing, () => IsIndexing);

        // 保存された設定のロード
        LoadSettings();
    }

    /// <summary>
    /// 設定ファイルからデータを読み込み、ViewModelのプロパティに適用します。
    /// </summary>
    public void LoadSettings()
    {
        Logger.LogInformation("設定ファイルからのロードを開始します。");
        var settings = _settingsManager.Load();
        TargetDirectory = settings.TargetDirectory;
        UpdateIntervalSeconds = settings.UpdateIntervalSeconds;
        ProcessingMode = settings.ProcessingMode;
        AutoUpdateEnabled = settings.AutoUpdateEnabled;
        WallpaperImageCount = settings.WallpaperImageCount;
        WallpaperImageSize = settings.WallpaperImageSize;
        WallpaperDensity = settings.WallpaperDensity;
        WallpaperBackgroundColor = settings.WallpaperBackgroundColor;
        
        Logger.LogInformation("設定ファイルのロードが完了しました: TargetDirectory={TargetDirectory}, UpdateIntervalSeconds={UpdateIntervalSeconds}秒, AutoUpdateEnabled={AutoUpdateEnabled}", 
            TargetDirectory, UpdateIntervalSeconds, AutoUpdateEnabled);

        ProgressValue = 0;
        ProgressText = "設定を読み込みました。";
    }

    /// <summary>
    /// カラーダイアログを表示して背景色を選択します。
    /// </summary>
    private void SelectColor()
    {
        using var dialog = new System.Windows.Forms.ColorDialog();
        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(WallpaperBackgroundColor);
            dialog.Color = System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
        }
        catch
        {
            dialog.Color = System.Drawing.Color.FromArgb(47, 79, 79); // デフォルト #2F4F4F
        }

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            WallpaperBackgroundColor = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        }
    }

    /// <summary>
    /// 現在のプロパティ値をもとに設定をローカルファイルに保存します。
    /// </summary>
    private async void SaveSettings()
    {
        Logger.LogInformation("設定の保存処理を開始します。");
        try
        {
            var settings = new AppSettings
            {
                TargetDirectory = TargetDirectory,
                UpdateIntervalSeconds = UpdateIntervalSeconds,
                ProcessingMode = ProcessingMode,
                AutoUpdateEnabled = AutoUpdateEnabled,
                WallpaperImageCount = WallpaperImageCount,
                WallpaperImageSize = WallpaperImageSize,
                WallpaperDensity = WallpaperDensity,
                WallpaperBackgroundColor = WallpaperBackgroundColor
            };
            Logger.LogInformation("保存対象の設定データ: TargetDirectory={TargetDirectory}, UpdateIntervalSeconds={UpdateIntervalSeconds}秒, AutoUpdateEnabled={AutoUpdateEnabled}", 
                settings.TargetDirectory, settings.UpdateIntervalSeconds, settings.AutoUpdateEnabled);

            _settingsManager.Save(settings);
            Logger.LogInformation("設定ファイルのディスク保存に成功しました。");

            OS.WallpaperManager.Instance.RestartTimer();

            // 新しいモード等をもとに即時に事前レンダリングバッファを再作成
            await OS.WallpaperManager.Instance.RecreatePreRenderedWallpaperAsync();

            System.Windows.MessageBox.Show("設定を適用・保存しました。", "DecoDesk", MessageBoxButton.OK, MessageBoxImage.Information);
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "設定の保存またはタイマーの再起動中に例外が発生しました。");
            System.Windows.MessageBox.Show($"設定の保存に失敗しました: {ex.Message}", "DecoDesk", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// インデックス構築を非同期で開始します。
    /// </summary>
    private async Task StartIndexingAsync()
    {
        if (string.IsNullOrWhiteSpace(TargetDirectory)) return;

        IsIndexing = true;
        ProgressValue = 0;
        ProgressText = "走査開始...";
        _indexingCts = new CancellationTokenSource();

        var progress = new Progress<ImageBulkIndexer.IndexProgress>(p =>
        {
            ProgressText = $"{p.FoundCount}ファイル検出、{p.IndexedCount}ファイルをデータベースに登録完了";
            // スキャンの進捗率は総ファイル数が判明するまでは推測値となるため、
            // 登録数に合わせてUIの進捗表記を更新（1ファイルあたり一定のプログレス値でインジケータを駆動）
            ProgressValue = p.IndexedCount % 100; // シンプルなループアニメーション効果
        });

        try
        {
            await Task.Run(async () =>
            {
                await _indexer.RunIndexAsync(TargetDirectory, progress, _indexingCts.Token);
            });
            ProgressText = "インデックスの同期が正常に完了しました。";
            ProgressValue = 100;
            System.Windows.MessageBox.Show("画像データベースの同期が完了しました。", "DecoDesk", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            ProgressText = "インデックスの更新がキャンセルされました。";
            ProgressValue = 0;
            System.Windows.MessageBox.Show("インデックス同期をキャンセルしました。", "DecoDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ProgressText = $"エラー: {ex.Message}";
            ProgressValue = 0;
            System.Windows.MessageBox.Show($"インデックス同期中にエラーが発生しました:\n{ex.Message}", "DecoDesk", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _indexingCts?.Dispose();
            _indexingCts = null;
            IsIndexing = false;
        }
    }

    /// <summary>
    /// 実行中のインデックススキャンをキャンセルします。
    /// </summary>
    private void CancelIndexing()
    {
        _indexingCts?.Cancel();
    }
}
