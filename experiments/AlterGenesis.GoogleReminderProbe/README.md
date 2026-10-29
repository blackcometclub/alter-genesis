# OAuth通知時刻の読み取り実験

2026-10-29。通常のWPF/iCalアプリとは独立した、通知機能のないWindows用検証プログラム。
実験ブランチ `experiment/google-oauth` (`400cc8c`) の暗号化保存処理と保存形式を再利用する。
通常版のソリューションには追加せず、アプリの起動・カード・音・通知予約へ接続しない。

## 今回の人の操作

開発側がテスト用デスクトップクライアントをビルドへ組み込む → 必要な場合だけ既定ブラウザーでログイン・許可 → 選択済みカレンダーのテスト予定を読み取る → 計算結果を確認する。
JSON選択UIはない。`--read` はブラウザーを開かず、認証を復元できなければ `ReconnectRequired` で止まる。
`--connect` は明示的な再接続操作。失効したトークンの処理は隔離コピーだけを対象にする。

## 準備と実行（開発者用）

リポジトリ直下から、PowerShell 7 / .NET SDK 8以降で実行する。

```powershell
./experiments/AlterGenesis.GoogleReminderProbe/Prepare-Probe.ps1
dotnet restore experiments/AlterGenesis.GoogleReminderProbe/AlterGenesis.GoogleReminderProbe.csproj --locked-mode
dotnet build experiments/AlterGenesis.GoogleReminderProbe/AlterGenesis.GoogleReminderProbe.csproj --no-restore
dotnet experiments/AlterGenesis.GoogleReminderProbe/bin/Debug/net8.0-windows/AlterGenesis.GoogleReminderProbe.dll --self-test artifacts/oauth-reminder-checks.json
dotnet experiments/AlterGenesis.GoogleReminderProbe/bin/Debug/net8.0-windows/AlterGenesis.GoogleReminderProbe.dll --read 2026-10-29 artifacts/oauth-reminder-live artifacts/oauth-reminder-live-result.json
```

`ReconnectRequired` が出た場合だけ、同じ引数の `--read` を `--connect` に置き換える。ブラウザーでの操作が必要なので、ユーザーへ説明してから実行する。テスト中のGoogle認可には同意から7日の制限があり、更新トークンも失効する。失効以外の通信エラーで保存トークンを削除しない。

`Prepare-Probe.ps1` は保存済み `artifacts/snooze-manual/google` を変更せずに読み、次を作る。

- `artifacts/oauth-reminder-build/client.json`: デスクトップ用の公開クライアント設定だけを復号したビルド入力。ユーザーのアクセストークン・更新トークンは含めない。Git除外を先に確認し、コンソールには内容を出さない。
- `artifacts/oauth-reminder-live/google/*.dpapi`: 認証・選択の隔離コピー。既存コピーは上書きしない。更新されたトークンはこのコピーにだけ保存される。

実クライアントの用意がなくてもビルドと偽データ試験は可能。実読み取りは `ClientNotPrepared` で止まる。実クライアントを埋め込んだbin/objもGit対象外で、今回の出力を製品として配布しない。

## 読み取りと計算の規則

