# ALTER GENESIS — Tauri比較用試作

> [!IMPORTANT]
> このフォルダは技術選定前に作った隔離モックであり、採用済みの本体ではない。
> 現在の実装本命は WPF + C#。この試作は比較材料として保持する。

Googleカレンダーの選んだ予定を、ユーザーが確認して閉じるまで残る独自パネルで知らせるWindowsアプリ。

## 試作した範囲

- Tauri 2 + Svelte + TypeScript
- 初回設定の操作順モック
- 実アカウントに接続しない隔離カレンダーデータ
- 別ウィンドウで開く、自動で消えないテスト通知

## コマンド

```powershell
npm run check
npm run build
npm run tauri dev
```

Google OAuthと実カレンダーには接続していない。
