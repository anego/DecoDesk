using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DecoDesk.Configuration;
using DecoDesk.Database;
using DecoDesk.Rendering;
using SkiaSharp;
using Microsoft.Extensions.Logging;

namespace DecoDesk.OS;

/// <summary>
/// 壁紙の自動更新スケジュール、事前レンダリング、および Windows API との連携を統括するマネージャークラスです。
/// </summary>
public sealed class WallpaperManager : IDisposable
{
    private static readonly Lazy<WallpaperManager> LazyInstance = new(() => new WallpaperManager());
    private static readonly ILogger Logger = AppLoggerFactory.CreateLogger<WallpaperManager>();

    /// <summary>
    /// <see cref="WallpaperManager"/> のシングルトンインスタンスを取得します。
    /// </summary>
    public static WallpaperManager Instance => LazyInstance.Value;

    private readonly SettingsManager _settingsManager;
    private readonly DatabaseManager _dbManager;
    private readonly WallpaperRenderer _renderer;
    private readonly string _tempDir;
    private readonly object _timerLock = new();

    private System.Threading.Timer? _updateTimer;
    private List<MonitorAreaInfo> _monitors = new();
    private bool _isPreRenderedReady;
    private bool _isUpdating;
    private bool _disposed;

    private readonly string _spanPathA;
    private readonly string _spanPathB;
    private bool _useA = true;

    /// <summary>
    /// 各モニターの物理配置座標および解像度情報を保持する内部クラスです。
    /// </summary>
    private sealed class MonitorAreaInfo
    {
        public string MonitorId { get; }
        public int Left { get; }
        public int Top { get; }
        public int Width { get; }
        public int Height { get; }

        public MonitorAreaInfo(string monitorId, int left, int top, int width, int height)
        {
            MonitorId = monitorId;
            Left = left;
            Top = top;
            Width = width;
            Height = height;
        }
    }

    private string GetNextSpanPath() => _useA ? _spanPathA : _spanPathB;
    private void ToggleSpan() => _useA = !_useA;

    private WallpaperManager()
    {
        _settingsManager = new SettingsManager();
        _dbManager = new DatabaseManager();
        _renderer = new WallpaperRenderer();

        _tempDir = Path.Combine(Path.GetTempPath(), "DecoDesk");
        if (!Directory.Exists(_tempDir))
        {
            Directory.CreateDirectory(_tempDir);
        }

        _spanPathA = Path.Combine(_tempDir, "wp_span_A.png");
        _spanPathB = Path.Combine(_tempDir, "wp_span_B.png");
    }

    /// <summary>
    /// 壁紙マネージャーを初期化し、ディスプレイ構成のロードと初期タイマーのセットアップを行います。
    /// </summary>
    public async Task InitializeAsync()
    {
        await _dbManager.InitializeAsync();
        RefreshMonitorInfo();

        // 起動時に次回分の壁紙を事前レンダリングしておく
        await PreRenderNextWallpaperAsync();

        // 自動更新タイマーの起動
        RestartTimer();
    }

    /// <summary>
    /// 現在のシステムに接続されているディスプレイ構成情報をもとにバッファ情報を更新します。
    /// </summary>
    public void RefreshMonitorInfo()
    {
        try
        {
            var activeMonitors = WallpaperInterop.GetDisplayMonitors();
            
            // 既存のモニター構成と比較し、数や位置、解像度、IDに変更がない場合は再構築をスキップしてダブルバッファ状態を維持
            if (_monitors.Count == activeMonitors.Count)
            {
                bool isMatched = true;
                for (int i = 0; i < _monitors.Count; i++)
                {
                    if (_monitors[i].MonitorId != activeMonitors[i].MonitorId ||
                        _monitors[i].Left != activeMonitors[i].Left ||
                        _monitors[i].Top != activeMonitors[i].Top ||
                        _monitors[i].Width != activeMonitors[i].Width ||
                        _monitors[i].Height != activeMonitors[i].Height)
                    {
                        isMatched = false;
                        break;
                    }
                }
                if (isMatched)
                {
                    return; // 変更なし
                }
            }

            var newMonitors = new List<MonitorAreaInfo>();
            for (int i = 0; i < activeMonitors.Count; i++)
            {
                var m = activeMonitors[i];
                newMonitors.Add(new MonitorAreaInfo(m.MonitorId, m.Left, m.Top, m.Width, m.Height));
            }

            _monitors = newMonitors;
            _isPreRenderedReady = false;
        }
        catch
        {
            // ディスプレイ取得エラー時は、フォールバックとしてプライマリのダミーステートを構築
            // すでにプライマリのみのステートがある場合は再構築をスキップ
            if (_monitors.Count == 1 && _monitors[0].MonitorId == null)
            {
                return;
            }

            _monitors = new List<MonitorAreaInfo>
            {
                new MonitorAreaInfo(null!, 0, 0, 1920, 1080)
            };
            _isPreRenderedReady = false;
        }
    }

