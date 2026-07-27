# Windows OS Integration Rules (Phase 4)
- **テクノロジー**: P/Invoke, COM Interop (`IDesktopWallpaper`)
- **OS連携の堅牢性**:
  - 壁紙の設定には、レガシーな `SystemParametersInfo` ではなく、マルチモニターをネイティブサポートしているシェルCOMインターフェース `IDesktopWallpaper`（または対応するWin32 API）を使用する。
  - Windows APIの呼び出し（P/Invoke）をラップする際は、`System.Runtime.InteropServices` を適切に使い、戻り値（HRESULT）のエラーハンドリングを厳格に行う。
- **マルチモニター対応**:
  - 接続されている全ディスプレイの「識別子」「解像度（DPI考慮）」「配置座標」を正確に取得する。
  - 各モニターに対して個別の画像（SkiaSharpで生成したキャッシュファイルなど）を独立して適用できる設計にする。
