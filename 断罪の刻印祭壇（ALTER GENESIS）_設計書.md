# 断罪の刻印祭壇（ALTER GENESIS）設計書

更新日: 2026-10-28  
対象OS: Windows  
状態: 初期設計  
旧企画名: Tillio（チリオ）

## 1. 設計原則

1. 通知パネルが本体。
2. Voice Systemは完全に追加機能。
3. 音声APIが落ちても通知は落とさない。
4. Googleカレンダー全体ではなく、ユーザーが選んだカレンダーだけを扱う。
5. 通知はユーザー自身が閉じるまで残る。
6. APIキー・OAuthトークンを安全に保存する。
7. 音声生成サービスはProvider方式で交換可能にする。
8. AIが文章を考える処理と、音声合成処理を分離する。

## 2. 推奨構成

Windowsデスクトップアプリ。

技術候補:
- Rust + Tauri 2
- UI: Svelte / TypeScript
- ローカルDB: SQLite
- HTTP: Rust側
- 認証情報: Windows Credential Manager
- トレイ常駐

技術スタックは実装時に最終決定する。

## 3. モジュール

### CalendarCore
Google Calendarとの接続とイベント同期。

### ScheduleEngine
いつ通知するかを決める。

### PersistentPanel
消えない独自通知パネル。

### NotificationStore
通知済み・未確認・再通知・履歴を保存。

### SpeechPlanner
イベントを短い自然文にする。

### TtsProvider
文章を音声へ変換する。

### AudioPlayer
音声再生。

### CredentialStore
OAuthトークンとAPIキーを安全に保持。

### Settings
ユーザー設定。

## 4. 全体データフロー

Google Calendar
↓
CalendarCore
↓
Local Event Cache
↓
ScheduleEngine
↓
Notification Instance
├─ PersistentPanel
└─ Voice Pipeline（有効時のみ）
      ↓
   SpeechPlanner
      ↓
   TtsProvider
      ↓
   AudioPlayer

重要:
PersistentPanelはVoice Pipelineを待たず即表示する。

## 5. Google Calendar連携

### 5.1 OAuth
デスクトップアプリ向けOAuthを利用する。

基本:
- システムブラウザでGoogleログイン
- 必要最低限のCalendar権限
- Refresh Tokenを安全に保存
- アプリ内ブラウザへGoogleパスワードを入力させない

### 5.2 必要権限
読み取りのみを基本とする。

予定編集機能を搭載しない限り、
書き込み権限は要求しない。

### 5.3 Calendar List
初回接続時:
- カレンダー一覧取得
- 名前
- ID
- 色
- Primary判定
- 選択状態

をローカル保存する。

## 6. 対象カレンダー

calendar_selection テーブルで管理。

項目:
- calendar_id
- display_name
- enabled
- notification_offset
- voice_enabled
- style_preset

カレンダーごとに音声有無を変えられるようにする。

## 7. イベント同期

### 7.1 同期対象
- 過去少量
- 現在
- 近未来

例:
現在から30日先まで。

### 7.2 差分同期
Google Calendar APIの差分同期機構を利用できる場合は利用する。

### 7.3 ローカルキャッシュ
イベントをSQLiteへ保存し、
API不通時にも直近予定を確認可能にする。

## 8. Eventデータ

EventRecord:
- provider_event_id
- calendar_id
- title
- description
- location
- start_at
- end_at
- timezone
- updated_at
- status
- recurring_event_id
- raw_revision

予定本文をVoice Systemへ送る範囲は設定で制御する。

## 9. 通知スケジュール

NotificationSchedule:
- event_id
- notify_at
- stage
- fired_at
- acknowledged_at
- snoozed_until
- state

state:
- pending
- visible
- acknowledged
- snoozed
- expired

## 10. スリープ・終了対策

常時起動だけを前提にしない。

起動時・スリープ復帰時に:
1. 前回確認時刻を読む
2. 現在時刻までの通知予定を確認
3. 未通知かつ有効な予定をcatch-up
4. 過去すぎる通知は設定に従う

例:
「30分以上過ぎたものは履歴だけ」
「2時間以内なら表示」
など選択可能。

## 11. PersistentPanel

### 11.1 性質
Windows標準Toastとは別の独自ウィンドウ。

