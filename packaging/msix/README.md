# Microsoft Store / MSIX 方針

通常インストーラーと別のアプリは作らない。`src/AlterGenesis.NotificationProbe`の同じWPF本体を`win-x64`へ発行し、`eng/Product.props`の製品名・実行ファイル名・版番号と、採用後の共通アイコン資産をMSIXへ渡す。

## 固定する前に必要な外部情報

- Microsoft Partner Centerで予約した製品名とPackage Identity Name
- Storeが指定するPublisher ID
- 採用されたアイコンから作るStore用PNG一式
- 対応Windows版と配布対象アーキテクチャ

これらを仮の公開値で埋めて追跡しない。ローカル検証用MSIXでは`.Dev`の別Identityとテスト証明書を使い、Store提出物と混ぜない。

## 通常版と異なる点

- 自動起動はスタートアップフォルダーの`.lnk`ではなく、パッケージのStartupTaskで登録する。
- Storeがインストール、更新、アンインストールを管理するため、Inno Setupの処理をMSIX内へ入れない。
- パッケージ化による保存場所の違いを確認し、通常版からStore版へ移るときの設定・Google接続の引継ぎを別の隔離試験で決める。

## 次の実装単位

1. アイコン案を一つ選び、ICOとStore用PNGへ展開する。
2. Partner Centerの製品名予約後、正式Identityを手元の非公開設定から注入できるマニフェストを作る。
3. `.Dev` Identityの署名済みMSIXをCodex外でインストールし、起動、Googleログイン、通知、更新、アンインストールを通常版と分けて確認する。

参考:

- https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/dotnet/package-app
- https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/choose-distribution-path
- https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msi/app-package-requirements
