using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace DecoDesk.OS;

/// <summary>
/// Windows API の RECT 構造体のラッパーです。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    /// <summary>
    /// RECT の幅を取得します。
    /// </summary>
    public int Width => Right - Left;

    /// <summary>
    /// RECT の高さを取得します。
    /// </summary>
    public int Height => Bottom - Top;
}

/// <summary>
/// デスクトップ壁紙のスライドショー再生方向を表す列挙型です。
/// </summary>
public enum DesktopSlideshowDirection
{
    Forward = 0,
    Backward = 1
}

/// <summary>
/// 壁紙の表示位置を表す列挙型です。
/// </summary>
public enum DesktopSlideshowPosition
{
    Center = 0,
    Tile = 1,
    Stretch = 2,
    Fit = 3,
    Fill = 4,
    Span = 5
}

/// <summary>
/// 壁紙のスライドショー状態を表す列挙型です。
/// </summary>
public enum DesktopSlideshowState
{
    Enabled = 0,
    Disabled = 1,
    DisabledByRemoteSession = 2
}

/// <summary>
/// Windows 8 以降で提供されるマルチディスプレイ壁紙制御用の COM インターフェースです。
/// </summary>
[ComImport]
[Guid("B92B5679-D139-417b-94AE-7C643F858E10")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDesktopWallpaper
{
    void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID, [MarshalAs(UnmanagedType.LPWStr)] string wallpaperUri);

    [return: MarshalAs(UnmanagedType.LPWStr)]
    string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID);

    [return: MarshalAs(UnmanagedType.LPWStr)]
    string GetMonitorDevicePathAt(uint monitorIndex);

    uint GetMonitorDevicePathCount();

    RECT GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID);

    void SetBackgroundColor(uint color);

    uint GetBackgroundColor();

    void SetPosition(DesktopSlideshowPosition position);

    DesktopSlideshowPosition GetPosition();

    void SetSlideshow(IntPtr images); // IShellItemArray

    IntPtr GetSlideshow(); // IShellItemArray

    void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string? monitorID, DesktopSlideshowDirection direction);

    DesktopSlideshowState GetStatus();

    bool Enable(bool enable);
}

