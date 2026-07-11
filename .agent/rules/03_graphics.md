# Graphics Engine Rules (Phase 3)
- **テクノロジー**: `SkiaSharp`
- **メモリ管理の厳守**:
  - SkiaSharpのオブジェクト（`SKBitmap`, `SKCanvas`, `SKPaint`, `SKImage` など）は、アンマネージドリソースを保持しているため、メモリリークの危険性が非常に高い。
  - 生成したすべてのSkiaSharpオブジェクトは、必ず `using` ステートメントまたは `using` 宣言を用いて、スコープを抜ける際に確実に `Dispose()` させる。
- **レンダリングロジック**:
  - 「ポストカード風加工（白いフチ、ドロップシャドウ、傾き描画）」や「コルクボード風加工（背景テクスチャ合成）」は、マルチスレッド環境で安全に並列処理（`Parallel.ForEach` など）ができるよう、状態を持たない（Stateless）純粋関数として実装する。
