DS4Updater — Integration Specification (implementation-aligned)
=============================================================

この文書は `DS4Windows` 側が `DS4Updater.exe` を起動・連携するための実装仕様です。
以下は現在のリポジトリ実装（Version 3.0.0 を参照）に合わせた要点です。

目的
----
- `DS4Updater` は DS4Windows の最新リリースを検出、ダウンロード、展開、置換までを安全に実行する GUI アップデータです。

重要なパス/概念
----------------
- `ds4UpdaterDir`: Updater 実行ファイルの所在ディレクトリ。自己更新用の `Update Files` はここに作られます。
- `ds4WindowsDir`: DS4Windows 本体のルートディレクトリ（インストール先）。
- `updatesFolder`: ダウンロードした ZIP や一時抽出ディレクトリを保管する場所（通常 `ds4WindowsDir\\Updates` または `ds4WindowsDir` 配下）。

主な起動引数（現行実装で有効）
--------------------------------
- `--ds4windows-path <path>` / `--ds4windows-path=<path>`: DS4Windows のルート。
- `--ds4updater-path <path>` / `--ds4updater-path=<path>`: Updater の実行ディレクトリ（自己更新で使用）。
- `--ds4windows-repo <url|owner/repo>`: DS4Windows のリポジトリを上書き。
- `--ds4updater-repo <url|owner/repo>`: Updater 自身のリポジトリを指定。
- `-autolaunch`: 更新完了後に DS4Windows を自動起動。
- `-skipLang`: 言語パックの取得をスキップ。
- `-user`: 自動起動をユーザー権限で行うフラグ（`forceLaunchDS4WUser`）。
- `--launchExe <name>`: 起動対象の実行ファイル名。
- `--ci`: CI/非対話モード。標準出力に JSON を出力し、対話的プロンプトを抑制。

高レベルフロー（実装に合わせた詳細）
------------------------------------
1. 起動と引数解析
   - `App` が引数を解析し、`MainWindow` を生成、`SetPaths(ds4WindowsPath, ds4UpdaterPath)` を呼ぶ。

2. 権限チェック
   - `AdminNeeded()` により `ds4UpdaterDir` と `ds4WindowsDir` の書き込み検査を行う。
   - 書き込みできない場合は UAC による昇格を促す（`--ci` の場合は昇格せず `admin_required` を返す）。

3. リリース検出
   - `RepoConfig` の設定に基づき GitHub Releases API（`/releases/latest` 等）へ問い合わせ、最新リリースタグとアセットを取得する。

4. ダウンロードと常時最新版適用
   - 実装はローカル版との厳密比較を行わず、常に最新の ZIP アセット（指定アセットタイプ）をダウンロードしてインストールする設計です。

5. 安全な展開手順
   - ZIP は一時抽出フォルダ（例: `Updates\\Extract_{GUID}`）へ展開してから検査・移動する（直接上書きしない）。
   - zip-slip（パス横領）対策：ZIP エントリのパス正規化を行い、想定外パスへの書き込みを防止する。

6. Updater の自己更新
   - Updater 自身の新バージョンが見つかった場合、アセットは `exedirpath\\Update Files\\DS4Windows` に配置される。
   - 終了時に %TEMP% に replacer バッチを作成して、`DS4Updater NEW.exe` を安全に `DS4Updater.exe` に置換する（コピー→削除の順）。
   - replacer 起動中は `skipUpdateFilesCleanupOnExit` フラグをセットして `Update Files` を削除しない。

7. ポストインストール検証
   - 配置後はダウンロードしたリリースタグ（例: `3.0.0`）を正規化してファイルの `ProductVersion` と比較する。
   - 厳密比較ができない場合は、実行ファイルの存在確認をフォールバックとして成功扱いする。

内部メソッド名の注意（デバッガ/拡張向け）
-----------------------------------------
内部的なメソッド名は識別を明確にするため変更されています。参照する場合は以下を確認してください：
- `StartInitialChecks_Ds4Windows` (旧: `StartInitialChecks`)
- `CheckNewerVersionExists_Ds4Windows` (旧: `CheckNewerVersionExists`)
- `StartVersionFileDownload_Ds4Windows` (旧: `StartVersionFileDownload`)
- `StartAppArchiveDownload_Ds4Windows` (旧: `StartAppArchiveDownload`)
- `TrySelfUpdateAndRestartIfNeeded_Ds4Updater` (旧: `TrySelfUpdateAndRestartIfNeeded`)
- `AutoOpenDS4_Ds4Windows` (旧: `AutoOpenDS4`)

ログ / トラブルシュート
-----------------------
- `Logger.Log` / `Logger.LogException` を使用。ログには `_Ds4Updater` / `_Ds4Windows` の識別子が含まれる。
- デバッグログは `%TEMP%\\DS4Updater.log` を参照。

CI / 非対話モードの出力
-----------------------
- `--ci` を指定すると標準出力へ単一行 JSON を出力し、終了コードで判定可能にします。
- 出力例：
```json
{"exit":0,"message":"updated:3.0.0"}
```
- 終了コード一覧（実装）:
  - `0`: 成功（`up_to_date` または `updated:<version>`）
  - `2`: ダウンロード失敗（`download_failed`）
  - `3`: 管理者権限が必要（`admin_required`）
  - `4`: ダウンロード保存不可（`cannot_save_download`）
  - `5`: 置換失敗（`replace_failed`）
  - `6`: 展開失敗（`unpack_failed`）

ビルド/リリース関連の注意
-------------------------
- `scripts/post-build.ps1` と `utils/post-build.ps1` は ZIP 作成前にファイルタイムスタンプを正規化するオプション（`ReleaseTime` 引数）を持ちます。GitHub Actions のワークフローは JST に揃えるステップを追加しており、アーカイブ内の記録時刻を意図した値に揃えます。

呼び出し例
-------------
通常実行:
```powershell
& 'C:\Program Files\DS4Windows\DS4Updater\DS4Updater.exe' --ds4windows-path 'C:\Program Files\DS4Windows' -autolaunch
```

CI 実行（非対話）:
```powershell
& 'C:\build\DS4Updater\DS4Updater.exe' --ds4windows-path 'C:\build\DS4Windows' --ci
```

参照（開発者）
----------------
- 起動引数解析: `Updater2/App.xaml.cs` (`Application_Startup`)
- メインフロー: `Updater2/MainWindow.xaml.cs`
- 自己更新: `Updater2/App.xaml.cs` の Exit ハンドラ
- 結果出力: `Updater2/UpdaterResult.cs`

バージョン
---------
この SPEC はリポジトリ内の現行実装（Version 3.0.0）に合わせて作成されています。



