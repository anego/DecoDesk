using System;
using System.IO;
using SkiaSharp;

namespace DecoDesk.IO;

/// <summary>
/// 画像ファイルを完全にデコードすることなく、バイナリヘッダから幅と高さを高速に抽出するクラスです。
/// </summary>
public static class ImageHeaderParser
{
    /// <summary>
    /// 指定された画像ファイルの絶対パスから、幅と高さを取得します。
    /// 自前のバイナリパースを優先し、失敗時や未対応形式の場合は SkiaSharp をフォールバックとして使用します。
    /// </summary>
    /// <param name="filePath">画像ファイルの絶対パス</param>
    /// <returns>幅と高さのタプル。取得失敗した場合は null</returns>
    public static (int Width, int Height)? GetImageSize(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(
                filePath, 
                FileMode.Open, 
                FileAccess.Read, 
                FileShare.Read, 
                bufferSize: 4096, 
                useAsync: false);

            var ext = Path.GetExtension(filePath).ToLowerInvariant();

            (int Width, int Height)? size = null;

            if (ext == ".png")
            {
                size = TryParsePng(stream);
            }
            else if (ext == ".gif")
            {
                size = TryParseGif(stream);
            }
            else if (ext is ".jpg" or ".jpeg")
            {
                size = TryParseJpeg(stream);
            }

            if (size.HasValue)
            {
                return size;
            }

            // 自前解析に失敗した場合、または WebP 等の場合は SkiaSharp.SKCodec でメタデータのみ取得
            stream.Position = 0;
            return ParseWithSkiaSharp(stream);
        }
        catch
        {
            // ファイル破損やI/Oエラーの場合は null を返す
            return null;
        }
    }

    private static (int Width, int Height)? TryParsePng(Stream stream)
    {
        // PNG シグネチャ確認 (8バイト)
        byte[] signature = new byte[8];
        if (stream.Read(signature, 0, 8) != 8) return null;

        if (signature[0] != 0x89 || signature[1] != 0x50 || signature[2] != 0x4E || signature[3] != 0x47 ||
            signature[4] != 0x0D || signature[5] != 0x0A || signature[6] != 0x1A || signature[7] != 0x0A)
        {
            return null;
        }

        // IHDR チャンクヘッダ確認 (長さ 4バイト, タイプ "IHDR" 4バイト)
        byte[] chunkHeader = new byte[8];
        if (stream.Read(chunkHeader, 0, 8) != 8) return null;

        // Chunk Type == "IHDR" (ASCII: 73, 72, 68, 82)
        if (chunkHeader[4] != 73 || chunkHeader[5] != 72 || chunkHeader[6] != 68 || chunkHeader[7] != 82)
        {
            return null;
        }

        // 幅(4バイト) と 高さ(4バイト) - Big-Endian
        byte[] sizeBuffer = new byte[8];
        if (stream.Read(sizeBuffer, 0, 8) != 8) return null;

        int width = (sizeBuffer[0] << 24) | (sizeBuffer[1] << 16) | (sizeBuffer[2] << 8) | sizeBuffer[3];
        int height = (sizeBuffer[4] << 24) | (sizeBuffer[5] << 16) | (sizeBuffer[6] << 8) | sizeBuffer[7];

        return (width, height);
    }

    private static (int Width, int Height)? TryParseGif(Stream stream)
    {
        // GIF シグネチャ確認 (6バイト)
        byte[] signature = new byte[6];
        if (stream.Read(signature, 0, 6) != 6) return null;

        // GIF89a または GIF87a
        if (signature[0] != 0x47 || signature[1] != 0x49 || signature[2] != 0x46 || signature[3] != 0x38 ||
            (signature[4] != 0x39 && signature[4] != 0x37) || signature[5] != 0x61)
        {
            return null;
        }

        // 幅(2バイト) と 高さ(2バイト) - Little-Endian
        byte[] sizeBuffer = new byte[4];
        if (stream.Read(sizeBuffer, 0, 4) != 4) return null;

        int width = sizeBuffer[0] | (sizeBuffer[1] << 8);
        int height = sizeBuffer[2] | (sizeBuffer[3] << 8);

        return (width, height);
    }

    private static (int Width, int Height)? TryParseJpeg(Stream stream)
    {
        // JPEG マジックナンバー確認 (0xFFD8)
        int r1 = stream.ReadByte();
        int r2 = stream.ReadByte();
        if (r1 != 0xFF || r2 != 0xD8) return null;

        while (true)
        {
            int marker = stream.ReadByte();
            if (marker == -1) return null;
            
            // 0xFFに達するまでスキップ
            if (marker != 0xFF) continue;

            // マーカーの種類を取得
            int type = stream.ReadByte();
            while (type == 0xFF) type = stream.ReadByte(); // 冗長なFFをスキップ
            if (type == -1) return null;

            // EOF または圧縮データ開始マーカー (SOS) に達したら探索中止
            if (type == 0xD9 || type == 0xDA) return null;

            // セグメント長を読み取る (2バイト Big-Endian)
            int lenHigh = stream.ReadByte();
            int lenLow = stream.ReadByte();
            if (lenHigh == -1 || lenLow == -1) return null;
            int len = (lenHigh << 8) | lenLow;

            // SOF (Start of Frame) マーカーか判定
            // (SOF0〜SOF3, SOF5〜SOF7, SOF9〜SOFB, SOFD〜SOFE)
            if ((type >= 0xC0 && type <= 0xC3) || (type >= 0xC5 && type <= 0xC7) ||
                (type >= 0xC9 && type <= 0xCB) || (type >= 0xCD && type <= 0xCE))
            {
                int precision = stream.ReadByte(); // 精度 (1バイト)
                int hHigh = stream.ReadByte();
                int hLow = stream.ReadByte();
                int wHigh = stream.ReadByte();
                int wLow = stream.ReadByte();
                if (wLow == -1) return null;

                int height = (hHigh << 8) | hLow;
                int width = (wHigh << 8) | wLow;
                return (width, height);
            }
            else
            {
                // セグメントをスキップ (長さは len 自体の2バイト分を含むため、len - 2 バイトスキップ)
                if (len < 2) return null;
                
                // streamがシーク可能であればシーク、できなければダミーリード
                if (stream.CanSeek)
                {
                    stream.Seek(len - 2, SeekOrigin.Current);
                }
                else
                {
                    byte[] buffer = new byte[len - 2];
                    if (stream.Read(buffer, 0, buffer.Length) != buffer.Length)
                    {
                        return null;
                    }
                }
            }
        }
    }

    private static (int Width, int Height)? ParseWithSkiaSharp(Stream stream)
    {
        // SKCodec はデコードせずにメタデータ情報だけを取り出す
        using var codec = SKCodec.Create(stream);
        if (codec != null)
        {
            return (codec.Info.Width, codec.Info.Height);
        }
        return null;
    }
}
