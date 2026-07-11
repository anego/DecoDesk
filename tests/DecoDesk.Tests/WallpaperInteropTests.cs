using System;
using System.Collections.Generic;
using System.Threading;
using Xunit;
using DecoDesk.OS;

namespace DecoDesk.Tests;

/// <summary>
/// Windows OS 連携用の API (<see cref="WallpaperInterop"/>) の動作を検証するテストクラスです。
/// </summary>
public sealed class WallpaperInteropTests
{
    [Fact]
    public void GetDisplayMonitors_ShouldReturnConnectedMonitors()
    {
        // IDesktopWallpaper などのシェル COM は STA (Single Threaded Apartment) である必要があるため、
        // xUnit のデフォルトスレッド (MTA) ではなく、明示的に STA に設定したスレッドで処理を実行します。
        List<DisplayMonitorInfo> monitors = null!;
        Exception? exception = null;

        var thread = new Thread(() =>
        {
            try
            {
                monitors = WallpaperInterop.GetDisplayMonitors();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception != null)
        {
            throw new InvalidOperationException("STA スレッド上でのモニター情報取得に失敗しました。", exception);
        }

        // Assert
        Assert.NotNull(monitors);
        
        // 物理ディスプレイまたはフォールバックの仮想ディスプレイが1つ以上取得できていること
        Assert.True(monitors.Count >= 1, "少なくとも1つのディスプレイ情報が検出される必要があります。");

        foreach (var monitor in monitors)
        {
            // 解像度は0より大きいこと
            Assert.True(monitor.Width > 0, "画面の幅は 0 より大きい必要があります。");
            Assert.True(monitor.Height > 0, "画面の高さは 0 より大きい必要があります。");
        }
    }
}
