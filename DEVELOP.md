# DecoDesk 開発ガイド (DEVELOP.md)

本書は「DecoDesk」プロジェクトのローカル開発環境の構築、ビルド、およびテスト手順（品質保証プロセス）を定めたドキュメントです。

## 1. 開発環境の構築

### 1-1. 前提条件とSDKのインストール
* **OS:** Windows 10 または Windows 11 (64-bit)
* **.NET SDK:** .NET 10.0 以上

~~PowerShellを管理者権限で開き、以下のコマンドでインストールします（完了後に再起動を推奨）。~~
~~```powershell~~
~~winget install Microsoft.DotNet.SDK.8~~
~~```~~
(https://learn.microsoft.com/ja-jp/dotnet/core/install/windows)から環境にあった方法でインストールを行う。

### 1-2. エディタと拡張機能

VS Codeを使用し、プロジェクトルートを開きます。`.vscode/extensions.json` の推奨に従い、以下の拡張機能をインストールしてください。

* C# Dev Kit (`ms-dotnettools.csdevkit`)
* SQLite (`alexcvzz.vscode-sqlite`)
* Cline (`saoudrizwan.claude-dev`) ※AIエージェント用

## 2. ビルドと実行

本プロジェクトは `.NET CLI` をベースに操作します。VS Codeのターミナル（`Ctrl + ``）を開いて実行します。

### 2-1. 開発モードでの起動

```powershell
dotnet run --project src/DecoDesk/DecoDesk.csproj
```

* **注意:** 起動してもウィンドウは出ず、タスクトレイにアイコンが常駐します。終了はタスクトレイの右クリックメニュー、またはターミナルでの `Ctrl + C` で行います。

### 2-2. リリースビルドの生成

最適化された単一実行ファイル（またはポータブルファイル）を生成します。

```powershell
dotnet publish src/DecoDesk/DecoDesk.csproj -c Release -r win-x64 --self-contained false
```

## 3. テスト手法 I: 自動化テスト (xUnit)

ドメインロジック（画像パスの解析、高速なSQLiteクエリの構築、設定のパースなど）は、UIに依存しない形で分離し、自動テストで品質を担保します。

### 3-1. テストプロジェクトのセットアップ（初回のみ）

```powershell
# テストプロジェクトの作成
dotnet new xunit -n DecoDesk.Tests -o src/DecoDesk.Tests

# メインプロジェクトへの参照を追加
dotnet add src/DecoDesk.Tests/DecoDesk.Tests.csproj reference src/DecoDesk/DecoDesk.csproj

# ソリューションへの追加
dotnet sln add src/DecoDesk.Tests/DecoDesk.Tests.csproj
```

### 3-2. テストの実行

AIエージェントにロジックを実装させた後は、必ず以下のコマンドで全テストがPassするか確認させてください。

```powershell
dotnet test
```

## 4. テスト手法 II: 手動・UI動作テスト

OSの機能（Windows API）に依存する部分は、目視での動作確認を行います。

* **UI確認:** タスクトレイアイコンの右クリックメニューが正しく展開されるか。
* **DB確認:** VS Codeの「SQLite: Open Database」から `decodesk.db` を開き、画像パスや解像度が意図通りにテーブルに格納されているか。
* **壁紙適用:** 「今すぐ変更」を実行後、マルチモニターそれぞれの壁紙が（ラグなく）瞬時に切り替わるか。

## 5. テスト手法 III: 大規模パフォーマンステスト (40万枚・2.5TB検証)

実運用環境（40万ファイル）を想定した限界テストを行い、I/Oの詰まりやメモリリークがないかを検証します。

### 5-1. ダミーファイルの大量生成

実際の2.5TBの画像を用意するのは困難なため、テスト用ディレクトリに空のダミーファイル（サイズ0バイト）を大量生成して、インデクサーの速度を計測します。

**ダミーファイル生成用PowerShellスクリプト例 (10万ファイル生成):**

```powershell
$testDir = "./TestImages"
New-Item -ItemType Directory -Force -Path $testDir
1..100000 | ForEach-Object { 
    $null = New-Item -Path "$testDir\image_$_.jpg" -ItemType File 
}
```

### 5-2. メモリと速度のプロファイリング検証項目

大量のファイルを対象ディレクトリに設定し、以下の基準を満たすか確認します。

1. **LOH (Large Object Heap) の圧迫回避:**
* 初期スキャン時、タスクマネージャー上のアプリのメモリ消費が `数MB 〜 数十MB` に収まっていること（`Directory.EnumerateFiles` が正しく効いていれば数百MBには膨らみません）。


2. **SQLite バルクInsertの速度:**
* 10万件のレコード追加が数秒以内に完了すること（WALモードおよびトランザクションが正しく機能していること）。


3. **バックグラウンド待機時の負荷:**
* 待機中のCPU使用率が `0%` であり、メモリが安定していること。



## 6. AIエージェント（Continue）への指示ガイド

Continueを使用してタスクを進める際は、チャット画面（`Ctrl+L`）を開き、`@` 記号を使用して関連ファイルを明示的にコンテキストに含めながら指示を出します。
※ `.continue/rules/` 配下のコーディング規約やアーキテクチャルールは自動で読み込まれるため、メンション不要です。

**基本プロンプトの例:**
> 「@TASKS.md の Phase X の [ ] X-X を実装してください。実装後、xUnitによるテストコードも併せて作成してください。」

**機能要件が絡む複雑なタスクの場合の例:**
> 「@TASKS.md の [ ] X-X に着手します。要件については @requirements definition.md を参照し、最適な設計で実装してください。」

* **ヒント:** 既存のコードを修正・リファクタリングさせる場合は、対象のファイルを開いた状態で `Ctrl+I` を押し、インラインで「このメソッドの非同期処理を最適化して」と指示すると効率的です。
