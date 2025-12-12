DS4Updater — Integration Specification
=====================================

この文書は `DS4Windows` 側が `DS4Updater.exe` を起動・連携するための仕様書です。
実行方法、利用可能な引数、挙動、および注意点を日本語でまとめています。

目的
----
- `DS4Updater` は `DS4Windows` 本体のバージョンチェック、ダウンロード、展開、既存ファイルの置換を自動で行う GUI アップデータです。
- `DS4Windows` はこの Updater を外部プロセスとして呼び出して自己更新フローを実行できます。

概要（重要変数）
----------------
- `ds4UpdaterDir` — DS4Updater 実行ファイルのあるディレクトリ（Updater 自身のフォルダ）。
- `ds4WindowsDir` — DS4Windows 本体のルートディレクトリ（インストール先）。
- `updatesFolder` — ダウンロードした ZIP 等を保存する一時/更新フォルダ（`ds4WindowsDir` 内に作成されることが多い）。

起動引数（呼び出し時に指定可能）
---------------------------------
（`App.xaml.cs` による解析に準拠）

- `--ds4windows-path <path>` または `--ds4windows-path=<path>`
  - Updater に対して DS4Windows のルートを明示的に指定します。
  - 例: `--ds4windows-path "C:\Program Files\DS4Windows"`
- `--ds4updater-path <path>` または `--ds4updater-path=<path>`
  - Updater の実行ディレクトリ（self-update や置換で使用）を指定します。
  - 例: `--ds4updater-path "C:\Program Files\DS4Windows\DS4Updater"`
- `-autolaunch`
  - 更新完了後に DS4Windows を自動起動します（UI 内 `autoLaunchDS4W` が true になります）。
- `-skipLang`
  - 言語パックダウンロードをスキップします。
- `-user`
  - `AutoOpen` 時にユーザー権限で起動することを強制するフラグ（`forceLaunchDS4WUser`）。
- `--launchExe <name>`
  - Updater 側で起動対象の実行ファイル名を指定します（`exedirpath` ベースで解決）。
  - 例: `--launchExe DS4Tool.exe`

リポジトリ指定（オーバーライド）
--------------------------------
Updater はデフォルトで組み込みの GitHub レポジトリ URL を使用しますが、起動引数でオーバーライドできます（`App` が起動引数を解析して `RepoConfig` を作成します）。

- `--ds4updater-repo <url|owner/repo>`
  - DS4Updater 自身のリポジトリを指定。GitHub の URL か `owner/repo` 形式を受け付けます。
- `--ds4windows-repo <url|owner/repo>`
  - DS4Windows のリポジトリを指定（Updater はこれを使い GitHub Releases API から最新情報を取得します）。
- 互換のため `--base-url`（レガシー）は `--ds4windows-repo` として扱われます。

振る舞い（高レベルフロー）
-------------------------
1. `App` が起動引数を解析し、`MainWindow` を生成して `SetPaths(ds4WindowsPath, ds4UpdaterPath)` を呼ぶ。
2. `MainWindow` はまず `AdminNeeded()` で書き込み権限を確認する。現在の実装では `ds4UpdaterDir` と `ds4WindowsDir` の両方へ一時ファイルを書けるかをテストする。
   - いずれかに書き込みができない場合は管理者実行を要求する（UI にメッセージを表示）。
