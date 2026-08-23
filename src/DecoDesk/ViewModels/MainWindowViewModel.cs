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

    private System.Collections.ObjectModel.ObservableCollection<string> _targetDirectories = new();
    private string? _selectedDirectory;
    private int _updateIntervalSeconds = 900;
    private string _processingMode = "None";
    private bool _autoUpdateEnabled;
    private int _wallpaperImageCount = 6;
    private int _wallpaperImageSize = 350;
    private double _wallpaperDensity = 1.0;
    private string _wallpaperBackgroundColor = "#2F4F4F";
    private bool _startWithWindows;
    private bool _updateWallpaperOnStartup;
    private int _startupDelaySeconds = 60;

    private bool _isIndexing;
    private double _progressValue;
    private string _progressText = "未スキャン";
    private CancellationTokenSource? _indexingCts;

    private bool _isDirty;
    private bool _isRenderDirty;
    private bool _isLoaded;

    /// <summary>
    /// 設定項目「対象ディレクトリ（複数）」のバインド用プロパティ
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<string> TargetDirectories
    {
        get => _targetDirectories;
        set => SetProperty(ref _targetDirectories, value);
    }

    /// <summary>
    /// リストから選択されているディレクトリパス
    /// </summary>
    public string? SelectedDirectory
    {
        get => _selectedDirectory;
        set
        {
            if (SetProperty(ref _selectedDirectory, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>
    /// 互換用・コマンド判定用：設定項目「対象ディレクトリ（単一）」のプロパティ
    /// </summary>
    public string TargetDirectory => TargetDirectories.Count > 0 ? TargetDirectories[0] : string.Empty;

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
                MarkDirty();
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
                MarkDirty(affectsRendering: true);
            }
        }
    }

    /// <summary>
    /// 設定項目「自動更新有効フラグ」のバインド用プロパティ
    /// </summary>
    public bool AutoUpdateEnabled
    {
        get => _autoUpdateEnabled;
        set
        {
            if (SetProperty(ref _autoUpdateEnabled, value))
            {
                MarkDirty();
            }
        }
    }

    /// <summary>
    /// 設定項目「Windows自動起動」のバインド用プロパティ
    /// </summary>
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (SetProperty(ref _startWithWindows, value))
            {
                MarkDirty();
            }
        }
    }

    /// <summary>
    /// 設定項目「起動時壁紙更新」のバインド用プロパティ
    /// </summary>
    public bool UpdateWallpaperOnStartup
    {
        get => _updateWallpaperOnStartup;
        set
        {
            if (SetProperty(ref _updateWallpaperOnStartup, value))
            {
                OnPropertyChanged(nameof(IsStartupDelaySliderEnabled));
                MarkDirty();
            }
        }
    }

    /// <summary>
    /// 設定項目「起動時壁紙更新のディレイ時間（秒）」のバインド用プロパティ
    /// </summary>
    public int StartupDelaySeconds
    {
        get => _startupDelaySeconds;
        set
        {
            if (SetProperty(ref _startupDelaySeconds, value))
            {
                MarkDirty();
            }
        }
    }

    /// <summary>
    /// 合成に使用する画像数のバインド用プロパティ
    /// </summary>
    public int WallpaperImageCount
    {
        get => _wallpaperImageCount;
        set
        {
            if (SetProperty(ref _wallpaperImageCount, value))
            {
                MarkDirty(affectsRendering: true);
            }
        }
    }

    /// <summary>
    /// 合成する画像の長辺のピクセルサイズを取得または設定します。
    /// </summary>
    public int WallpaperImageSize
    {
        get => _wallpaperImageSize;
        set
        {
            if (SetProperty(ref _wallpaperImageSize, value))
            {
                MarkDirty(affectsRendering: true);
            }
        }
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
                MarkDirty(affectsRendering: true);
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
                MarkDirty(affectsRendering: true);
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
                MarkDirty(affectsRendering: true);
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
    public bool IsPostcardModeSelected => IsNotIndexing && ProcessingMode == "Postcard";

    /// <summary>
    /// ポストカード/コルクボードなどの高度な加工モードが選択されているかどうかを示すプロパティ
    /// </summary>
    public bool IsProcessingModeAdvanced => IsNotIndexing && (ProcessingMode == "Postcard" || ProcessingMode == "Corkboard");

    /// <summary>
    /// 起動時適用ディレイスライダーが有効かどうかを示すプロパティ
    /// </summary>
    public bool IsStartupDelaySliderEnabled => IsNotIndexing && UpdateWallpaperOnStartup;

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
                OnPropertyChanged(nameof(IsProcessingModeAdvanced));
                OnPropertyChanged(nameof(IsPostcardModeSelected));
                OnPropertyChanged(nameof(IsStartupDelaySliderEnabled));
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
    /// 進捗状態を文字で表示するプロパティ（例: "3,020ファイル登録完了"）
    /// </summary>
    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    /// <summary>
    /// 読み込み後に未保存の変更があるかどうかを示すプロパティ（保存ボタンの有効化状態に使用）
    /// </summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
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
    /// 登録ディレクトリをリストから削除するコマンドを取得します。
    /// </summary>
    public ICommand RemoveDirectoryCommand { get; }

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

        SaveCommand = new RelayCommand(SaveSettings, () => !IsIndexing && IsDirty);
        SelectColorCommand = new RelayCommand(SelectColor);
        StartIndexingCommand = new RelayCommand(async () => await StartIndexingAsync(), () => !IsIndexing && TargetDirectories.Count > 0);
        CancelIndexingCommand = new RelayCommand(CancelIndexing, () => IsIndexing);
        RemoveDirectoryCommand = new RelayCommand(RemoveDirectory, () => !IsIndexing && SelectedDirectory != null);

        // 対象ディレクトリの追加・削除も「未保存の変更」として検知する（壁紙合成に影響するため再レンダリング対象）
        _targetDirectories.CollectionChanged += (_, _) => MarkDirty(affectsRendering: true);

        // 保存された設定のロード
        LoadSettings();
    }

    /// <summary>
    /// 設定読み込み完了後の変更を「未保存」としてマークします。読み込み処理中の変更は対象外とします。
    /// </summary>
    /// <param name="affectsRendering">壁紙の見た目（合成結果）に影響する設定項目の変更かどうか</param>
    private void MarkDirty(bool affectsRendering = false)
    {
        if (!_isLoaded)
        {
            return;
        }

        IsDirty = true;
        if (affectsRendering)
        {
            _isRenderDirty = true;
        }
    }

    /// <summary>
    /// 設定ファイルからデータを読み込み、ViewModelのプロパティに適用します。
    /// </summary>
    public void LoadSettings()
    {
        Logger.LogInformation("設定ファイルからのロードを開始します。");
        _isLoaded = false;
        var settings = _settingsManager.Load();

        TargetDirectories.Clear();
        foreach (var dir in settings.TargetDirectories)
        {
            TargetDirectories.Add(dir);
        }

        UpdateIntervalSeconds = settings.UpdateIntervalSeconds;
        ProcessingMode = settings.ProcessingMode;
        AutoUpdateEnabled = settings.AutoUpdateEnabled;
        WallpaperImageCount = settings.WallpaperImageCount;
        WallpaperImageSize = settings.WallpaperImageSize;
        WallpaperDensity = settings.WallpaperDensity;
        WallpaperBackgroundColor = settings.WallpaperBackgroundColor;

        // レジストリ状態と設定ファイル状態の同期
        settings.StartWithWindows = OS.StartupManager.IsStartupEnabled();
        StartWithWindows = settings.StartWithWindows;

        UpdateWallpaperOnStartup = settings.UpdateWallpaperOnStartup;
        StartupDelaySeconds = settings.StartupDelaySeconds;
        
        Logger.LogInformation("設定ファイルのロードが完了しました: TargetDirectoriesCount={DirectoriesCount}, UpdateIntervalSeconds={UpdateIntervalSeconds}秒, AutoUpdateEnabled={AutoUpdateEnabled}, StartWithWindows={StartWithWindows}", 
            TargetDirectories.Count, UpdateIntervalSeconds, AutoUpdateEnabled, StartWithWindows);

        ProgressValue = 0;
        ProgressText = "設定を読み込みました。";

        _isLoaded = true;
        IsDirty = false;
        _isRenderDirty = false;
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
    private void SaveSettings()
    {
        Logger.LogInformation("設定の保存処理を開始します。");
        try
        {
            var settings = new AppSettings
            {
                TargetDirectories = new System.Collections.Generic.List<string>(TargetDirectories),
                UpdateIntervalSeconds = UpdateIntervalSeconds,
                ProcessingMode = ProcessingMode,
                AutoUpdateEnabled = AutoUpdateEnabled,
                WallpaperImageCount = WallpaperImageCount,
                WallpaperImageSize = WallpaperImageSize,
                WallpaperDensity = WallpaperDensity,
                WallpaperBackgroundColor = WallpaperBackgroundColor,
                StartWithWindows = StartWithWindows,
                UpdateWallpaperOnStartup = UpdateWallpaperOnStartup,
                StartupDelaySeconds = StartupDelaySeconds
            };
            Logger.LogInformation("保存対象の設定データ: TargetDirectoriesCount={DirectoriesCount}, UpdateIntervalSeconds={UpdateIntervalSeconds}秒, AutoUpdateEnabled={AutoUpdateEnabled}, StartWithWindows={StartWithWindows}", 
                settings.TargetDirectories.Count, settings.UpdateIntervalSeconds, settings.AutoUpdateEnabled, settings.StartWithWindows);

            _settingsManager.Save(settings);
            Logger.LogInformation("設定ファイルのディスク保存に成功しました。");

            // レジストリへのスタートアップ登録/解除反映
            OS.StartupManager.SetStartup(StartWithWindows);

            OS.WallpaperManager.Instance.RestartTimer();

            // 壁紙の見た目に影響する設定（対象ディレクトリ、加工モード、枚数/サイズ/密度/背景色）が
            // 変更された場合のみ、時間のかかる事前レンダリングバッファの再作成が必要となる
            bool needsRerender = _isRenderDirty;

            IsDirty = false;
            _isRenderDirty = false;

            if (needsRerender)
            {
                System.Windows.MessageBox.Show("設定を保存しました。壁紙の再生成をバックグラウンドで実行します。完了するとタスクトレイに通知します。", "DecoDesk", MessageBoxButton.OK, MessageBoxImage.Information);
                RequestClose?.Invoke();

                // 画面を閉じた後もバックグラウンドで継続し、完了はタスクトレイ通知で知らせる（設定画面をブロックしない）
                _ = Task.Run(async () =>
                {
                    try
                    {
                        Logger.LogInformation("壁紙の見た目に影響する設定変更を検知したため、事前レンダリングバッファをバックグラウンドで再作成します。");
                        await OS.WallpaperManager.Instance.RecreatePreRenderedWallpaperAsync();
                        Logger.LogInformation("バックグラウンドでの事前レンダリングバッファの再作成が完了しました。");
                        NotifyIconWrapper.Current?.ShowBalloonNotification("DecoDesk", "壁紙の再生成が完了しました。");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "バックグラウンドでの事前レンダリングバッファの再作成中に例外が発生しました。");
                        NotifyIconWrapper.Current?.ShowBalloonNotification("DecoDesk", $"壁紙の再生成に失敗しました: {ex.Message}", System.Windows.Forms.ToolTipIcon.Error);
                    }
                });
            }
            else
            {
                Logger.LogInformation("壁紙の見た目に影響する設定変更がなかったため、事前レンダリングの再作成をスキップしました。");
                System.Windows.MessageBox.Show("設定を適用・保存しました。", "DecoDesk", MessageBoxButton.OK, MessageBoxImage.Information);
                RequestClose?.Invoke();
            }
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
        if (TargetDirectories.Count == 0) return;

        IsIndexing = true;
        ProgressValue = 0;
        ProgressText = "走査開始...";
        _indexingCts = new CancellationTokenSource();

        ImageBulkIndexer.IndexProgress? lastProgress = null;
        var progress = new Progress<ImageBulkIndexer.IndexProgress>(p =>
        {
            lastProgress = p;
            ProgressText = $"{p.FoundCount:N0}ファイル検出、{p.IndexedCount:N0}ファイルをデータベースに登録完了";
            // スキャンの進捗率は総ファイル数が判明するまでは推測値となるため、
            // 登録数に合わせてUIの進捗表記を更新（1ファイルあたり一定のプログレス値でインジケータを駆動）
            ProgressValue = p.IndexedCount % 100; // シンプルなループアニメーション効果
        });

        try
        {
            await Task.Run(async () =>
            {
                await _indexer.RunIndexAsync(TargetDirectories, progress, _indexingCts.Token);
            });
            ProgressText = "インデックスの同期が正常に完了しました。";
            ProgressValue = 100;

            // 設定画面を閉じていても完了が分かるよう、タスクトレイ通知で知らせる（モーダルダイアログは表示しない）
            var indexedCount = lastProgress?.IndexedCount ?? 0;
            NotifyIconWrapper.Current?.ShowBalloonNotification("DecoDesk", $"画像データベースの同期が完了しました。（{indexedCount:N0}ファイルを登録）");
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

    /// <summary>
    /// リストから選択されているディレクトリパスを削除します。
    /// </summary>
    private void RemoveDirectory()
    {
        if (SelectedDirectory != null)
        {
            TargetDirectories.Remove(SelectedDirectory);
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
