# ALTER GENESISのアイコン

2026-11-11採用。通知カードの太い色帯とダークな面に、丸い「！」を置く。
採用案A v2と同一の原本は`app-icon.svg`。文字フォントを使わない図形SVG。

| 役割 | 色 |
| --- | --- |
| 通知面 | `#303033` |
| 太い帯 | `#71859B` |
| マーク | `#DBDBDB` |

アプリの`app`フォルダーで、Tauri公式の変換コマンドを実行する。

```powershell
npm run tauri -- icon src-tauri/icons/app-icon.svg --output ../artifacts/icon-generation/generated
```

生成したルート直下のPNG/ICO/ICNSをこのフォルダーへコピーする。iOS/Androidのサブフォルダーは今回のWindows製品へ取り込まない。

- Windows本体と設定窓: `tauri.conf.json`の`bundle.icon`から埋め込む。
- トレイ: 既存の`default_window_icon()`を使い、本体と同じマークにする。
- NSISのSetup/アンインストーラー: `installerIcon`/`uninstallerIcon`で同じ`icon.ico`を指定する。
- Store向けPNGも同じ原本から生成する。MSIX化・Microsoft Storeへの提出は別の作業。

透明な余白と外周の角丸を保ち、横へ伸ばしたり色帯を細くしたりしない。
小サイズを含む最終表示は、更新後のWindows本体・トレイ・Setupで利用者が確認する。