    /// <summary>
    /// 設定ファイルの自動更新設定をロードし、タイマーを再起動します。
    /// </summary>
    public void RestartTimer()
    {
        lock (_timerLock)
        {
            Logger.LogInformation("壁紙自動更新タイマーの再起動処理を開始します。");
            _updateTimer?.Dispose();
            _updateTimer = null;

            var settings = _settingsManager.Load();
            Logger.LogInformation("現在の設定状況: AutoUpdateEnabled={AutoUpdateEnabled}, UpdateIntervalSeconds={UpdateIntervalSeconds}秒", settings.AutoUpdateEnabled, settings.UpdateIntervalSeconds);

            if (settings.AutoUpdateEnabled && settings.UpdateIntervalSeconds > 0)
            {
                var interval = TimeSpan.FromSeconds(settings.UpdateIntervalSeconds);
                // タイマー開始（初回発火は設定時間後）
                _updateTimer = new System.Threading.Timer(
                    async state => await OnTimerFiredAsync(),
                    null,
                    interval,
                    interval);
                Logger.LogInformation("壁紙自動更新タイマーを起動しました。周期={IntervalSeconds}秒", settings.UpdateIntervalSeconds);
            }
            else
            {
                Logger.LogWarning("自動更新が無効、または間隔値が不正なためタイマーは起動されませんでした。");
            }
        }
    }

    /// <summary>
    /// 設定変更時などに、既存の事前レンダリングバッファを強制クリアして即座に非同期で次回分の壁紙を再生成します。
    /// </summary>
    public async Task RecreatePreRenderedWallpaperAsync()
    {
        _isPreRenderedReady = false;
        await PreRenderNextWallpaperAsync();
    }

    /// <summary>
    /// タイマー発火時のコールバック。非同期で壁紙更新を実行します。
    /// </summary>
    private async Task OnTimerFiredAsync()
    {
        await TriggerUpdateNowAsync();
    }

