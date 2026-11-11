# Google verification submission draft

Draft for review, 2026-11-11. Not submitted. Use only after the public policy and in-app policy access match the actual Tauri build.

## App purpose

ALTER GENESIS is a Windows desktop reminder app. Users connect their Google Account, choose calendars, and receive persistent reminder cards with optional sounds. For timed events, the app uses Google Calendar popup reminder settings, including event overrides and calendar defaults. All-day events follow the app's own rule of 9:00 a.m. on the event date in the PC's local time zone. Event creation and editing remain in Google Calendar.

## Scope: calendar.calendarlist.readonly

Full scope: `https://www.googleapis.com/auth/calendar.calendarlist.readonly`

The app retrieves the user's subscribed calendar list so the user can select which calendars should generate reminders. Calendar names and colors identify each calendar in Settings and on reminder cards. Calendar identifiers route subsequent event requests only to the calendars the user selects. The app does not create, subscribe to, edit, or remove calendars.

Free/busy information cannot provide the calendar selection list and its names and colors. The app uses the read-only calendar-list scope instead of a scope that permits changing the subscription list.

## Scope: calendar.events.readonly

Full scope: `https://www.googleapis.com/auth/calendar.events.readonly`

The app reads event titles, start times or dates, cancellation status, popup reminder settings, and calendar default reminders for the calendars the user selects. It uses this information to calculate reminder times and display local cards. It reads attendee self flags and response statuses to exclude events declined by the user; it does not request attendee names or email addresses. Data is not uploaded to a TINMOON server.

Free/busy scopes do not provide event titles or popup reminder settings. An owned-events-only scope would exclude events on shared calendars that the user can read but does not own. The app requests the read-only events scope and never calls event creation, update, or deletion endpoints.

## Data handling summary

Google authentication uses the user's external browser, authorization code flow with PKCE S256, and state validation. The app communicates directly with Google. Google connection data and the Google reminder queue are encrypted locally with Windows CurrentUser DPAPI. App settings and the separate local reminder store use their own files; do not claim all application files are encrypted.

Closing a Google reminder marks its local record dismissed to prevent duplicate notifications. A successful local Disconnect removes Google connection information and the Google reminder queue. It does not revoke the Google Account's app authorization. Users can manage that authorization in their Google Account and can remove local data separately. Public retention and deletion wording must describe these behaviors accurately before submission.

## Demonstration sequence

撮影台本の準備段階。未撮影・未アップロード・未提出。2026-11-11に下記公式要件と現行Svelteの操作入口を照合した。実際に使う本体の版・SHAは撮影直前に記録する。

### 公式要件と今回の撮り方

Googleは、英語での実際のOAuth許可操作、同意画面のアプリ名と申請する権限、アドレスバーのOAuth client ID、各権限を使う機能を求めている。アプリもEnglishにそろえ、短い英語字幕で用途を説明する。録画は音声込みで4〜6分を目安とするが、これは今回の構成案でありGoogle指定の長さではない。