### 11.2 要件
- ユーザーが閉じるまで残る
- ウィンドウ再起動後も未確認通知を復元可能
- 複数件表示可能
- 最前面固定ON/OFF
- モニター指定可能
- 画面端配置
- ドラッグ移動可能
- サイズ変更は必要に応じて

### 11.3 カード
1通知 = 1カード。

表示:
- 時刻
- タイトル
- Calendar名
- 説明要点
- 再通知
- Calendarを開く
- 閉じる

## 12. 通知を閉じる

閉じる操作で acknowledged_at を保存する。

パネルから消えても履歴には残る。

「閉じた = 予定を完了した」とは扱わない。

## 13. 再通知

Snooze:
- 5分
- 10分
- 30分
- 1時間
- 任意

Snooze時:
現在カードを閉じる / 残す
を設定可能。

## 14. Voice Systemの独立性

Voice Systemは次のインターフェースだけを見る。

VoiceRequest:
- event
- notification_stage
- current_time
- recent_utterances
- user_preferences

VoiceResult:
- text
- style
- provider_metadata

CalendarCoreへ直接依存しない。

## 15. SpeechPlanner

### 15.1 役割
予定を「喋るための一言」に変換する。

### 15.2 入力
- title
- description（設定で無効化可能）
- location（設定で無効化可能）
- start_at
- current_time
- stage
- recent utterances
- preferred personality
- max speech duration

### 15.3 出力例

{
  "text": "そろそろ歯医者ですよ。診察券を忘れずに。帰りは薬局ですね。",
  "style": "warm, casual, lightly urging",
  "priority": "normal"
}

### 15.4 制約
- 予定内容を捏造しない
- 元情報にない持ち物を勝手に追加しない
- 日時を間違えない
- 5〜10秒を標準
- 短文
- 最近の言い回しを避ける
- 美文を作ろうとしない
- 内容を優先する

## 16. SpeechPlanner Provider

interface SpeechPlannerProvider

候補:
- Gemini Text Model
- OpenAI Text Model
- Local LLM
- RuleBasedPlanner

初期のAI版ではGemini系を第一候補としてよい。

## 17. RuleBasedPlanner

APIなしでも最低限動く。

Template例:
- 「{time}から{title}です。」
- 「そろそろ{title}の時間です。」
- 「{title}ですよ。予定を確認してください。」

ランダム選択で単調さを減らす。

## 18. Recent Utterance履歴

speech_history:
- event_key
- generated_at
- text
- style
- provider
- favorite

同じRecurring Eventの場合、
直近10〜20件をPlannerへ渡す。

目的:
同じ通知文の繰り返し回避。

## 19. お気に入り

ユーザーが音声に
「この言い方好き」
を付けられるようにする将来案。

お気に入り文そのものを繰り返すのではなく、
Plannerへ好みの参考として渡す。

## 20. TtsProvider

interface TtsProvider:
- synthesize(text, style, voiceConfig)
- listVoices()
- healthCheck()

候補実装:
- GeminiTtsProvider
- Chirp3Provider
- OpenAiProvider
- ElevenLabsProvider
- LocalProvider

Providerごとに対応能力が違うため、
capabilitiesを返す。

例:
- natural_language_style
- ssml
- speed
- pitch
- streaming
- japanese
- voice_selection

## 21. Gemini TTS利用

Gemini TTS側が自然言語のstyle指示を受けられる場合、
SpeechPlannerが出したstyleを直接渡す。

例:
text:
「そろそろ歯医者ですよ。」

style:
「親しみやすく、明るい。少しだけ急かすが、怖くしない。」

TTSは文章内容を決めない。
話し方だけを担当する。

## 22. Chirp 3 HD利用

Chirp 3を利用する場合、
対応可能なSSML / voice parametersへstyleを変換するAdapterを置く。

SpeechPlanner側はProvider固有仕様を知らない。

## 23. Provider Capability Adapter

StyleIntent:
- warmth: 0..1
- urgency: 0..1
- playfulness: 0..1
- calmness: 0..1
- speaking_rate
- optional freeform_instruction

Provider Adapterが、
各APIの形式へ変換する。

これにより、
Gemini TTSから別サービスへ変更してもPlannerを書き換えない。

## 24. 音声生成のタイミング