3. ローカルの `version.txt` や `DS4Windows.exe` のファイルバージョンを確認して現在バージョンを特定。
4. `RepoConfig` による API エンドポイント（GitHub Releases の latest）へ問い合わせ、最新リリースタグを取得。
5. 新しいバージョンがあればリリースアセット（ZIP）をダウンロードして `updatesFolder` に保存する。
6. 必要なら DS4Windows のプロセスをユーザー確認のうえ停止し、展開 → 既存ファイルの置換を行う（`DS4Updater.exe` 自身は上書きされないロジックあり）。
6.1. 自己アップデート（Updater 自身の置換）

    - 振る舞い: Updater は新しい `DS4Updater.exe` を `Update Files\DS4Windows\DS4Updater.exe` としてダウンロード/配置した場合、終了時に既存の `DS4Updater.exe` を入れ替える処理を行います。処理の流れは以下の通りです。
      1. 現在のプロセスのバージョンとダウンロードされた `DS4Updater.exe` のバージョンを比較する。
      2. バージョンが異なれば、ダウンロードしたファイルを `DS4Updater NEW.exe` にリネームして一時退避する。
      3. %TEMP% にバッチファイル (`UpdateReplacer.bat`) を作成し、既存 `DS4Updater.exe` を削除して `DS4Updater NEW.exe` を `DS4Updater.exe` にリネームするコマンドを記述する。
      4. バッチを起動して現在の Updater プロセスを終了させ、バッチが入れ替えを実行することで Updater の自己置換を完了する。

    - 実装参照: 実際のコードは [Updater2/App.xaml.cs](Updater2/App.xaml.cs#L112-L160) の Exit ハンドラに実装されています。
7. 更新完了後、`-autolaunch` 等のフラグに従い DS4Windows を起動するか UI を閉じる。

例: DS4Windows からの起動（推奨）
--------------------------------
- Updater を DS4Windows のサブフォルダに配置している場合（例: `C:\Program Files\DS4Windows\DS4Updater\DS4Updater.exe`）:

```powershell
# DS4Windows が Updater を呼び出す例
Start-Process -FilePath "C:\Program Files\DS4Windows\DS4Updater\DS4Updater.exe" -ArgumentList '--ds4windows-path', 'C:\Program Files\DS4Windows', '-autolaunch'
```

- Updater を別場所に置く／リポジトリを指定する例:

```powershell
DS4Updater.exe --ds4windows-path "C:\Program Files\DS4Windows" --ds4updater-path "C:\Tools\DS4Updater" --ds4windows-repo "owner/DS4Windows-Repo" -autolaunch
```

注意点 / 推奨
-------------
- 管理者権限
  - `ds4WindowsDir` が Program Files 以下にある場合、書き込みに管理者権限が必要なことが多いです。呼び出し元（DS4Windows）は必要なら `ProcessStartInfo` で昇格（RunAs）を検討してください。Updater 自身は書込テストを行い、UI で再起動（管理者権限）を促します。
- 実行中の DS4Windows
  - Updater は実行中プロセスを強制終了して更新を行うことがあるため、事前にユーザー保存処理を行わせるか、呼び出し方に注意してください。
- 自己アップデート
  - Updater は更新後に自分自身を置換する仕組みを持っています（Exit 時にバッチ等を起動して入れ替え）。そのため Updater の実行ディレクトリ（`--ds4updater-path`）を明示的に指定すると安定します。
- 引数の互換性
  - 既存の `--base-url` を渡す古い呼び出しもサポートされ、`--base-url` は `--ds4windows-repo` として扱われます。

CI モード（終了コード / 機械判定）
----------------------------
- 目的: 自動化スクリプトや `DS4Windows` 側で Updater の結果を判定できるように、終了コードと標準出力による機械判定を追加しました。
- 有効化: `--ci` を起動引数に渡すと CI/非対話モードの出力を行います（UI 表示はそのままですが、標準出力へ結果 JSON を書き出し、終了コードを設定します）。
- 出力フォーマット（JSON）: 標準出力へ単一行 JSON を出力します。例:

```json
{"exit": 0, "message": "updated:1.2.3"}
```

- 既定の終了コード（実装済み）:
  - `0` : 成功（`up_to_date` または `updated:<version>`）
  - `2` : ダウンロード失敗（`download_failed`）
  - `3` : 管理者権限が必要（`admin_required`）
  - `4` : ダウンロード保存不可（`cannot_save_download`）
  - `5` : 置換失敗（`replace_failed`）
  - `6` : 展開失敗（`unpack_failed`）

- 実装の参照: `Updater2/UpdaterResult.cs`（`UpdaterResult` が `ExitCode`/`Message` を管理し、`--ci` 時に JSON を出力します）。アプリ終了時に `UpdaterResult.WriteAndApply()` が呼ばれて結果が出力され、`Environment.ExitCode` が設定されます。

- 呼び出し例（CI 連携）:

```powershell
& 'path\to\DS4Updater.exe' --ds4windows-path 'C:\Program Files\DS4Windows' --ci
```

実装での参照先（開発者向け）
---------------------------
- 起動引数の解析: `App.xaml.cs`（`Application_Startup`）
- パス注入: `MainWindow.SetPaths(string ds4WindowsPath, string ds4UpdaterPath)`
- リポジトリ管理: `RepoConfig`（`RepoConfig.FromArgs(...)`, `FromEnvironmentArgs()`）
- バージョン確認／ダウンロード／展開: `MainWindow` 内の `StartVersionFileDownload`, `StartAppArchiveDownload`, `wc_DownloadFileCompleted` 等


