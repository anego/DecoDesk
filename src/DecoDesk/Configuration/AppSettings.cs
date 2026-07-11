using System;

namespace DecoDesk.Configuration;

/// <summary>
/// アプリケーションの設定情報を保持するデータ構造です。
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// スキャン対象の画像ディレクトリパスを取得または設定します。
    /// </summary>
    public string TargetDirectory { get; set; } = string.Empty;

    /// <summary>
    /// 壁紙の自動更新間隔（分単位）を取得または設定します。
    /// </summary>
    public int UpdateIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// 画像加工モードを取得または設定します（例: "None", "Postcard", "Corkboard"）。
    /// </summary>
    public string ProcessingMode { get; set; } = "None";

    /// <summary>
    /// タイマーによる壁紙の自動更新を有効にするかどうかを取得または設定します。
    /// </summary>
    public bool AutoUpdateEnabled { get; set; } = false;

    /// <summary>
    /// デフォルト値を持つ新しい設定インスタンスを作成します。
    /// </summary>
    /// <returns>デフォルト設定オブジェクト</returns>
    public static AppSettings CreateDefault()
    {
        return new AppSettings
        {
            TargetDirectory = string.Empty,
            UpdateIntervalMinutes = 15,
            ProcessingMode = "None",
            AutoUpdateEnabled = false
        };
    }
}
