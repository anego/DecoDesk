using System;
using System.IO;
using Xunit;
using SkiaSharp;
using DecoDesk.Rendering;

namespace DecoDesk.Tests;

/// <summary>
/// <see cref="WallpaperRenderer"/> による画像リサイズ、合成、ポストカード/コルクボード風加工の挙動を検証するテストクラスです。
/// </summary>
public sealed class WallpaperRendererTests : IDisposable
{
    private readonly string _tempDir;
    private readonly WallpaperRenderer _renderer;

    public WallpaperRendererTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DecoDeskRendererTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _renderer = new WallpaperRenderer();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // クリーンアップエラーは無視
        }
    }

    /// <summary>
    /// 指定されたサイズと色のダミー画像を保存し、その絶対パスを返します。
    /// </summary>
    private string CreateTestImage(string filename, int width, int height, SKColor color)
    {
        var filePath = Path.Combine(_tempDir, filename);
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        using var stream = File.OpenWrite(filePath);
        data.SaveTo(stream);

        return filePath;
    }

    [Fact]
    public void CenterCrop_ShouldResizeAndCropToTargetSize()
    {
        // Arrange
        // 縦長の元画像 (100 x 200)
        using var srcBitmap = new SKBitmap(100, 200);
        using (var canvas = new SKCanvas(srcBitmap))
        {
            canvas.Clear(SKColors.Blue);
        }

        // Act
        // 正方形にクロップ (300 x 300)
        using var result = _renderer.CenterCrop(srcBitmap, 300, 300);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(300, result.Width);
        Assert.Equal(300, result.Height);
    }

    [Fact]
    public void CreateGridComposite_ShouldGenerateCorrectSizeBitmap()
    {
        // Arrange
        var img1 = CreateTestImage("grid1.png", 200, 200, SKColors.Red);
        var img2 = CreateTestImage("grid2.png", 200, 200, SKColors.Green);
        var filePaths = new[] { img1, img2 };

        // Act
        using var composite = _renderer.CreateGridComposite(filePaths, 800, 600);

        // Assert
        Assert.NotNull(composite);
        Assert.Equal(800, composite.Width);
        Assert.Equal(600, composite.Height);
    }

    [Fact]
    public void CreateRandomComposite_ShouldGenerateCorrectSizeBitmap()
    {
        // Arrange
        var img1 = CreateTestImage("rand1.png", 300, 200, SKColors.Yellow);
        var img2 = CreateTestImage("rand2.png", 200, 300, SKColors.Purple);
        var filePaths = new[] { img1, img2 };

        // Act
        using var composite = _renderer.CreateRandomComposite(filePaths, 1024, 768);

        // Assert
        Assert.NotNull(composite);
        Assert.Equal(1024, composite.Width);
        Assert.Equal(768, composite.Height);
    }

    [Fact]
    public void CreateCorkboardComposite_ShouldGenerateCorrectSizeBitmap()
    {
        // Arrange
        var img1 = CreateTestImage("cork1.png", 150, 150, SKColors.Cyan);
        var img2 = CreateTestImage("cork2.png", 200, 100, SKColors.Magenta);
        var filePaths = new[] { img1, img2 };

        // Act
        using var composite = _renderer.CreateCorkboardComposite(filePaths, 1280, 720);

        // Assert
        Assert.NotNull(composite);
        Assert.Equal(1280, composite.Width);
        Assert.Equal(720, composite.Height);
        
        // コルクテクスチャが塗られ、透過ビットマップではないことの簡易検証
        var color = composite.GetPixel(10, 10);
        Assert.NotEqual(SKColors.Transparent, color);
    }
}