/// <summary>
/// IDesktopWallpaper インターフェースの実装クラスを提供するコクラスインターフェースです。
/// </summary>
[ComImport]
[Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")]
[CoClass(typeof(DesktopWallpaperClass))]
public interface DesktopWallpaper : IDesktopWallpaper
{
}

/// <summary>
/// IDesktopWallpaper コクラスのインポート定義です。
/// </summary>
[ComImport]
[Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")]
public class DesktopWallpaperClass
{
}

/// <summary>
/// ディスプレイの識別情報、物理画面配置座標、およびサイズを保持する構造です。
/// </summary>
/// <param name="MonitorId">モニターの物理デバイスID</param>
/// <param name="Left">仮想画面座標の左端位置</param>
/// <param name="Top">仮想画面座標の上端位置</param>
/// <param name="Width">画面の幅（ピクセル単位）</param>
/// <param name="Height">画面の高さ（ピクセル単位）</param>
public record DisplayMonitorInfo(string MonitorId, int Left, int Top, int Width, int Height);

/// <summary>
/// Windows COM API (IDesktopWallpaper) を安全に呼び出すための P/Invoke ラッパークラスです。
/// </summary>
public static class WallpaperInterop
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);

    private const int SPI_SETDESKWALLPAPER = 20;
    private const int SPIF_UPDATEINIFILE = 0x01;
    private const int SPIF_SENDCHANGE = 0x02;

    private static readonly ILogger Logger = AppLoggerFactory.Factory.CreateLogger("DecoDesk.OS.WallpaperInterop");

    private static void LogDebug(string message)
    {
        Logger.LogDebug("{Message}", message);
    }

    /// <summary>
    /// 現在システムに接続されているすべてのモニター情報（IDと解像度）を取得します。
    /// COM キャスト失敗時（非デスクトップセッションやヘッドレス環境）はプライマリ画面情報のフォールバックを返します。
    /// </summary>
    /// <returns>モニター情報のリスト</returns>
    public static List<DisplayMonitorInfo> GetDisplayMonitors()
    {
        var monitors = new List<DisplayMonitorInfo>();
        try
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            LogDebug($"GetDisplayMonitors count={screens.Length}");
            for (int i = 0; i < screens.Length; i++)
            {
                var s = screens[i];
                monitors.Add(new DisplayMonitorInfo(s.DeviceName, s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height));
                LogDebug($"  Monitor {i}: Name={s.DeviceName}, Left={s.Bounds.X}, Top={s.Bounds.Y}, Width={s.Bounds.Width}, Height={s.Bounds.Height}");
            }
        }
        catch (Exception ex)
        {
            LogDebug($"GetDisplayMonitors failed or empty, falling back. Exception: {ex.Message}");
            // 非デスクトップセッションやテスト環境におけるフォールバック
            int width = 1920;
            int height = 1080;
            try
            {
                width = (int)System.Windows.SystemParameters.PrimaryScreenWidth;
                height = (int)System.Windows.SystemParameters.PrimaryScreenHeight;
                if (width <= 0) width = 1920;
                if (height <= 0) height = 1080;
            }
            catch
            {
                // WPFコンテキストが無い場合のセーフガード
            }

            monitors.Clear();
            monitors.Add(new DisplayMonitorInfo("Fallback_Primary", 0, 0, width, height));
        }

        return monitors;
    }

    /// <summary>
    /// 各モニターに跨る「スパン壁紙」をシステム全体に適用します。
    /// COM 経由での適用が失敗した場合は、SystemParametersInfo による古典的壁紙適用（スパンモード）をフォールバックとして実行します。
    /// </summary>
    /// <param name="imagePath">適用する巨大画像（スパン用）ファイルの絶対パス</param>
    public static void SetSpanWallpaper(string imagePath)
    {
        LogDebug($"SetSpanWallpaper Start: imagePath={imagePath}");
        if (string.IsNullOrWhiteSpace(imagePath)) throw new ArgumentException("画像パスを空にすることはできません。", nameof(imagePath));
        if (!System.IO.File.Exists(imagePath)) throw new System.IO.FileNotFoundException("適用する壁紙ファイルが見つかりません。", imagePath);

        var success = false;
        Exception? comException = null;

        var thread = new System.Threading.Thread(() =>
        {
            IDesktopWallpaper? wallpaper = null;
            try
            {
                wallpaper = (IDesktopWallpaper)new DesktopWallpaperClass();
                
                // 壁紙適用前に表示スタイルを「スパン」に設定
                try
                {
                    wallpaper.SetPosition(DesktopSlideshowPosition.Span);
                }
                catch (Exception pex)
                {
                    LogDebug($"  SetPosition Span Before Error: {pex.Message}");
                }

                wallpaper.SetWallpaper(null, imagePath);
                LogDebug("  SetWallpaper Span Success");

                // 適用後に再度「スパン」を強制設定
                try
                {
                    wallpaper.SetPosition(DesktopSlideshowPosition.Span);
                }
                catch (Exception pex)
                {
                    LogDebug($"  SetPosition Span After Error: {pex.Message}");
                }

                success = true;
            }
            catch (Exception ex)
            {
                comException = ex;
                LogDebug($"  COM Span Thread Inside Error: {ex.Message}");
            }
            finally
            {
                if (wallpaper != null)
                {
                    try
                    {
                        Marshal.ReleaseComObject(wallpaper);
                    }
                    catch
                    {
                        // 無視
                    }
                }
            }
        });

        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (!success)
        {
            LogDebug($"SetSpanWallpaper COM Failed. Falling back to SystemParametersInfo. Error: {comException?.Message}");
            try
            {
                // レジストリの壁紙位置調整設定を「スパン」に書き換え (WallpaperStyle="22", TileWallpaper="0")
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true))
                {
                    if (key != null)
                    {
                        key.SetValue("WallpaperStyle", "22");
                        key.SetValue("TileWallpaper", "0");
                    }
                }

                int result = SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, imagePath, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
                if (result == 0)
                {
                    throw new InvalidOperationException("SystemParametersInfo によるスパン壁紙の適用に失敗しました。");
                }
            }
            catch (Exception fallbackEx)
            {
                throw new InvalidOperationException(
                    $"スパン壁紙の適用プロセスが失敗しました。フォールバック適用も失敗しました。", 
                    new AggregateException(comException ?? new Exception("COMスレッドエラー"), fallbackEx));
            }
        }
    }
}
