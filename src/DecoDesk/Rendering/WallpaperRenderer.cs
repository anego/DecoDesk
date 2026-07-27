using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace DecoDesk.Rendering;

/// <summary>
/// SkiaSharp を使用して画像の加工、サイズ調整、および複数画像のレイアウト合成を行うクラスです。
/// 全アンマネージドリソースの確実な解放を行います。
/// </summary>
public sealed class WallpaperRenderer
{
    private static readonly Random Rand = new();

    /// <summary>
    /// アスペクト比を維持しながら画像を中央で切り抜き、指定サイズにリサイズしたビットマップを生成します。
    /// </summary>
    /// <param name="source">元画像ビットマップ</param>
    /// <param name="targetWidth">目標の幅</param>
    /// <param name="targetHeight">目標の高さ</param>
    /// <returns>リサイズ・クロップされた新しい <see cref="SKBitmap"/></returns>
    public SKBitmap CenterCrop(SKBitmap source, int targetWidth, int targetHeight)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (targetWidth <= 0) throw new ArgumentOutOfRangeException(nameof(targetWidth));
        if (targetHeight <= 0) throw new ArgumentOutOfRangeException(nameof(targetHeight));

        var targetBitmap = new SKBitmap(targetWidth, targetHeight);
        using var canvas = new SKCanvas(targetBitmap);
        canvas.Clear(SKColors.Transparent);

        float srcWidth = source.Width;
        float srcHeight = source.Height;

        float scale = Math.Max(targetWidth / srcWidth, targetHeight / srcHeight);

        float drawWidth = srcWidth * scale;
        float drawHeight = srcHeight * scale;

        float x = (targetWidth - drawWidth) / 2.0f;
        float y = (targetHeight - drawHeight) / 2.0f;

        using var paint = new SKPaint
        {
            IsAntialias = true
        };

        // 大幅な縮小描画時のジャギー・モアレを防ぐため、ミップマップ付きサンプリングを使用する
        var destRect = new SKRect(x, y, x + drawWidth, y + drawHeight);
        canvas.DrawBitmap(source, destRect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);

