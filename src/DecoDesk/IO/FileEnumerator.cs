using System;
using System.Collections.Generic;
using System.IO;

namespace DecoDesk.IO;

/// <summary>
/// ディレクトリ配下のファイルを、アクセス拒否やIO例外を安全にスキップしながら列挙するクラスです。
/// </summary>
public static class FileEnumerator
{
    // 対象とする画像ファイルの拡張子（比較を高速化するために大文字小文字を区別しないHashSet）
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp"
    };

    /// <summary>
    /// 指定されたルートパスから、対応する画像ファイルの絶対パスを安全に列挙します（遅延評価）。
    /// </summary>
    /// <param name="rootPath">走査を開始するルートディレクトリのパス</param>
    /// <returns>画像ファイルの絶対パスのシーケンス</returns>
    public static IEnumerable<string> EnumerateImageFiles(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            yield break;
        }

        if (!Directory.Exists(rootPath))
        {
            yield break;
        }

        var queue = new Queue<string>();
        queue.Enqueue(rootPath);

        while (queue.Count > 0)
        {
            var currentDir = queue.Dequeue();

            // 1. ファイルの列挙
            IEnumerable<string>? files = null;
            try
            {
                files = Directory.EnumerateFiles(currentDir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or PathTooLongException or IOException)
            {
                // アクセス権限のないフォルダや消失したフォルダ等は無視してスキップ
                continue;
            }

            if (files != null)
            {
                foreach (var file in files)
                {
                    var ext = Path.GetExtension(file);
                    if (SupportedExtensions.Contains(ext))
                    {
                        yield return file;
                    }
                }
            }

            // 2. サブディレクトリの取得と追加
            IEnumerable<string>? subDirs = null;
            try
            {
                subDirs = Directory.EnumerateDirectories(currentDir);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or PathTooLongException or IOException)
            {
                continue;
            }

            if (subDirs != null)
            {
                foreach (var subDir in subDirs)
                {
                    queue.Enqueue(subDir);
                }
            }
        }
    }
}
