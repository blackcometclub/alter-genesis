# ALTER GENESIS — Tauri製品版候補

> [!IMPORTANT]
> Tauri 2 + Rust + Svelteを、次の製品版の実装基盤として採用した。まだ移植中であり、一般利用者向け完成版ではない。

Googleカレンダーの選んだ予定を、ユーザーが確認して閉じるまで残る独自パネルで知らせるWindowsアプリ。

## 現在の範囲

- Tauri 2 + Svelte + TypeScript
- Rust製のデスクトップ本体
- 言語、カード大小、通知音、鳴り直しだけに絞った設定画面
- Svelteの4項目をRust側の設定保存へ接続。画面側には別の保存状態を持たせない
- 言語変更時に画面文言とウィンドウタイトルを即時更新
- 壊れた設定と新しい形式は上書きせず保護する
- 開発時だけ`ALTER_GENESIS_SETTINGS_PATH`で隔離設定ファイルを指定できる
- Rustが持つ通知状態から、独立した通知カードを1枚だけ表示して閉じる
- 小さめ376×130／大きめ560×154、40pxのカレンダー色帯、大きな時刻と予定名

WPF版で確認済みのGoogle接続、通知時刻計算、カード、音、鳴り直し、トレイ、保存、自動起動を一度に作り直さず、操作単位ごとに移植する。WPF版は比較と回帰確認のために残す。

旧い初回設定モック、偽のGoogle接続、テスト通知ボタンは製品画面から外した。通知カード一枚の表示・閉じると、起動中の「5分後に再通知」を移植済み。Google接続、予定の通知時刻計算、音はまだTauri版へ移植していない。

## 5分後に再通知

カードの「5分後に再通知」で同じ窓を隠し、Rust側の状態とTokioタイマーで300秒後に同じカードを一度だけ戻す。二重操作・別ID・期限前・古いタイマー・取消後の復活を拒否する。予定の開始時刻は変えない。日本語・英語のボタンは既存の共通高さ32pxへそろえる。

この単位はアプリ起動中の予約だけを扱う。アプリを終了すると待機予約も消える。終了・再起動をまたぐ保存、音、複数件は後続の移植範囲。

検証（2026-11-10）：Rust12試験、整形、Clippy合格、Svelte0エラー/0警告、Tauri Releaseビルド成功。Small・日本語の実画面で32pxの二つのボタンが収まり、「5分後に再通知」で窓が隠れることを確認。短縮せず5分待ち、同じ窓IDに同じ予定一枚が戻ることと、カード内の「閉じる」で終了することを確認した。English・Largeの実画面、再起動復元、音は今回の確認対象外。

もうあるか：Tauriの非同期ランタイムと、依存済みTokioのsleepを利用する。独自の待機スレッドや画面側の別タイマーは作らない。[Tauri spawn](https://docs.rs/tauri/latest/tauri/async_runtime/fn.spawn.html)、[Tokio sleep](https://docs.rs/tokio/latest/tokio/time/fn.sleep.html)。

## 一般配布前のGoogle準備

2026-11-10、[Google公式のSensitive scope verification](https://developers.google.com/identity/protocols/oauth2/production-readiness/sensitive-scope-verification)を確認。既存WPFの要求権限はカレンダー一覧と予定の読取りのみ。一般向けOAuth配布では、ブランド確認・要求権限の審査に備え、アプリ紹介ページ、プライバシーポリシー、ドメイン所有確認、各権限の必要理由、英語の認可フローと機能を示す動画を準備する。個人・開発テスト利用には審査の例外があるが、一般配布の完成条件とは分ける。

今回は公式条件の読取確認だけ。Cloud Consoleの設定変更、申請、公開は行っていない。

## 責任の分け方

- Rust: 設定と通知状態の保存、Google接続、予定取得、時刻計算、鳴り直し、トレイ、単一起動、自動起動ショートカット。
- Svelte: 設定画面と通知カードの描画、利用者の明示的な操作。
- 両者の境界: Rustが状態を持ち、画面へイベントを送る。画面は操作結果をRustへ返し、独自の保存状態を重複して持たない。

Google OAuth情報と認証トークンはソース管理へ入れない。一般配布用のGoogle設定、署名、プライバシーポリシーは別の公開準備として扱う。

## コマンド

```powershell
npm run check
npm run build
npm run tauri build -- --bundles nsis
npm run tauri dev
```

2026-10-30の機能移植前基準は、Release EXE 4.22 MiB、NSISインストーラー 1.38 MiB。設定画面をRust保存へ接続した現在の隔離ビルドは、EXE 4.24 MiB、NSIS 1.39 MiB。最終容量ではない。

## 2026-10-30 設定画面の確認

- `npm run check`: 0エラー、0警告
- `cargo fmt --check`: 合格
- `cargo test`: 5件合格
- `cargo clippy --all-targets -- -D warnings`: 合格
- NSIS Releaseビルド: 成功
- 実画面: 408×392の固定窓で4つの選択欄が切れず、共通高さ32px、左右端と上下の並びがそろうことを確認
- 保存復元: Englishを保存して同じ最終EXEを起動し直すと、英語文言と英語タイトルで復元
- 実操作: 同じ一つの窓でLarge／Pikon／Every 3 minutesへ変更し、隔離JSONへ`large`／`pikon`／`three_minutes`として保存

実画面確認は開発用の隔離設定で行った。一般利用者の既定LocalAppData、実Google、通知カード、音の再生は今回の確認対象ではない。

Google OAuthと実カレンダーにはまだ接続していない。

## 2026-10-30 通知カード1枚の確認

- `npm run check`: 0エラー、0警告
- `cargo fmt --check`: 合格
- `cargo test`: 設定5件＋通知3件＋窓サイズ1件の計9件合格
- `cargo clippy --all-targets -- -D warnings`: 合格
- NSIS Releaseビルド: 成功
- 実画面: 小さめ376×130で、40pxの色帯、15:00、歯医者の予約、閉じるが切れずに表示
- 実操作: 「閉じる」でRustの同じ通知IDを消し、通知窓とプレビュー用プロセスが終了

最初の2回の実画面確認では、TauriのApp URLへquery／fragmentを足したため404になった。正確な`index.html`を開き、Tauri窓ラベルで通知画面を判定する方式へ変更した。続く確認用JSONの形式ミスは製品コードではなくfixture側を直し、最終実画面で合格した。

この確認は固定のプレビュー通知と隔離設定によるもの。実Google予定、通知音、再通知タイマー、複数カードは未確認であり、利用者による最終受け入れとは別に扱う。