### 案A: 通知時に生成
利点:
- 最新の時刻を使える
- Stageに応じた言い方
- 毎回違う

欠点:
- 数秒遅延する可能性

### 案B: 少し前に先行生成
通知時刻の1〜2分前に生成する。

利点:
- 通知とほぼ同時に再生可能

欠点:
- 予定変更直後への対応

推奨:
通知パネルは時刻通り即表示。
音声だけ数秒遅れて再生してよい。

将来、先行生成も選択肢とする。

## 25. 音声生成失敗

TTS失敗:
→ パネルだけ表示。

SpeechPlanner失敗:
→ RuleBasedPlanner
→ TTS

両方失敗:
→ パネルのみ。

音声失敗をエラーダイアログで邪魔しない。

## 26. API Timeout

Speech Planner:
例 3〜5秒

TTS:
例 5秒

通知用途なので、
長時間リトライしない。

バックグラウンドで1回だけ再試行可能。

## 27. APIキー

BYOKを基本とする。

保存:
Windows Credential Manager。

SQLite / JSON / .envへ平文保存しない。

設定画面では:
- Provider
- API key登録状態
- Test
- Remove

のみ表示。

## 28. Google Calendar OAuth資格情報

Google Calendar OAuthとTTS APIキーは別概念として扱う。

Calendar:
ユーザーのGoogleアカウント認証。

Voice:
ユーザーが選んだAI/TTSサービスのAPI認証。

設計上混ぜない。

## 29. Voice Privacy

送信レベル設定:

### Level 0
音声AIなし。

### Level 1
タイトルのみ外部送信。

### Level 2
タイトル + 時刻。

### Level 3
タイトル + 時刻 + 説明。

### Level 4
ユーザー指定フィールド全部。

初期値は保守的にする。

## 30. 音声キャッシュ

CacheKey:
provider + voice + text + style

ただし通常は毎回違う言い方を生成するため、
キャッシュヒット率は重要視しない。

用途:
- リプレイ
- API障害
- お気に入り
- テスト

古い音声は容量上限で自動削除可能。

## 31. 音声再生

AudioPlayer:
- Windowsデフォルト出力
- 出力デバイス指定
- 音量
- ミュート
- Test voice

他の音楽を強制停止しない。

## 32. Quiet Hours

音声だけ静音時間を設定できる。

例:
23:00〜07:00
→ パネル表示のみ。

重要カレンダーだけQuiet Hours無視も可能。

## 33. Notification Stage音声

Stage 0:
通常通知。

Stage 1:
通知が未確認のまま一定時間経過。

Stage 2:
開始時刻が近い / 過ぎた。

各Stageで:
- 音声ON/OFF
- 間隔
- 最大回数
を設定する。

無限に喋らせない。

## 34. スケジューリング

バックグラウンドSchedulerは、
次の通知時刻を常に把握する。

方式候補:
- アプリ常駐タイマー
- Windows Task Scheduler補助
- 起動時catch-up

最初はトレイ常駐 + catch-upで十分。

## 35. SQLite

### calendars
- calendar_id
- name
- enabled
- settings

### events
- event_id
- calendar_id
- title
- description
- start_at
- end_at
- updated_at
- raw_revision

### notification_instances
- id
- event_id
- notify_at
- stage
- state
- fired_at
- acknowledged_at
- snoozed_until

### speech_history
- id
- event_key
- generated_at
- text
- style
- provider
- voice
- favorite

### app_settings
key/value

## 36. ログ

ローカルログ:
- Calendar sync
- Notification fire
- Panel open/close
- Speech generation timing
- TTS timing
- Provider error

予定本文をデバッグログへ無条件に残さない。

## 37. 初回セットアップ

1. Google Calendar接続
2. 対象カレンダー選択
3. 通知タイミング
4. パネルテスト
5. 完了

音声は後から設定する。

初回セットアップでAPIキーを要求しない。

## 38. Voice Setup

1. 音声機能ON
2. Speech Planner選択
3. TTS Provider選択
4. APIキー
5. 声選択
6. テスト通知
7. 送信範囲確認

## 39. UI構成

### Main
- 次の予定
- 未確認通知
- 今日の通知履歴

### Calendars
- カレンダー選択
- 各種設定

### Voice
- ON/OFF
- Provider
- Voice
- Style
- Quiet Hours
- Test

