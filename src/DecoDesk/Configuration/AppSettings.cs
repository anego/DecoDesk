namespace DecoDesk.Configuration;

/// <summary>
/// アプリケーションの設定情報を保持するデータ構造です。
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// スキャン対象の画像ディレクトリパスのリストを取得または設定します。
    /// </summary>
    public System.Collections.Generic.List<string> TargetDirectories { get; set; } = new();

    /// <summary>
    /// 壁紙の自動更新間隔（秒単位）を取得または設定します。
    /// </summary>
    public int UpdateIntervalSeconds { get; set; } = 900;

    /// <summary>
    /// 画像加工モードを取得または設定します（例: "None", "Postcard", "Corkboard"）。
    /// </summary>
    public string ProcessingMode { get; set; } = "None";

    /// <summary>
    /// タイマーによる壁紙の自動更新を有効にするかどうかを取得または設定します。
    /// </summary>
    public bool AutoUpdateEnabled { get; set; } = false;

    /// <summary>
    /// 合成壁紙1枚あたりに使用する画像数（枚）を取得または設定します。
    /// </summary>
    public int WallpaperImageCount { get; set; } = 6;

    /// <summary>
    /// 合成する画像の長辺のピクセルサイズを取得または設定します。
    /// </summary>
    public int WallpaperImageSize { get; set; } = 350;

    /// <summary>
    /// 合成時の画像同士の間隔・密度（0.5 〜 1.5 の倍率）を取得または設定します。
    /// </summary>
    public double WallpaperDensity { get; set; } = 1.0;

    /// <summary>
    /// ポストカード加工時の背景カラーコードを取得または設定します。
    /// </summary>
    public string WallpaperBackgroundColor { get; set; } = "#2F4F4F";

    /// <summary>
    /// Windows 起動時に自動で実行するかどうかを取得または設定します。
    /// </summary>
    public bool StartWithWindows { get; set; } = false;

    /// <summary>
    /// アプリケーション起動時に壁紙を自動更新するかどうかを取得または設定します。
    /// </summary>
    public bool UpdateWallpaperOnStartup { get; set; } = false;

    /// <summary>
    /// アプリケーション起動時の壁紙自動更新の遅延秒数を取得または設定します。
    /// </summary>
    public int StartupDelaySeconds { get; set; } = 60;

    /// <summary>
    /// デフォルト値を持つ新しい設定インスタンスを作成します。
    /// </summary>
    /// <returns>デフォルト設定オブジェクト</returns>
    public static AppSettings CreateDefault()
    {
        return new AppSettings
        {
            TargetDirectories = new System.Collections.Generic.List<string>(),
            UpdateIntervalSeconds = 900,
            ProcessingMode = "None",
            AutoUpdateEnabled = false,
            WallpaperImageCount = 6,
            WallpaperImageSize = 350,
            WallpaperDensity = 1.0,
            WallpaperBackgroundColor = "#2F4F4F",
            StartWithWindows = false,
            UpdateWallpaperOnStartup = false,
            StartupDelaySeconds = 60
        };
    }
}
