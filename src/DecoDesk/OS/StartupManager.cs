using System;
using Microsoft.Win32;
using Microsoft.Extensions.Logging;

namespace DecoDesk.OS;

/// <summary>
/// Windows 起動時（サインイン時）のアプリケーション自動起動（スタートアップ登録）の管理を担うユーティリティクラスです。
/// </summary>
public static class StartupManager
{
    private static readonly ILogger Logger = AppLoggerFactory.Factory.CreateLogger("DecoDesk.OS.StartupManager");

    private const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "DecoDesk";

    /// <summary>
    /// 現在自動起動が有効に設定されているかどうかをレジストリから取得します。
    /// </summary>
    /// <returns>有効な場合は true、それ以外は false</returns>
    public static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupKey, false);
            if (key == null) return false;

            var value = key.GetValue(AppName);
            if (value is string exePath)
            {
                var currentPath = Environment.ProcessPath;
                if (currentPath != null && exePath.Contains(currentPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "スタートアップ登録状態の取得中に例外が発生しました。");
            return false;
        }
    }

    /// <summary>
    /// 自動起動の登録または解除を行います。
    /// </summary>
    /// <param name="enable">登録する場合は true、解除する場合は false</param>
    public static void SetStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupKey, true);
            if (key == null)
            {
                Logger.LogWarning("スタートアップ登録用のレジストリキーを開けませんでした。");
                return;
            }

            if (enable)
            {
                var exePath = Environment.ProcessPath;
                if (exePath != null)
                {
                    var runCommand = $"\"{exePath}\"";
                    key.SetValue(AppName, runCommand);
                    Logger.LogInformation("Windows スタートアップに登録しました。Path={Path}", runCommand);
                }
                else
                {
                    Logger.LogWarning("現在のプロセスパスを取得できなかったため、スタートアップ登録をスキップしました。");
                }
            }
            else
            {
                if (key.GetValue(AppName) != null)
                {
                    key.DeleteValue(AppName, false);
                    Logger.LogInformation("Windows スタートアップから登録を解除しました。");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "スタートアップの設定変更（レジストリ書き込み）中に例外が発生しました。");
        }
    }
}