### History
- 通知履歴
- 発話履歴

### Settings
- 起動
- パネル
- 再通知
- プライバシー

## 40. パネル表示方式

初期:
画面右下付近。

ただしWindowsタスクバーや他アプリとの競合を避け、
ユーザーが位置を選べるようにする。

位置候補:
- 右上
- 右下
- 左上
- 左下
- カスタム

## 41. アニメーション

登場:
短いフェード / スライド。

消去:
ユーザー操作後に短く消える。

通知の存在感を弱めないため、
派手なアニメーションは不要。

## 42. アクセシビリティ

- キーボード操作
- 高コントラスト
- 文字サイズ
- 音声なしでも全機能利用可能
- 色だけで状態を伝えない

## 43. 世界観モード

通常UI:
実用的。

About / Splash:
厨二病全開可。

Optional Theme:
「祭壇モード」

通知文例:
通常:
「18:00から予定があります。」

祭壇:
「刻限が満ちました。18時より予定が始まります。」

AI Plannerへ世界観を渡すことも可能だが、
予定情報の正確さを優先する。

## 44. セキュリティ

- Calendarは読み取り権限優先
- OAuth tokenは安全保存
- API keyはCredential Manager
- 外部APIへ送るデータを最小化
- Provider responseをHTMLとして直接描画しない
- LLM生成文は通知テキストとしてのみ扱う
- LLMにOS操作権限を与えない

Voice Systemは「文章生成と音声生成」だけ。
エージェント機能にしない。

## 45. エラー設計

Calendar API障害:
- ローカルキャッシュで継続
- 状態表示

認証期限:
- 再ログイン案内

AI API障害:
- RuleBasedへ

TTS障害:
- 無音

音声デバイス障害:
- パネルのみ

DB障害:
- ユーザーへ明示
- 通知欠落を隠さない

## 46. テスト項目

### Calendar
- 複数Calendar
- Recurring Event
- Event変更
- Event削除
- Timezone
- All-day
- PC sleep
- PC reboot

### Panel
- 単一通知
- 同時複数通知
- 長いタイトル
- 複数モニター
- Taskbar位置
- DPI

### Voice
- 日本語短文
- 数字
- 時刻
- 固有名詞
- 説明欄
- 同一イベントの繰り返し
- API Timeout
- Offline
- Provider変更

## 47. POC順序

### POC 1
Google Calendar → Windows独自パネル。

### POC 2
パネルを消すまで残す。
再起動で復元。

### POC 3
Google系TTSへ固定文を送り、日本語品質と待ち時間を確認。

### POC 4
Speech Plannerを追加。
予定から5〜10秒の通知文を生成。

### POC 5
同一予定を10回生成し、
言い回しの変化と情報保持を確認。

### POC 6
Planner style → Gemini TTS等の演技指示へ接続。

## 48. 初期リリース候補

配布版の最初の完成ライン:
- Google Calendar接続
- 複数カレンダー選択
- 通知オフセット
- 独自通知パネル
- 消すまで残る
- 再通知
- トレイ常駐
- 履歴
- スリープ復帰対策
- 自動起動

音声機能は別ブランチ / experimentalから追加してもよい。

ただし設計上は最初から差し込み口を用意する。

## 49. 将来案

- カレンダー内容から重要度判断
- 移動時間を加味した通知
- 天候を加味した一言
- 音声で「あと10分」と返す双方向モード
- ローカルLLM Planner
- ローカルTTS
- キャラクター音声プリセット
- 通知人格の学習
- 「お気に入りの言い方」から嗜好抽出

双方向会話が必要になった場合に初めてLive/Realtime系を検討する。

## 50. 非目標

初期版では以下を目的にしない。

- AI秘書全般
- メール管理
- タスク管理
- 音声アシスタント
- Googleカレンダー編集
- 自律エージェント
- 常時会話

「消えないカレンダー通知」から離れない。

---

ALTER GENESISの構造は、

Calendar
→ 時刻を守る
→ パネルを出す
→ 消すまで残る

が核である。

AI音声は、

Calendar
→ 意味を短く読む
→ 毎回少し違う一言を作る
→ その内容に合った声で喋る

という独立した遊びとして上に載せる。

本体の信頼性と、音声の面白さを混ぜないこと。
