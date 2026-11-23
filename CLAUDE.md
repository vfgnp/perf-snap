# PerfSnap（仮称）

Windows 11 で、操作が重くなる事象が起きたときに、ワンクリック／ホットキーで調査用データ（パフォーマンスカウンター・ETW トレース）を保存するタスクトレイ常駐ツール。

## 進め方
要件定義 → 基本設計 → 詳細設計 → 実装 → テスト → GitHub へ公開（exe は Releases に添付）。
各工程の文書は `docs/` に置く。
- `docs/01-requirements.md`：要件定義書（v1.0 確定）
- `docs/02-basic-design.md`：基本設計書
- `docs/03-detailed-design.md`：詳細設計書
- `docs/04-test-spec.md`：テスト仕様書、`docs/05-test-report.md`：テスト結果報告書
- `scripts/verify-integration.ps1`：結合テスト（管理者権限で実行。結果とトレースは Git に入れない）

## この PC の注意点
- スマート アプリ コントロールが有効。ビルドし直したテスト用 DLL がブロックされることがある。設定は変えない

## 技術
- C# / .NET 10（LTS）/ WinForms。.NET 込みの単体 exe（x64）
- dotnet：C:\Program Files\dotnet\dotnet.exe
- 本体：`src/PerfSnap`、自動テスト：`tests/PerfSnap.Tests`（xUnit）
- テスト：`dotnet test PerfSnap.slnx`
- 配布用 exe：`dotnet publish src/PerfSnap -c Release -o publish`（publish/ は Git に入れない）
- Windows に依存する部分（外部コマンド・時計・wpr・logman）はインターフェース越しに呼び、テストでは模擬に差し替える

## 作業のルール
- 返答とドキュメントは日本語で書く
- 待機中に CPU をほぼ使わないことを最優先にする（要件 N-01）
- 記録には Windows 標準の `logman.exe` と `wpr.exe` だけを使う
- ファイルの削除は利用者が行う（削除コマンドは設定で禁止している）
- このフォルダの外のファイルは読み書きしない
- 取得したトレースやログは、PC 内の情報を含むため Git に入れない
- 認証情報をファイルに書かない

## 環境
- OS：Windows 11、シェル：PowerShell
- Git：C:\Program Files\Git\cmd\git.exe
