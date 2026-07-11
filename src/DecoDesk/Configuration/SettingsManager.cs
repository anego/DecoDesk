using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DecoDesk.Configuration;

/// <summary>
/// アプリケーション設定の読み込みと保存を管理するクラスです。
/// </summary>
public sealed class SettingsManager
{
    private static readonly ILogger Logger = OS.AppLoggerFactory.CreateLogger<SettingsManager>();

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
                    Logger.LogWarning("設定ファイルが存在しないため、デフォルト設定を適用します。ファイルパス={FilePath}", _settingsFilePath);
                    return AppSettings.CreateDefault();
                }

                var json = File.ReadAllText(_settingsFilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings == null)
                {
                    Logger.LogWarning("設定ファイルのデシリアライズ結果が null でした。デフォルト設定を適用します。");
                    return AppSettings.CreateDefault();
                }

                // 互換性マイグレーション：json 内に UpdateIntervalMinutes があり、UpdateIntervalSeconds が無い場合
                if (json.Contains("UpdateIntervalMinutes") && !json.Contains("UpdateIntervalSeconds"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("UpdateIntervalMinutes", out var minProp))
                        {
                            int mins = minProp.GetInt32();
                            settings.UpdateIntervalSeconds = mins * 60;
                            Logger.LogInformation("旧設定形式からマイグレーションを実行しました: UpdateIntervalMinutes={Minutes}分 -> UpdateIntervalSeconds={Seconds}秒", mins, settings.UpdateIntervalSeconds);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "互換マイグレーション中にエラーが発生しました。");
                    }
                }

                // 互換性マイグレーション：json 内に TargetDirectory があり、TargetDirectories が無いか空の場合
                if (json.Contains("TargetDirectory") && (settings.TargetDirectories == null || settings.TargetDirectories.Count == 0))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("TargetDirectory", out var dirProp))
                        {
                            string dir = dirProp.GetString() ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(dir))
                            {
                                settings.TargetDirectories ??= new System.Collections.Generic.List<string>();
                                if (!settings.TargetDirectories.Contains(dir))
                                {
                                    settings.TargetDirectories.Add(dir);
                                    Logger.LogInformation("旧設定形式からフォルダマイグレーションを実行しました: TargetDirectory={Path}", dir);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "フォルダマイグレーション中にエラーが発生しました。");
                    }
                }

                Logger.LogInformation("設定ファイルを読み込みました。ファイルパス={FilePath}, UpdateIntervalSeconds={IntervalSeconds}秒", _settingsFilePath, settings.UpdateIntervalSeconds);
                return settings;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "設定ファイルの読み込みまたはデシリアライズ中にエラーが発生しました。デフォルト設定を適用します。パス={FilePath}", _settingsFilePath);
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
                Logger.LogInformation("設定の書き出しに成功しました。ファイルパス={FilePath}, サイズ={Size}バイト", _settingsFilePath, json.Length);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "設定の書き出し中にエラーが発生しました。パス={FilePath}", _settingsFilePath);
                throw new InvalidOperationException("設定の保存に失敗しました。", ex);
            }
        }
    }
}