- 保存済みの選択カレンダーだけを読む。選択がなければ止まり、全カレンダーへ広げない。
- 指定日は今回の日本時間（UTC+09:00）で一日。タイトルが `テスト` と完全一致する予定だけを結果にする。過去の予定も計算できる。
- Google APIへは読み取り専用の `calendar.calendarlist.readonly` と `calendar.events.readonly` だけを要求する。Calendar APIへの予定の書き込みはない。
- `Events.list` は期間指定・繰り返しの展開・ページ送りに対応。取得項目をID、タイトル、状態、開始、通知設定など必要なものだけへ限定する。APIの期間下限は終了時刻に作用するため、計算後にも開始日を確認し、前日から重なる予定を除く。
- `CalendarList.get` でカレンダーごとの `defaultReminders` を先に一度取得し、全ページへ同じ値を適用する。取得失敗を「既定通知なし」へ置き換えない。
- `reminders.useDefault=true` は取得した既定通知を使用する。falseは個別設定を使用し、個別設定が空なら通知なし。
- `popup` の分数を開始時刻から引き、UTCで計算する。複数の異なる時刻を保持し、同じ分数の重複はまとめる。メール通知は件数だけ区別し、popupへ変換しない。
- 未対応の通知方法は件数として区別し、混在する有効なpopupを失わない。popup/emailの不正な分数は不正な設定として扱う。
- 通知設定が欠けている場合は「不明」とする。終日は保留とし、9時などへ補完しない。取消・不正な値から時刻を作らない。
- 予定の `updated` で読み飛ばさない。通知だけの変更ではこの値が変わらないため。
- 途中ページ・途中カレンダーの失敗では成功結果を出さない。以前の結果ファイルがある場合も、今回の終了コードが0でなければ今回の成功結果として扱わない。

`Q` とタイトル完全一致は、この実験でテスト予定だけを扱うための制限であり、通常版の通知対象の条件ではない。本番の通知予約へ統合するときはこの制限を外し、最大4週間前の通知を見落とさない取得範囲を別途設計する。今回の指定日一日という範囲を、そのまま本番スケジューラーへ転用しない。

結果には時刻・分数・設定の種類だけを残す。タイトル、カレンダー名/ID、予定ID、認証URL、トークンは出さない。SDK例外本文も出さない。
同じ隔離保存先への二重起動は `probe.lock` の排他ファイルハンドルで防ぐ。終了後に残る0バイトのファイル自体はロック状態を意味しない。

## 確認状況

- 2026-10-29、ビルドは警告0・エラー0。レビュー反映後の偽データによる24件の自動試験に成功。結果は `artifacts/oauth-reminder-checks-reviewed.json`。ページに既定通知が含まれない場合、カレンダー取得の失敗、未対応方法と有効popupの混在も確認した。
- 17:26 JST、保存済み認証を復元し、選択済み1カレンダーから実テスト1件を取得できた。17:15開始・個別popup 2分前を読み、17:13（08:13 UTC）と計算した。新たなログイン・JSON選択は不要だった。
- 17:45 JST、ユーザー了承のもとブラウザーで同日の16:00・16:30にテスト予定を2件追加し、既存の17:15と合わせて3件を実取得した。Google Calendarプラグインはインストール済みだったが、このチャットには操作ツールが提供されていなかった。結果は `artifacts/oauth-reminder-live-three-cases.json`、画面記録は `artifacts/oauth-reminder-test-events.png`。
- 16:00は `Default / NoReminders`。16:30も、画面で通知を追加後に削除して保存すると `Default / NoReminders` として返った。したがって実データで確認できたのは「個別2分前」と「空の既定通知」であり、`useDefault=false` かつ空の個別通知、および分数を持つ既定通知は未確認。偽データでは両方を区別して計算・抑止できている。既存の予定やカレンダー全体の設定は編集していない。追加した2件は再確認用に残している。
- 元のOAuth保存3ファイルとiCal登録1ファイルの前後ハッシュは一致した。通常版は別プロセスで稼働中で、通知状態ファイルの前後一致は確認できていない。実験プログラムの保存先は隔離コピーのみで、通知機能への接続・発火・音は行っていない。
- 対話的なログイン、7日後の再接続、複数アカウント切替、終日の通知時刻、通常版への統合は未確認・対象外。

## 参照

- https://developers.google.com/identity/protocols/oauth2/native-app
- https://developers.google.com/workspace/calendar/api/v3/reference/events
- https://developers.google.com/workspace/calendar/api/v3/reference/events/list
- https://developers.google.com/workspace/calendar/api/concepts/reminders
- https://support.google.com/cloud/answer/15549945?hl=en
