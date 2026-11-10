using System;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;

namespace DecoDesk;

/// <summary>
/// タスクトレイ常駐（NotifyIcon）およびコンテキストメニューの管理を行うクラスです。
/// </summary>
public sealed class NotifyIconWrapper : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly MainWindow _mainWindow;
    private ToolStripMenuItem? _pauseAutoUpdateItem;
    private bool _disposed;

    /// <summary>
    /// アプリケーション全体で唯一生成される <see cref="NotifyIconWrapper"/> のインスタンスを取得します。
    /// バックグラウンド処理（インデックス作成など）からタスクトレイ通知を表示するために使用します。
    /// </summary>
    public static NotifyIconWrapper? Current { get; private set; }

    /// <summary>
    /// <see cref="NotifyIconWrapper"/> クラスの新しいインスタンスを初期化します。
    /// </summary>
    /// <param name="mainWindow">設定画面として表示するメインウィンドウ</param>
    public NotifyIconWrapper(MainWindow mainWindow)
    {
        _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
        Current = this;

        // NotifyIconの初期化。埋め込みリソースからアイコンを読み込む
        _notifyIcon = new NotifyIcon
        {
            Text = "DecoDesk",
            Visible = true
        };

        try
        {
            var uri = new Uri("pack://application:,,,/icon.ico");
            var streamResourceInfo = System.Windows.Application.GetResourceStream(uri);
            if (streamResourceInfo != null)
            {
                using var stream = streamResourceInfo.Stream;
                _notifyIcon.Icon = new System.Drawing.Icon(stream);
            }
            else
            {
                _notifyIcon.Icon = SystemIcons.Application;
            }
        }
        catch
        {
            _notifyIcon.Icon = SystemIcons.Application;
        }

        // ダブルクリックで設定画面を表示
        _notifyIcon.DoubleClick += (s, e) => ShowSettings();

        // コンテキストメニューの作成
        var contextMenu = new ContextMenuStrip();

        var changeNowItem = new ToolStripMenuItem("今すぐ変更");
        changeNowItem.Click += ChangeWallpaperNow;
        contextMenu.Items.Add(changeNowItem);

        _pauseAutoUpdateItem = new ToolStripMenuItem("自動更新を一時停止");
        _pauseAutoUpdateItem.Click += (s, e) => ToggleAutoUpdatePause();
        contextMenu.Items.Add(_pauseAutoUpdateItem);

        // メニューを開くたびに最新の状態を反映する（設定画面やバックグラウンド処理による変化に追従するため）
        contextMenu.Opening += (s, e) => RefreshAutoUpdatePauseState();

        var settingsItem = new ToolStripMenuItem("設定");
        settingsItem.Click += (s, e) => ShowSettings();
        contextMenu.Items.Add(settingsItem);

        contextMenu.Items.Add(new ToolStripSeparator());

        var versionItem = new ToolStripMenuItem("バージョン情報");
        versionItem.Click += (s, e) => ShowVersionInfo();
        contextMenu.Items.Add(versionItem);

        contextMenu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("終了");
        exitItem.Click += (s, e) => ExitApplication();
        contextMenu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = contextMenu;
    }

    /// <summary>
    /// 設定画面（メインウィンドウ）を表示します。
    /// 開くたびに最新の設定データを再ロードし、最小化されている場合は通常状態に戻します。
    /// </summary>
    private void ShowSettings()
    {
        if (_mainWindow.DataContext is ViewModels.MainWindowViewModel viewModel)
        {
            viewModel.LoadSettings();
        }

        if (_mainWindow.IsVisible)
        {
            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }
            _mainWindow.Activate();
        }
        else
        {
            _mainWindow.Show();
        }
    }

    /// <summary>
    /// 即座に壁紙を更新する処理を実行します。
    /// </summary>
    private async void ChangeWallpaperNow(object? sender, EventArgs e)
    {
        try
        {
            await OS.WallpaperManager.Instance.TriggerUpdateNowAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"壁紙の更新中にエラーが発生しました:\n{ex.Message}", "DecoDesk", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 壁紙の自動更新の一時停止と再開を切り替えます。
    /// </summary>
    private void ToggleAutoUpdatePause()
    {
        var manager = OS.WallpaperManager.Instance;

        if (manager.IsAutoUpdatePaused)
        {
            manager.ResumeAutoUpdate();
            ShowBalloonNotification("DecoDesk", "壁紙の自動更新を再開しました。");
        }
        else
        {
            manager.PauseAutoUpdate();
            ShowBalloonNotification("DecoDesk", "壁紙の自動更新を一時停止しました。再開するまで壁紙は変更されません。");
        }

        RefreshAutoUpdatePauseState();
    }

    /// <summary>
    /// 一時停止の状態を、メニューのチェック表示とタスクトレイのツールチップに反映します。
    /// </summary>
    private void RefreshAutoUpdatePauseState()
    {
        if (_disposed) return;

        var isPaused = OS.WallpaperManager.Instance.IsAutoUpdatePaused;

        if (_pauseAutoUpdateItem != null)
        {
            _pauseAutoUpdateItem.Checked = isPaused;
        }

        // メニューを開かなくても停止中であることが分かるよう、ツールチップにも状態を表示する
        _notifyIcon.Text = isPaused ? "DecoDesk - 自動更新を一時停止中" : "DecoDesk";
    }

    /// <summary>
    /// バージョン情報ダイアログを表示します。
    /// </summary>
    private void ShowVersionInfo()
    {
        var versionWindow = new VersionWindow();
        versionWindow.ShowDialog();
    }

    /// <summary>
    /// アプリケーションを終了します。
    /// </summary>
    private void ExitApplication()
    {
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>
    /// タスクトレイからWindowsの通知（バルーン/トースト）を表示します。
    /// 設定画面を開いていない場合でも、バックグラウンド処理の完了などをユーザーに知らせるために使用します。
    /// </summary>
    /// <param name="title">通知のタイトル</param>
    /// <param name="message">通知本文</param>
    /// <param name="icon">通知アイコンの種類</param>
    public void ShowBalloonNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        if (_disposed) return;

        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(5000);
    }

    /// <summary>
    /// NotifyIcon のリソースを解放します。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        if (Current == this)
        {
            Current = null;
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _disposed = true;
    }
}