        return targetBitmap;
    }

    /// <summary>
    /// 単一画像をモニター解像度に合わせて中央配置（切り抜きリサイズ）したビットマップを生成します。
    /// </summary>
    /// <param name="filePaths">画像パス。先頭の1枚のみを使用します。</param>
    /// <param name="canvasWidth">目標のキャンバス幅</param>
    /// <param name="canvasHeight">目標のキャンバス高さ</param>
    /// <returns>加工なしの壁紙ビットマップ</returns>
    public SKBitmap CreateGridComposite(IEnumerable<string> filePaths, int canvasWidth, int canvasHeight)
    {
        var paths = filePaths?.ToList() ?? new List<string>();
        if (!paths.Any()) throw new ArgumentException("ファイルパスのリストが空です。", nameof(filePaths));

        // 最初の1枚を画面解像度に合わせて中央切り抜き
        using var sourceBitmap = SKBitmap.Decode(paths.First());
        if (sourceBitmap == null)
        {
            throw new InvalidOperationException($"画像のデコードに失敗しました: {paths.First()}");
        }

        return CenterCrop(sourceBitmap, canvasWidth, canvasHeight);
    }

    /// <summary>
    /// 複数の画像をランダムな位置・角度に散りばめて1枚の壁紙を合成します。
    /// </summary>
    /// <param name="filePaths">使用する画像のファイルパス群</param>
    /// <param name="canvasWidth">キャンバスの幅</param>
    /// <param name="canvasHeight">キャンバスの高さ</param>
    /// <param name="density">配置密度（画像サイズ倍率）</param>
    /// <returns>合成されたビットマップ</returns>
    public SKBitmap CreateRandomComposite(IEnumerable<string> filePaths, int canvasWidth, int canvasHeight, int imageSize = 350, double density = 1.0, string backgroundColor = "#2F4F4F")
    {
        var paths = filePaths?.ToList() ?? new List<string>();
        if (!paths.Any()) throw new ArgumentException("ファイルパスのリストが空です。", nameof(filePaths));

        var compositeBitmap = new SKBitmap(canvasWidth, canvasHeight);
        using var canvas = new SKCanvas(compositeBitmap);

        // 指定されたカラーコードをパースして背景を塗りつぶし (失敗時は DarkSlateGray)
        var bgColor = SKColors.DarkSlateGray;
        if (!string.IsNullOrWhiteSpace(backgroundColor) && SKColor.TryParse(backgroundColor, out var parsedColor))
        {
            bgColor = parsedColor;
        }
        canvas.Clear(bgColor);

        // ポストカード風画像群をグリッドジッターでバランス良く配置して描画
        DrawJitteredLayout(canvas, paths, canvasWidth, canvasHeight, isCorkboard: false, imageSize: imageSize, density: density);

        return compositeBitmap;
    }

    /// <summary>
    /// 画像に白いフチとドロップシャドウを付け、指定角度で傾けて描画する「ポストカード風加工」を行います。
    /// </summary>
    public void DrawPostcard(SKCanvas canvas, SKBitmap bitmap, float x, float y, float drawWidth, float drawHeight, float angle)
    {
        if (canvas == null) throw new ArgumentNullException(nameof(canvas));
        if (bitmap == null) throw new ArgumentNullException(nameof(bitmap));

        canvas.Save();

        // 1. 移動と回転
        float centerX = x + drawWidth / 2.0f;
        float centerY = y + drawHeight / 2.0f;
        canvas.Translate(centerX, centerY);
        canvas.RotateDegrees(angle);

        // 2. 白いフチ（マージン）の計算
        // 短辺の約 4% をフチの太さとする（最小 8px）
        float borderSize = Math.Max(8.0f, Math.Min(drawWidth, drawHeight) * 0.04f);
        
        float outerWidth = drawWidth + borderSize * 2;
        float outerHeight = drawHeight + borderSize * 2;

        float halfOuterW = outerWidth / 2.0f;
        float halfOuterH = outerHeight / 2.0f;

        // 3. 影（ドロップシャドウ）の作成
        using var shadowPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            // 影のエフェクト: ぼかし半径 10px、オフセット (5px, 5px)、半透明の黒
            ImageFilter = SKImageFilter.CreateDropShadow(5.0f, 5.0f, 10.0f, 10.0f, new SKColor(0, 0, 0, 150))
        };

        // 影付きの白い台紙（フチ）を描画
        var outerRect = new SKRect(-halfOuterW, -halfOuterH, halfOuterW, halfOuterH);
        canvas.DrawRoundRect(outerRect, 4.0f, 4.0f, shadowPaint);

        // 4. 画像自体の描画
        using var imagePaint = new SKPaint
        {
            IsAntialias = true
        };

        // 原寸から大幅に縮小して回転描画するため、ミップマップ付きサンプリングでエイリアシング（輪郭のジャギー）を防ぐ
        var innerRect = new SKRect(-halfOuterW + borderSize, -halfOuterH + borderSize, halfOuterW - borderSize, halfOuterH - borderSize);
        canvas.DrawBitmap(bitmap, innerRect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), imagePaint);

        canvas.Restore();
    }

    /// <summary>
    /// 背景にコルクテクスチャを敷き、合成画像をポストカード風にピン留めした「コルクボード風加工」を施した壁紙を構築します。
    /// </summary>
    /// <param name="filePaths">使用する画像ファイルパスのリスト</param>
    /// <param name="canvasWidth">キャンバスの幅</param>
    /// <param name="canvasHeight">キャンバスの高さ</param>
    /// <param name="density">配置密度（画像サイズ倍率）</param>
    /// <returns>コルクボード風の合成されたビットマップ</returns>
    public SKBitmap CreateCorkboardComposite(IEnumerable<string> filePaths, int canvasWidth, int canvasHeight, int imageSize = 350, double density = 1.0)
    {
        var paths = filePaths?.ToList() ?? new List<string>();
        if (!paths.Any()) throw new ArgumentException("ファイルパスのリストが空です。", nameof(filePaths));

        var compositeBitmap = new SKBitmap(canvasWidth, canvasHeight);
        using var canvas = new SKCanvas(compositeBitmap);

        // 1. コルクテクスチャの動的生成と描画
        GenerateCorkTexture(canvas, canvasWidth, canvasHeight);

        // 2. ポストカード風＋プッシュピン付き画像群をグリッドジッターでバランス良く配置して描画
        DrawJitteredLayout(canvas, paths, canvasWidth, canvasHeight, isCorkboard: true, imageSize: imageSize, density: density);

        return compositeBitmap;
    }

    /// <summary>
    /// ランダムな回転角度のみを利用して画像を描画する内部メソッドです。
    /// </summary>
    private void DrawRotatedImage(SKCanvas canvas, SKBitmap bitmap, float x, float y, float width, float height, float angle)
    {
        canvas.Save();
        float centerX = x + width / 2.0f;
        float centerY = y + height / 2.0f;
        canvas.Translate(centerX, centerY);
        canvas.RotateDegrees(angle);

        using var paint = new SKPaint
        {
            IsAntialias = true
        };

        var rect = new SKRect(-width / 2.0f, -height / 2.0f, width / 2.0f, height / 2.0f);
        canvas.DrawBitmap(bitmap, rect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        canvas.Restore();
    }

    /// <summary>
    /// コルクボードの背景テクスチャをキャンバス全体にテクスチャとして生成・描画します。
    /// </summary>
    private void GenerateCorkTexture(SKCanvas canvas, int width, int height)
    {
        canvas.Clear(new SKColor(194, 154, 106)); // #C29A6A

        using var paint = new SKPaint { IsAntialias = false };

        int dotCount = (width * height) / 16;
        var colors = new[]
        {
            new SKColor(133, 94, 56, 120),  // ダークブラウン（影用）
            new SKColor(216, 181, 137, 100), // ライトベージュ（影用）
            new SKColor(165, 125, 80, 150)   // ミディアムブラウン
        };

        for (int i = 0; i < dotCount; i++)
        {
            float px = (float)Rand.NextDouble() * width;
            float py = (float)Rand.NextDouble() * height;
            float radius = (float)(Rand.NextDouble() * 1.5 + 0.5); // 半径 0.5px 〜 2px

            paint.Color = colors[Rand.Next(colors.Length)];
            canvas.DrawCircle(px, py, radius, paint);
        }

        using var shaderPaint = new SKPaint
        {
            BlendMode = SKBlendMode.Modulate,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(width / 2.0f, height / 2.0f),
                Math.Max(width, height) * 0.7f,
                new[] { SKColors.White, new SKColor(230, 210, 190) },
                null,
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(new SKRect(0, 0, width, height), shaderPaint);
    }

    /// <summary>
    /// 指定された座標に立体的なプッシュピン（およびピン自身の影）を描画します。
    /// </summary>
    private void DrawPushPin(SKCanvas canvas, float x, float y, float angle)
    {
        canvas.Save();
        canvas.Translate(x, y);
        canvas.RotateDegrees(angle);

        // 1. ピンの影 (斜め下にずらして半透明黒で描画)
        using (var shadowPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 80),
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        })
        {
            // ピンの針の影
            canvas.DrawLine(2.0f, 2.0f, 2.0f, 15.0f, shadowPaint);
            // ピンの頭の影
            canvas.DrawCircle(2.0f, 2.0f, 7.0f, shadowPaint);
        }

        // 2. ピンの針 (細いシルバーの線)
        using (var needlePaint = new SKPaint
        {
            Color = new SKColor(180, 180, 180),
            StrokeWidth = 1.5f,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        })
        {
            canvas.DrawLine(0, 0, 0, 15.0f, needlePaint);
        }

        // 3. ピンの頭 (立体感を出すために円形グラデーション)
        using var pinPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(-2.0f, -2.0f), // 左上に光源を配置して立体感を出す
                8.0f,
                new[] { new SKColor(255, 120, 120), new SKColor(200, 10, 10), new SKColor(100, 0, 0) },
                new float[] { 0.0f, 0.6f, 1.0f },
                SKShaderTileMode.Clamp)
        };
        canvas.DrawCircle(0, 0, 7.0f, pinPaint);

        canvas.Restore();
    }

    /// <summary>
    /// 画像の配置バランスを保つため、画面を均等なグリッドセルに分割し、
    /// 重なりや偏りを防ぐためのランダムジッターと傾きを加えながら画像をポストカード風に配置・描画します。
    /// </summary>
    private void DrawJitteredLayout(SKCanvas canvas, List<string> paths, int canvasWidth, int canvasHeight, bool isCorkboard, int imageSize, double density)
    {
        int count = paths.Count;
        if (count == 0) return;

        // 空きセルを原則廃止し、配置枚数＝セル数とする（大領域の空白地帯を解消）
        int cellCount = count;

        // 画像の枚数に応じた最適な行数・列数を決定（画面全体に満遍なく並ぶようにマッピング）
        int cols, rows;
        if (count <= 3)
        {
            cols = count;
            rows = 1;
        }
        else if (count <= 4)
        {
            cols = 2;
            rows = 2;
        }
        else if (count <= 6)
        {
            cols = 3;
            rows = 2;
        }
        else if (count <= 8)
        {
            cols = 4;
            rows = 2;
        }
        else if (count <= 9)
        {
            cols = 3;
            rows = 3;
        }
        else if (count <= 12)
        {
            cols = 4;
            rows = 3;
        }
        else
        {
            // 12枚を超える場合の汎用アスペクト比考慮の分割数決定
            cols = (int)Math.Ceiling(Math.Sqrt(count * 1.6));
            rows = (int)Math.Ceiling((double)count / cols);
        }

        int cellWidth = canvasWidth / cols;
        int cellHeight = canvasHeight / rows;

        // セルインデックスを生成してシャッフル
        var cellIndices = Enumerable.Range(0, rows * cols).ToList();
        for (int i = cellIndices.Count - 1; i > 0; i--)
        {
            int j = Rand.Next(i + 1);
            (cellIndices[i], cellIndices[j]) = (cellIndices[j], cellIndices[i]);
        }

        // 指定されたピクセルサイズに配置密度（0.5 〜 1.5）を適用して最終サイズを算出
        float scaleFactor = 0.84f * (float)density;
        float baseSize = imageSize * scaleFactor;

        // 配置済み画像の矩形を追跡するリスト
        var placedRects = new System.Collections.Generic.List<SKRect>();

        for (int i = 0; i < Math.Min(count, cellIndices.Count); i++)
        {
            int cellIdx = cellIndices[i];
            int col = cellIdx % cols;
            int row = cellIdx / cols;

            // セルの中心座標
            float cellCenterX = col * cellWidth + cellWidth / 2.0f;
            float cellCenterY = row * cellHeight + cellHeight / 2.0f;

            try
            {
                using var sourceBitmap = SKBitmap.Decode(paths[i]);
                if (sourceBitmap == null) continue;

                // アスペクト比を維持した描画サイズを決定
                float ratio = (float)sourceBitmap.Width / sourceBitmap.Height;
                float drawWidth, drawHeight;
                if (ratio > 1.0f)
                {
                    drawWidth = baseSize;
                    drawHeight = baseSize / ratio;
                }
                else
                {
                    drawHeight = baseSize;
                    drawWidth = baseSize * ratio;
                }

                // フチと影の余白を考慮した安全マージン（ディスプレイの四辺から画像がはみ出すのを防ぐ）
                float borderSize = Math.Max(8.0f, Math.Min(drawWidth, drawHeight) * 0.04f);
                float margin = borderSize + 15.0f; // フチの厚み ＋ シャドウのぼかし・オフセット幅

                // 衝突回避と段階的収縮付きリトライ処理（最大15回リトライして重なり合いを5割未満に制御）
                float x = 0, y = 0;
                float jitterX = 0, jitterY = 0;
                SKRect currentRect = default;
                const int MaxRetries = 15;

                for (int retry = 0; retry < MaxRetries; retry++)
                {
                    // リトライごとにジッター許容幅を縮小し、最終的にはセルの中心方向へ収束させる (1.0 -> 0.0)
                    float shrinkFactor = 1.0f - ((float)retry / MaxRetries);
                    float currentJitterLimit = 0.46f * shrinkFactor;

                    jitterX = (float)((Rand.NextDouble() * 2.0 - 1.0) * cellWidth * currentJitterLimit);
                    jitterY = (float)((Rand.NextDouble() * 2.0 - 1.0) * cellHeight * currentJitterLimit);

                    x = cellCenterX - drawWidth / 2.0f + jitterX;
                    y = cellCenterY - drawHeight / 2.0f + jitterY;

                    // 画面の端からマージン分内側に強制的にクランプ（見切れを防止）
                    x = Math.Clamp(x, margin, canvasWidth - drawWidth - margin);
                    y = Math.Clamp(y, margin, canvasHeight - drawHeight - margin);

                    currentRect = new SKRect(x, y, x + drawWidth, y + drawHeight);

                    bool hasTooMuchOverlap = false;
                    float currentArea = drawWidth * drawHeight;

                    foreach (var existRect in placedRects)
                    {
                        // 2つの矩形が交差しているか自前で判定
                        bool intersects = existRect.Left < currentRect.Right && currentRect.Left < existRect.Right &&
                                         existRect.Top < currentRect.Bottom && currentRect.Top < existRect.Bottom;

                        if (intersects)
                        {
                            var intersect = SKRect.Intersect(existRect, currentRect);
                            float intersectArea = intersect.Width * intersect.Height;

                            float existArea = existRect.Width * existRect.Height;

                            // 既存画像または新規画像のいずれかが 50% 以上隠れてしまうかチェック
                            if ((intersectArea / existArea > 0.50f) || (intersectArea / currentArea > 0.50f))
                            {
                                hasTooMuchOverlap = true;
                                break;
                            }
                        }
                    }

                    if (!hasTooMuchOverlap)
                    {
                        break;
                    }
                }

                // 確定した矩形を配置済みリストに追跡登録
                placedRects.Add(currentRect);

                // 規則性を崩すための回転角（-20度 〜 +20度）への拡張
                float angle = (float)((Rand.NextDouble() * 2.0 - 1.0) * 20.0f);

                // ポストカード風描画
                DrawPostcard(canvas, sourceBitmap, x, y, drawWidth, drawHeight, angle);

                if (isCorkboard)
                {
                    // プッシュピンを画像上部中央（フチの上）に配置
                    borderSize = Math.Max(8.0f, Math.Min(drawWidth, drawHeight) * 0.04f);
                    float pinX = x + drawWidth / 2.0f;
                    float pinY = y - borderSize * 0.5f;
                    DrawPushPin(canvas, pinX, pinY, angle);
                }
            }
            catch
            {
                // スキップ
            }
        }
    }
}
