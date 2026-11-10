# Tauri版の通常インストーラー

Tauri標準のNSISを使い、Windows x64向けのSetupを作る。設定の正本は`app/src-tauri/tauri.conf.json`。WPF版のInno定義、製品情報、データを上書き・移行しない。

## 固定する場所

| 対象 | 既定の場所 |
| --- | --- |
| 本体 | `%LOCALAPPDATA%\ALTER GENESIS\alter-genesis.exe` |
| アンインストーラー | `%LOCALAPPDATA%\ALTER GENESIS\uninstall.exe` |
| スタートメニュー | `%APPDATA%\Microsoft\Windows\Start Menu\Programs\TINMOON\ALTER GENESIS.lnk` |
| 設定・Google接続・通知 | `%LOCALAPPDATA%\com.automatonprotocol.altergenesis\` |
| 自動起動（利用者がオンにした時） | 現在ユーザーのStartupに`ALTER GENESIS (Tauri).lnk` |

管理者権限を求めない`currentUser`。本体は版番号のない場所に入れ替え、設定保存先とidentifierは変えない。利用者が別の場所を選んだ場合は、NSISがその場所を次回にも引き継ぐ。データは本体横へ置かず、開発用fixtureからも自動コピーしない。

WebView2はPCの共有Evergreenを使う。不足時だけMicrosoftのBootstrapperを取得するため、その場合は通信が必要。固定WebView2や.NETランタイムは同梱しない。インストーラーの表示はOSに合わせた日本語/英語で、余分な言語選択ダイアログは追加しない。

## 作り方

Windows x64 MSVC、Rust、Nodeと`npm ci`済みの`app/`を用意し、リポジトリ直下で実行する。

```powershell
.\tools\Build-TauriInstaller.ps1
# 別のデスクトップOAuth JSONを使う場合
.\tools\Build-TauriInstaller.ps1 -GoogleClientFile 'C:\private\desktop-client.json'
```

省略時は既存のローカル専用`artifacts/oauth-reminder-build/client.json`を使う。空/不正/Webアプリ用の設定では停止する。秘密の値を表示せず、既存`build.rs`経由でデスクトップクライアント設定を埋め込む。JSON自体やトークン/予定/個人の設定を同梱ファイルにしない。

Svelte検査後、公式Tauri CLIでフロントエンド生成・Rust Release・NSIS作成を一続きで実行する。古いEXEをそのまま詰め直す手順ではない。`artifacts/tauri-installer/<version>/<timestamp>/`へSetup、本体、生成NSIS、ログ、サイズ/SHA256/ソースハッシュ/Git状態のmanifestを保存する。過去の出力を削除しない。

NSIS一形式だけなので、公式の`--no-binary-patching`を指定し、記録したEXEとSetup内のEXEを同一にする。Tauriの標準動作は梱包時に形式識別子3バイトを変更してから元EXEを戻すため、そのままでは両ハッシュが異なる。自動更新プラグインや同一プラットフォームでMSI等の併配布を追加する場合は、この指定を再検討する。[公式変更履歴](https://github.com/tauri-apps/tauri/blob/dev/crates/tauri-bundler/CHANGELOG.md)と使用CLIのhelpで範囲を確認した。

## インストール・更新・解除

1. Codex外のExplorerからSetupを起動する。初回は既定フォルダーに入れ、完了後に起動する。
2. 必要なら通常どおりGoogleに接続し、通知対象を選ぶ。サインイン時起動をオンにすると、インストールされた実体へのリンクを作る。
3. 更新の前にトレイの「終了」で閉じる。版を上げる更新にはSetupの`/UPDATE`モードを使う。既定の対話的な「旧版をアンインストールしてから入れる」は削除→再導入であり、自動起動の解除を含む。更新モードでは既存リンク/設定を保つ。同じ版の追加・修復インストールも同じ場所を使う。
4. 通常のアンインストールでは「アプリのデータを削除」のチェックを付けなければ設定・接続を残す。独自フックは自分の実行ファイル＋`--startup`に一致するTauri用Startupリンクだけを除去する。別実行先/別引数/破損リンクやWPF版には触れない。`/UPDATE`中は除去しない。

インストーラーが行うアプリ一覧/アンインストール情報の標準登録と、アプリ自身の設定・自動起動を区別する。アプリはRunレジストリを書き換えず、設定ファイルとStartupリンクを使う。

Codex MSIX内から見えるAppDataだけを実配置の証拠にしない。実インストールは利用者がCodex外で実行し、外部プロセスとExplorerで配置/スタートメニュー/リンク先を確認する。再起動を伴う検証、署名、一般配布、MSIX生成、Store提出、Google審査は別の段階。

## 2026-11-11の確認範囲

- 公式CLI2.12.4でSvelte検査0エラー/0警告、Release/NSIS生成に成功。最終Setupは2,120,130 bytes、本体は6,154,240 bytes。7-Zipで取り出した本体とmanifestのSHA256が一致し、インストーラー内部部品と本体以外の個人データを含まないことを確認した。
- 追加した解除フックを専用Jフォルダーで実行し、自分のリンクの削除、更新中の保持、別引数/別実行先/不正ファイルの保持の5条件に合格。実Startupはこの隔離試験では変更しない。
- 利用者がCodex外のExplorerからインストールし、設定が出たと回答。本体の実行先、インストール情報、スタートメニューのリンク先と最終本体ハッシュがすべて固定先に一致した。開発者はインストール版の設定画面と自動起動オンを画像で確認し、作成されたリンクが固定先＋`--startup`を指すことを照合した。
- 利用者が同じSetupの`/UPDATE /R`を外部で実行し、設定が再び出たと回答。更新前後で固定配置・本体SHA256・保存設定1ファイル・StartupリンクのSHA256が一致した。更新後の開発者による画像取得はEscapeで中断したため、更新の根拠は利用者回答と実行先/保存ファイル照合として記録する。
- 通常保存先のGoogle接続は未設定。検証用fixtureの接続を自動移行しておらず、接続済みデータの更新維持や通知・音の再試験はしていない。実際のアンインストール、版を上げる更新、Windows再起動も別の確認。アプリ本体の変更を伴わない単位なので、前単位のRust44試験を今回再実行したとは扱わない。

詳しい画像/ログ/ハッシュ/隔離試験はGit除外の`artifacts/tauri-installer-20261111/`、最終配布候補は`artifacts/tauri-installer/0.1.0/20261111-002516/`に保存する。署名・公開はしていない。

参考：[Tauri Windows Installer](https://v2.tauri.app/distribute/windows-installer/)、[NSIS設定](https://v2.tauri.app/reference/config/#nsisconfig)。標準テンプレートを置き換えず、追加処理は`app/src-tauri/windows/installer-hooks.nsh`だけにする。
