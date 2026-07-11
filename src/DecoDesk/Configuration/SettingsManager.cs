using System;
using System.IO;
using System.Text.Json;

namespace DecoDesk.Configuration;

/// <summary>
/// アプリケーション設定の読み込みと保存を管理するクラスです。
/// </summary>
public sealed class SettingsManager
{
    private readonly string _settingsFilePath;
    private readonly object _lockObject = new();

    /// <summary>
    /// デフォルトの設定ファイルパス（%LOCALAPPDATA%/DecoDesk/settings.json）を使用して <see cref="SettingsManager"/> を初期化します。
    /// </summary>
    public SettingsManager() : this(GetDefaultSettingsPath())
    {
    }

    /// <summary>
    /// 指定された設定ファイルパスを使用して <see cref="SettingsManager"/> を初期化します。
    /// </summary>
    /// <param name="settingsFilePath">設定ファイルの絶対パス</param>
    public SettingsManager(string settingsFilePath)
    {
        if (string.IsNullOrWhiteSpace(settingsFilePath))
        {
            throw new ArgumentException("設定ファイルのパスを空にすることはできません。", nameof(settingsFilePath));
        }
        _settingsFilePath = settingsFilePath;
    }

    /// <summary>
    /// デフォルトの設定ファイルの保存先を取得します。
    /// </summary>
    private static string GetDefaultSettingsPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appDir = Path.Combine(localAppData, "DecoDesk");
        return Path.Combine(appDir, "settings.json");
    }

    /// <summary>
    /// 設定ファイルから設定情報を読み込みます。ファイルが存在しない場合はデフォルト設定を返します。
    /// </summary>
    /// <returns>読み込まれた <see cref="AppSettings"/> オブジェクト</returns>
    public AppSettings Load()
    {
        lock (_lockObject)
        {
            try
            {
                if (!File.Exists(_settingsFilePath))
                {
                    return AppSettings.CreateDefault();
                }

                var json = File.ReadAllText(_settingsFilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                return settings ?? AppSettings.CreateDefault();
            }
            catch
            {
                // 読み込みに失敗した場合は破損とみなし、安全のためデフォルトを返す
                return AppSettings.CreateDefault();
            }
        }
    }

    /// <summary>
    /// 設定情報をファイルに保存します。
    /// </summary>
    /// <param name="settings">保存する設定情報</param>
    public void Save(AppSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));

        lock (_lockObject)
        {
            try
            {
                var dir = Path.GetDirectoryName(_settingsFilePath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };
                var json = JsonSerializer.Serialize(settings, options);
                File.WriteAllText(_settingsFilePath, json);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("設定の保存に失敗しました。", ex);
            }
        }
    }
}
