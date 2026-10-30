# 通常インストーラー

`AlterGenesis.iss`は、Microsoft Storeを使わず直接配布するALTER GENESIS用のInno Setup定義。MSIX版も同じWPF本体と`eng/Product.props`の製品情報を使い、ここにMSIX固有処理を混ぜない。

## 作る

前提:

- .NET 8 SDK
- Inno Setup 6
- Google Cloudで作成したデスクトップOAuthクライアントJSON

リポジトリ直下で実行:

```powershell
.\tools\Build-Installer.ps1 -GoogleClientFile 'C:\安全な場所\client.json'
```

既定の非公開JSONが`artifacts/oauth-reminder-build/client.json`にある場合は引数を省略できる。JSON、発行本体、インストーラーは`.gitignore`対象で、コミットしない。

出力:

- `artifacts/productization/publish/AlterGenesis.exe`
- `artifacts/productization/installer/ALTER-GENESIS-Setup-<version>-win-x64.exe`
- `artifacts/productization/build-manifest.json`

版番号、製品名、発行元、実行ファイル名は`eng/Product.props`だけで変更する。Innoの`AppId`は更新インストールを同じ製品として扱う恒久IDなので変更しない。

## 現在の約束

- 現在ユーザーだけへ入り、管理者権限を求めない。
- 本体は`%LOCALAPPDATA%\Programs\TINMOON\ALTER GENESIS`へ置く。
- スタートメニュー項目は一つだけ作る。
- インストール時に自動起動を勝手に有効化しない。
- 更新は同じ場所へ上書きし、ユーザー設定とGoogle接続を保持する。
- アンインストール時はアプリが作ったスタートアップショートカットを削除する。設定とGoogle接続は、通常アンインストール時だけ利用者へ削除するか尋ねる。

## 公開前にCodex外で確かめる

1. インストール先、スタートメニュー、初回起動を確認する。
2. Google接続、通知カード、音、再通知、言語切替を確認する。
3. 同じ`AppId`の新しい版を重ね、設定とGoogle接続が残ることを確認する。
4. アンインストールし、本体と専用ショートカットだけが消えることを確認する。
5. 設定保持と設定削除を別々に確認する。
6. 採用アイコンとコード署名を入れ、署名後のハッシュを公開記録へ残す。

CodexはMSIX版のため、AppDataへの書き込みが仮想化される場合がある。実インストール、ショートカット、設定保存先は、じゅんさんがCodex外から実行して確認するまで完了扱いにしない。

## Inno Setupの扱い

公式利用許諾は商用利用を認めている。公式は一定規模を超える商用利用者へ商用ライセンス購入を要請しているため、開発中は試作に使い、販売・本番投入前に最新条件を再確認する。

- https://jrsoftware.org/files/is/license.txt
- https://jrsoftware.org/isorder.php
