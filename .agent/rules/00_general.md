# General Coding Standards
- **言語・環境**: .NET 10, C# 14, Windows WPF
- **言語設定**: 会話、コードコメント、コミットメッセージはすべて「日本語」とする。
- **基本ルール**:
  - Microsoft公式のコーディング規則・命名規則（PascalCase/camelCase）を厳守する。
  - すべての公開クラス、メソッド、プロパティにトリプルスラッシュのXMLドキュメントコメント（`/// <summary>`）を付与する。
  - Null許容参照型（Nullable Reference Types）を有効化し、警告（Warning）がゼロになるコードを書く。
  - 非同期処理は `async` / `await` を使用し、イベントハンドラを除き `async void` は禁止する。
