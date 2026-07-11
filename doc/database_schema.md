# DecoDesk データベース仕様書 (SQLite Schema)

DecoDesk アプリケーションが使用するメタデータ管理用データベースのスキーマ、インデックス設計、およびアクセス最適化について記載します。

---

## 1. データベース配置

* **デフォルトパス**: `%LOCALAPPDATA%\DecoDesk\decodesk.db`
  * 例: `C:\Users\<ユーザー名>\AppData\Local\DecoDesk\decodesk.db`
* **接続文字列の設定**:
  ```ini
  Data Source=<DBパス>;Mode=ReadWriteCreate;Cache=Shared;
  ```

---

## 2. テーブル定義

### 2.1. `Images` テーブル

アプリケーションが走査した画像ファイルのメタデータを保持します。

| カラム名 | データ型 | 制約 | 説明 |
| :--- | :--- | :--- | :--- |
| `Path` | TEXT | PRIMARY KEY | 画像ファイルの絶対パス（インデックスの一意性を保証） |
| `Width` | INTEGER | NOT NULL | 画像の幅（ピクセル数） |
| `Height` | INTEGER | NOT NULL | 画像の高さ（ピクセル数） |
| `AspectRatio` | REAL | NOT NULL | アスペクト比（Width / Height / 小数点） |
| `LastUsed` | INTEGER | NOT NULL | 最後に壁紙として適用された日時（Unixタイムスタンプ / 未適用時は `0`） |
| `IsFavorite` | INTEGER | NOT NULL, DEFAULT 0 | お気に入りフラグ（`0` = 通常, `1` = お気に入り） |

#### DDL定義
```sql
CREATE TABLE IF NOT EXISTS Images (
    Path TEXT PRIMARY KEY,
    Width INTEGER NOT NULL,
    Height INTEGER NOT NULL,
    AspectRatio REAL NOT NULL,
    LastUsed INTEGER NOT NULL,
    IsFavorite INTEGER NOT NULL DEFAULT 0
);
```

---

## 3. インデックス設計

40万件規模の大規模な画像インデックスから、特定の条件で高速に抽出を行うために以下のインデックスを作成しています。

### 3.1. `IX_Images_AspectRatio`
* **対象カラム**: `AspectRatio`
* **目的**: モニター解像度とマッチするアスペクト比を持つ画像を高速に絞り込むため。
* **DDL**:
  ```sql
  CREATE INDEX IF NOT EXISTS IX_Images_AspectRatio ON Images(AspectRatio);
  ```

### 3.2. `IX_Images_LastUsed`
* **対象カラム**: `LastUsed`
* **目的**: 直近で適用されていない（古い画像、あるいは未使用の画像）を高速に判定し、壁紙の重複適用を避けるため。
* **DDL**:
  ```sql
  CREATE INDEX IF NOT EXISTS IX_Images_LastUsed ON Images(LastUsed);
  ```

---

## 4. データベースの最適化設定 (性能非機能要件)

大量のデータ登録と並行アクセスに対するゼロ・レイテンシ維持のため、接続初期化時に以下の PRAGMA コマンドを必ず実行します。

1. **WALモードの有効化** (`PRAGMA journal_mode = WAL;`)
   * 読み取り処理と書き込み処理の並行実行を可能にし、壁紙更新時の遅延を完全に防ぎます。
2. **同期モードの変更** (`PRAGMA synchronous = NORMAL;`)
   * WALモードにおけるディスクへの書き込みタイミングを最適化し、バルクインサート速度を向上させます。

---

## 5. 高速ランダム抽出クエリの設計

SQLiteにおける単純な `ORDER BY random() LIMIT 1` は、すべてのレコードに対してランダムな値の生成とソート（全件スキャン）が発生するため、40万件の規模ではパフォーマンスが低下します。

DecoDesk では、作成したインデックス（`IX_Images_AspectRatio` および `IX_Images_LastUsed`）を活用し、以下の2ステップでランダムな1件を抽出します（10ミリ秒以下）。

### ステップ1: 条件に合致する件数の高速カウント
```sql
SELECT COUNT(*) FROM Images 
WHERE AspectRatio >= @MinRatio 
  AND AspectRatio <= @MaxRatio 
  AND LastUsed <= @MaxLastUsed;
```
* インデックスによる範囲検索となるため、非常に高速に件数 `N` が取得できます。

### ステップ2: 乱数Rを用いた1件抽出
取得した件数 `N` に基づき、プログラム側で `0` から `N-1` の間の乱数 `R` を作成し、OFFSET句でランダム位置のレコードを直接取り出します。
```sql
SELECT Path, Width, Height, AspectRatio, LastUsed, IsFavorite 
FROM Images 
WHERE AspectRatio >= @MinRatio 
  AND AspectRatio <= @MaxRatio 
  AND LastUsed <= @MaxLastUsed
LIMIT 1 OFFSET @Offset; -- @Offset に乱数Rをバインド
```
* SQLiteは条件に合致するインデックスのエントリをスキャンするだけで済み、不要な画像データのデコードや全レコードのランダムソートが発生しないため、超高速で1件取得できます。
