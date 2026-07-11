using System.Threading;
using System.Windows;

namespace DecoDesk;

/// <summary>
/// アプリケーションのエントリーポイントおよびライフサイクル管理を行うクラスです。
/// </summary>
public partial class App : System.Windows.Application
{
    private static Mutex? _mutex;
    private const string MutexName = "DecoDesk_SingleInstance_Mutex";
    private NotifyIconWrapper? _notifyIconWrapper;

    /// <summary>
    /// アプリケーション起動時の初期化処理を行います。
    /// </summary>
    /// <param name="sender">イベントの発生元</param>
    /// <param name="e">イベント引数</param>
    private async void App_OnStartup(object sender, StartupEventArgs e)
    {
        // 二重起動の検知と防止
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        // メインウィンドウ（設定画面）の生成
        var mainWindow = new MainWindow();

        // タスクトレイ常駐アイコンの生成
        _notifyIconWrapper = new NotifyIconWrapper(mainWindow);

        // 壁紙エンジンの非同期初期化
        try
        {
            await OS.WallpaperManager.Instance.InitializeAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"壁紙エンジンの初期化に失敗しました:\n{ex.Message}", "DecoDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// アプリケーション終了時のクリーンアップ処理を行います。
    /// </summary>
    /// <param name="e">イベントの引数</param>
    protected override void OnExit(ExitEventArgs e)
    {
        // 常駐アイコンのリソース解放
        _notifyIconWrapper?.Dispose();

        // 壁紙マネージャーのリソース解放
        OS.WallpaperManager.Instance.Dispose();

        // Mutexの解放
        if (_mutex != null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (SemaphoreFullException)
            {
                // Mutexが既に解放されている場合の例外保護
            }
            finally
            {
                _mutex.Dispose();
            }
        }

        base.OnExit(e);
    }
}

