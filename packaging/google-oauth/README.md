# Google OAuth公開・審査の準備

確認日: 2026-11-11 JST。対象はTauri版0.1.0。これは準備資料で、公開切替・審査提出の完了記録ではありません。

## 進める順番

1. 公開ページの説明を、現在のTauri版の動作へ合わせる。
2. アプリ内からプライバシーポリシーを開けるようにする。
3. Google側の表示・登録内容と、審査用本体を照合する。
4. 実際の認可と通知を英語で撮影し、権限の利用理由と合わせて確認する。
5. 公開設定の切替・ブランド確認・データアクセス審査は、画面に出る手順に沿って別途承認を得て行う。

もうあるか: Google Auth Platformのブランディング、データアクセス、検証センターを使用する。独自の申請システムや認証中継は作らない。

## Google側で読み取れた状態

2026-11-11にChrome拡張のBrowser Useで、既存アカウントとALTER GENESISプロジェクトを照合して読取り。保存・公開・提出はしていない。

| 項目 | 確認結果 |
| --- | --- |
| アプリ名 | ALTER GENESIS |
| 検証センター | 公開ステータスがテスト中なので検証不要、と表示。一般配布の承認済みという意味ではない |
| ホームページ | https://tinmoon-label-site.pages.dev/works/alter-genesis/ |
| ポリシー | https://tinmoon-label-site.pages.dev/works/alter-genesis/privacy/ |
| 承認済みドメイン | tinmoon-label-site.pages.dev |
| 非機密スコープ | `https://www.googleapis.com/auth/calendar.calendarlist.readonly` |
| 機密性の高いスコープ | `https://www.googleapis.com/auth/calendar.events.readonly` |
| 制限付きスコープ | 登録なし |

所有確認は2026-11-10にSearch Consoleで成功した記録がある。当日にSearch Consoleへ入り直した確認ではない。公開紹介・日本語/英語ポリシーは今回もHTTP200で取得できた。

概要の診断には「安全なフローの使用」の警告があった。現行の`google_auth.rs`はPKCE S256とstateを使い、`google.rs`はコード交換でverifierを渡している。警告の対象期間・クライアント別の原因は未確定。過去のアクセスや診断更新遅延と決めつけず、提出前に現行フローとの対応を確認する。警告だけを根拠に現行本体がPKCE未使用と断定しない。

## 先に直す説明・入口

以下は2026-11-11の修正前の公開文面と現行コードを比較した差分。その後の日英本文公開とアプリ内入口の進捗は、下のチェックリストで管理する。

| 現行の公開説明・不足 | 現在のTauri版 | 必要な対応 |
| --- | --- | --- |
| Google連携を新しい配布版へ移植中と説明 | 通常インストール本体で接続・選択・実通知・音・再起動保持を確認済み | 開発状況を更新。未公開であることと、未実装であることを混ぜない |
| 旧WPFの保存先を案内 | Local AppDataの`com.automatonprotocol.altergenesis` | 保存先を更新 |
| 通知記録JSONはアプリで暗号化しないと説明 | Google接続とGoogle通知はCurrentUser DPAPI、設定や従来の単独再通知JSONは別 | Google通知とその他のファイルを区別する |
| 閉じると保存した通知を削除すると説明 | Google通知は重複防止のため`dismissed`を保存 | 即時削除と書かない。同期時の整理と、利用者による全データ削除を説明する |
| 切断時にGoogle認可も撤回すると説明 | ローカルの接続・Google通知を削除。Google側の認可撤回はしない | ローカル切断とGoogleアカウントでのアクセス解除を区別する |
| 参加者一覧を取得しないと説明 | 氏名/メールは要求せず、参加者の本人フラグ・出欠状態を取得して本人辞退を除く | 取得フィールドと用途を正確に書く |
| カードからGoogleの予定を開けると説明 | 現行Tauriのカードにはそのリンクがない | リンク機能を戻すか、紹介文の該当記述を修正する。審査動画で未実装の操作を見せたことにしない |
| アプリ内のポリシー入口なし | 現行設定画面に公開ポリシーへのリンクなし | 認可前も認可後も到達できる短い入口を用意する |

根拠: `app/src-tauri/src/google_store.rs`、`google_queue.rs`、`google_events.rs`、`google.rs`、`lib.rs`、`app/src/lib/NotificationCard.svelte`、`app/src/routes/+page.svelte`。

閉じたGoogle通知は、同期時に通知期限が30日より古いものを整理する実装。停止中・同期しない間も必ず30日以内に消えるという保証にはしない。取得した予定本文の全量や認証値を審査資料へ添付しない。

## 提出前にそろえるもの

- [x] 日英の紹介とポリシーを同じTauri版の動作へ修正し、公開URLで照合する（2026-11-11、サイトmain `021aad6`）。
- [x] アプリ内のポリシー入口を用意し、利用者が確認する（2026-11-11、固定配置の更新本体で日英とも既定ブラウザーへ開き、設定窓を保持。表示言語を元へ復帰）。
- [x] カードから予定を開く機能の説明を紹介文から外す（同日公開）。
- [ ] サポート連絡先が受信可能で、同意画面の表示として適切か確認する。
- [ ] 診断の安全なフロー警告を現行クライアントと照合する。
- [ ] [英語の権限理由・動画台本](submission-draft.md)を最終本体と照合する。
- [ ] 認可画面を含む実動画を撮影し、限定公開URLを用意する。アップロードは別途承認する。
- [ ] 公開設定と提出内容を確認できる状態にして、最後に承認を得る。

アプリ本体の画面確認は利用者が担当する。Browser UseによるWebページの確認と、Computer UseによるWindowsアプリの操作を区別する。審査動画の撮影時も、現在の接続を無断で切断しない。

## 公式資料

- [Calendar APIのスコープ](https://developers.google.com/workspace/calendar/api/auth)
- [機密スコープの審査と申請手順](https://developers.google.com/identity/protocols/oauth2/production-readiness/sensitive-scope-verification)
- [ホームページ・ポリシー・動画等の確認要件](https://support.google.com/cloud/answer/13464321?hl=en)
- [Google Auth Platform概要と安全なフローの診断](https://support.google.com/cloud/answer/15548748?hl=en)

要件や画面の順番は変更されるため、実際の提出時に再確認する。ブランド確認、公開ステータス、機密データアクセスの審査を同じ完了状態として扱わない。