- [Verification requirements: App functionality demonstration video](https://support.google.com/cloud/answer/13464321?hl=en)
- [Sensitive scope verification: submission steps / video](https://developers.google.com/identity/protocols/oauth2/production-readiness/sensitive-scope-verification)

### 録画前に整えるもの

- 審査に出すTauri本体一つを使う。WPFや別の検証用アプリへ途中で切り替えない。
- アプリの言語をEnglish、通知音を聞こえる設定にする。Googleの同意画面も左下の言語でEnglishへ。録画範囲にアプリとブラウザーを含め、システム音が入るか短い試し録りをする。
- Google Calendarに撮影用のカレンダーと架空の予定だけを用意する。例: calendar `ALTER GENESIS Demo`、event `Demo reminder`。作成・変更はGoogle Calendar上で行う。現在の実カレンダーや実予定を改名・削除しない。
- 録画でConnectから許可画面まで進められる状態を先に確認する。現在の通常接続を無断で切らない。アプリのDisconnectはGoogleの許可を撤回しないので、切断だけで完全な同意画面が必ず再表示されるとは扱わない。必要なら撮影に使う承認済みテストアカウントで準備する。現在の接続・選択をどう戻すかを決めてから操作する。
- パスワード、2段階認証、OAuth client secret、token、認可コード、callback URL、実予定、無関係なアカウントや通知を映さない。client IDは公開識別子なので、同意画面で読めるように残す。ログや認証ファイル、Consoleのクライアントシークレット画面は開かない。
- 許可画面からアプリへ戻るまでの流れは省略しない。callbackのアドレスバーに認可コード等が出る箇所は隠し、権限やclient IDが必要な同意画面は隠さない。完成動画を全編見てから共有する。

### 撮る順番

| 順番 | 撮る操作 | 見せる目的 / 英語字幕案 |
| --- | --- | --- |
| 1 | Tauriの設定画面でアプリ名と新しいアイコンを見せる。`Privacy policy`を押し、英語ページを短く見せて設定へ戻る。 | `ALTER GENESIS is a Windows reminder app. Its privacy policy is available before connecting to Google.` |
| 2 | `Connect` → 普段のブラウザー → 撮影用アカウント選択 → 英語のGoogle同意画面。アプリ名、アドレスバーのclient ID、カレンダー一覧と予定の参照の2権限を読める状態にし、両方を許可する。未確認アプリの案内が出た場合も、実際に出た流れを撮る。 | `The user grants read-only access to the calendar list and calendar events in the external browser.` |
| 3 | アプリへ戻り、取得したカレンダー名と色を見せる。撮影用カレンダーにチェックを付ける。 | `Calendar list access lets the user choose which calendars provide reminders.` |
| 4 | Google Calendarで撮影用予定を作成して保存する。開始を「今から5分後」、通知を「2分前」にする例なら、通知は約3分後。予定名・日付・開始時刻・通知の2分前を見せ、実時刻から通知予定時刻を字幕で明記する。 | `This event starts at 14:10. Google Calendar specifies a reminder two minutes before, at 14:08.` ※時刻は当日の実値へ差し替える。 |
| 5 | アプリで`Refresh`を押し、取得完了を待つ。設定を閉じてトレイ常駐にし、通知予定時刻に同じ予定名・開始時刻のカードと音が出るところを撮る。間に合わない場合はGoogle側で未来の時刻へ変更し、更新して撮り直す。 | `Event access supplies the title, start time, and reminder settings. The app displays a local reminder at the Google Calendar reminder time.` |
| 6 | カードの`Close`を押して消えるところまで撮る。 | `Closing this card dismisses the local reminder. It does not change the Google Calendar event.` |

1〜3の認可と一覧表示、4〜5の実予定と通知の対応が中心。待ち時間だけ短縮する場合は、英語字幕で時間経過と実際の通知時刻を示す。通知を後付けで合成したり、試聴や架空のカードを実Google通知の代わりに使ったりしない。

`5分後に再通知`、繰り返し音、終日9時、再起動、インストール操作は今回の短い動画には含めない案。Googleの各権限の用途を示すために必要な流れへ絞る。Googleから追加説明を求められたら補足する。カードからGoogleの予定を開く操作は現行Tauriに存在しないため撮らない。

### 録画後

- 英語の同意画面・2権限・client ID・一覧選択・Googleの通知時刻・実カードが読めるか全編確認する。音と個人情報の映り込みも確認する。
- 撮影のために変更した言語・接続・選択を元へ戻し、試験予定などの扱いを利用者と確認する。録画ファイルは成果物として保持し、勝手に削除・アップロードしない。
- 共有時は公式案内どおりYouTubeのUnlisted（限定公開）を使う。Private（非公開）と混同しない。アップロードとGoogleへの提出・公開切替は別途承認後に実施する。
- state未使用のConsole警告は前の確認で残存している。動画の準備と診断の解消を分け、提出前に再確認する。台本だけで審査用の動画が完成したとは扱わない。

## Fields still to fill

- Final candidate version and binary identity.
- Reviewed demonstration-video URL.
- Current Console contact information and review-specific questions.
- Explanation/evidence for any unresolved current Console diagnostics.

See [the preparation record](README.md) for the observed Console state, remaining changes, and official sources.