    /// <summary>
    /// 事前レンダリング済みの画像を Windows デスクトップに即時適用し、
    /// 適用完了後に次回分の壁紙を裏で非同期生成します（ゼロ・レイテンシ適用）。
    /// </summary>
    public async Task TriggerUpdateNowAsync()
    {
        if (_isUpdating) return;
        _isUpdating = true;

        try
        {
            // 1. 事前レンダリング画像が未準備の場合は、その場で即時生成（フォールバック）
            var nextPath = GetNextSpanPath();
            if (!_isPreRenderedReady || !File.Exists(nextPath))
            {
                await PreRenderNextWallpaperAsync();
                nextPath = GetNextSpanPath();
            }

            // 2. 巨大スパン画像を Windows に適用
            if (File.Exists(nextPath))
            {
                WallpaperInterop.SetSpanWallpaper(nextPath);
                ToggleSpan(); // 適用したバッファをアクティブ面に切り替え
            }

            // 3. 適用完了後、バックグラウンドスレッドで「次回適用分」のレンダリングを開始
            _isPreRenderedReady = false;
            _ = Task.Run(async () =>
            {
                try
                {
                    await PreRenderNextWallpaperAsync();
                }
                catch
                {
                    // バックグラウンド生成の例外は上位に伝播させない
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"壁紙更新処理中にエラーが発生しました: {ex.Message}");
        }
        finally
        {
            _isUpdating = false;
        }
    }

    /// <summary>
    /// 次回適用するための壁紙画像を、各ディスプレイの配置状態に合わせて1枚の巨大なスパン壁紙として事前レンダリングします。
    /// </summary>
    private async Task PreRenderNextWallpaperAsync()
    {
        // 壁紙作成の直前に常に最新のディスプレイ構成情報を反映する
        RefreshMonitorInfo();

        var settings = _settingsManager.Load();
        var targetDir = settings.TargetDirectory;

        // 対象フォルダ未設定、または画像がDBに登録されていない場合は処理不可
        if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir) || !_monitors.Any())
        {
            return;
        }

        // 1. 全モニターの配置矩形を包含するバウンディングボックスの算出
        int minLeft = _monitors.Min(m => m.Left);
        int minTop = _monitors.Min(m => m.Top);
        int maxRight = _monitors.Max(m => m.Left + m.Width);
        int maxBottom = _monitors.Max(m => m.Top + m.Height);

        int canvasWidth = maxRight - minLeft;
        int canvasHeight = maxBottom - minTop;

        // 巨大なスパン用ビットマップを作成
        var spanBitmap = new SKBitmap(canvasWidth, canvasHeight);
        using var spanCanvas = new SKCanvas(spanBitmap);
        
        // 隙間の背景色を設定（ポストカード背景色またはデフォルトのシックな色、コルクボードの時はコルク色）
        var bgColor = SKColors.Black;
        if (settings.ProcessingMode == "Postcard" && 
            !string.IsNullOrWhiteSpace(settings.WallpaperBackgroundColor) && 
            SKColor.TryParse(settings.WallpaperBackgroundColor, out var parsedColor))
        {
            bgColor = parsedColor;
        }
        else if (settings.ProcessingMode == "Corkboard")
        {
            bgColor = new SKColor(194, 154, 106); // コルク背景色 #C29A6A
        }
        spanCanvas.Clear(bgColor);

        // 2. モニターごとに個別に壁紙を描画して、巨大キャンバスの対応する座標に貼り付ける
        foreach (var monitor in _monitors)
        {
            int imageCountNeeded = settings.ProcessingMode switch
            {
                "None" => 1,
                "Postcard" => settings.WallpaperImageCount,
                "Corkboard" => settings.WallpaperImageCount,
                _ => 1
            };

            double monitorRatio = (double)monitor.Width / monitor.Height;
            double? minRatio = null;
            double? maxRatio = null;

            if (settings.ProcessingMode == "None")
            {
                minRatio = monitorRatio * 0.8;
                maxRatio = monitorRatio * 1.2;
            }

            var imagePaths = await GetRandomImagePathsAsync(imageCountNeeded, minRatio, maxRatio);
            if (!imagePaths.Any())
            {
                imagePaths = await GetRandomImagePathsAsync(imageCountNeeded, null, null);
            }

            if (!imagePaths.Any())
            {
                continue;
            }

            // モニター用の合成画像を生成
            using SKBitmap monitorBitmap = settings.ProcessingMode switch
            {
                "None" => _renderer.CreateGridComposite(imagePaths, monitor.Width, monitor.Height),
                "Postcard" => _renderer.CreateRandomComposite(imagePaths, monitor.Width, monitor.Height, settings.WallpaperImageSize, settings.WallpaperDensity, settings.WallpaperBackgroundColor),
                "Corkboard" => _renderer.CreateCorkboardComposite(imagePaths, monitor.Width, monitor.Height, settings.WallpaperImageSize, settings.WallpaperDensity),
                _ => _renderer.CreateGridComposite(imagePaths, monitor.Width, monitor.Height)
            };

            // 巨大スパン用キャンバスの、各モニターの配置位置に描画 (オフセット変換)
            float destX = monitor.Left - minLeft;
            float destY = monitor.Top - minTop;
            spanCanvas.DrawBitmap(monitorBitmap, destX, destY, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), null);
        }

        // 次回適用予定のスロットに PNG として書き出し
        var nextPath = GetNextSpanPath();
        using var image = SKImage.FromBitmap(spanBitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 95);
        using var stream = new FileStream(nextPath, FileMode.Create, FileAccess.Write, FileShare.None);
        data.SaveTo(stream);

        _isPreRenderedReady = true;
    }

    /// <summary>
    /// 条件に合致する画像をデータベースからランダムに指定枚数取得し、使用日時を現在に更新します。
    /// </summary>
    private async Task<List<string>> GetRandomImagePathsAsync(int count, double? minRatio, double? maxRatio)
    {
        var paths = new List<string>();
        // 連続して同じ画像が選ばれないように一時的にチェック
        for (int i = 0; i < count; i++)
        {
            var record = await _dbManager.GetRandomImageAsync(minRatio, maxRatio, null);
            if (record != null && !paths.Contains(record.Path))
            {
                paths.Add(record.Path);
                // 選ばれた画像の使用履歴を更新
                await UpdateImageLastUsedAsync(record.Path);
            }
        }
        return paths;
    }

    /// <summary>
    /// 指定された画像の使用履歴（LastUsed）を現在時間に更新します。
    /// </summary>
    private async Task UpdateImageLastUsedAsync(string path)
    {
        try
        {
            using var connection = _dbManager.CreateConnection();
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE Images SET LastUsed = @LastUsed WHERE Path = @Path;";
            command.Parameters.AddWithValue("@LastUsed", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("@Path", path);
            await command.ExecuteNonQueryAsync();
        }
        catch
        {
            // DB更新エラーは処理を中断させないために握り潰す
        }
    }

    /// <summary>
    /// リソースを破棄します。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        lock (_timerLock)
        {
            _updateTimer?.Dispose();
            _updateTimer = null;
        }

        _disposed = true;
    }
}
