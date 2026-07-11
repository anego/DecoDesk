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
    private bool _disposed;

    /// <summary>
    /// <see cref="NotifyIconWrapper"/> クラスの新しいインスタンスを初期化します。
    /// </summary>
    /// <param name="mainWindow">設定画面として表示するメインウィンドウ</param>
    public NotifyIconWrapper(MainWindow mainWindow)
    {
        _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));

        // NotifyIconの初期化。標準のアプリケーションアイコンを使用
        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "DecoDesk",
            Visible = true
        };

        // ダブルクリックで設定画面を表示
        _notifyIcon.DoubleClick += (s, e) => ShowSettings();

        // コンテキストメニューの作成
        var contextMenu = new ContextMenuStrip();

        var changeNowItem = new ToolStripMenuItem("今すぐ変更");
        changeNowItem.Click += ChangeWallpaperNow;
        contextMenu.Items.Add(changeNowItem);

        var settingsItem = new ToolStripMenuItem("設定");
        settingsItem.Click += (s, e) => ShowSettings();
        contextMenu.Items.Add(settingsItem);

        contextMenu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("終了");
        exitItem.Click += (s, e) => ExitApplication();
        contextMenu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = contextMenu;
    }

    /// <summary>
    /// 設定画面（メインウィンドウ）を表示します。
    /// </summary>
    private void ShowSettings()
    {
        if (_mainWindow.IsVisible)
        {
            _mainWindow.Activate();
        }
        else
        {
            _mainWindow.Show();
        }
    }

    /// <summary>
    /// 即座に壁紙を更新する処理を実行します（現時点ではダミー処理）。
    /// </summary>
    private void ChangeWallpaperNow(object? sender, EventArgs e)
    {
        System.Windows.MessageBox.Show("壁紙を変更します（ダミー処理）", "DecoDesk", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// アプリケーションを終了します。
    /// </summary>
    private void ExitApplication()
    {
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>
    /// NotifyIcon のリソースを解放します。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _disposed = true;
    }
}
