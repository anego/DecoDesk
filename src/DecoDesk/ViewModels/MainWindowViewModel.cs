using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DecoDesk.Configuration;
using DecoDesk.Database;

namespace DecoDesk.ViewModels;

/// <summary>
/// 設定画面 (MainWindow) のデータバインディングとコマンド処理を行う ViewModel です。
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly SettingsManager _settingsManager;
    private readonly DatabaseManager _dbManager;
    private readonly ImageBulkIndexer _indexer;

    private string _targetDirectory = string.Empty;
    private int _updateIntervalMinutes = 15;
    private string _processingMode = "None";
    private bool _autoUpdateEnabled;

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
    /// 設定項目「更新間隔（分）」のバインド用プロパティ
    /// </summary>
    public int UpdateIntervalMinutes
    {
        get => _updateIntervalMinutes;
        set => SetProperty(ref _updateIntervalMinutes, value);
    }

    /// <summary>
    /// 設定項目「画像加工モード」のバインド用プロパティ
    /// </summary>
    public string ProcessingMode
    {
        get => _processingMode;
        set => SetProperty(ref _processingMode, value);
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
        var settings = _settingsManager.Load();
        TargetDirectory = settings.TargetDirectory;
        UpdateIntervalMinutes = settings.UpdateIntervalMinutes;
        ProcessingMode = settings.ProcessingMode;
        AutoUpdateEnabled = settings.AutoUpdateEnabled;
        
        ProgressValue = 0;
        ProgressText = "設定を読み込みました。";
    }

    /// <summary>
    /// 現在のプロパティ値をもとに設定をローカルファイルに保存します。
    /// </summary>
    private void SaveSettings()
    {
        try
        {
            var settings = new AppSettings
            {
                TargetDirectory = TargetDirectory,
                UpdateIntervalMinutes = UpdateIntervalMinutes,
                ProcessingMode = ProcessingMode,
                AutoUpdateEnabled = AutoUpdateEnabled
            };
            _settingsManager.Save(settings);
            System.Windows.MessageBox.Show("設定を適用・保存しました。", "DecoDesk", MessageBoxButton.OK, MessageBoxImage.Information);
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
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
