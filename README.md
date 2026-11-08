# ProjectDeck

Stream Deck のような見た目の、Windows 用の常駐ランチャーです。案件（ページ）ごとに画像付きボタンを並べ、ワンクリックでフォルダ・ファイル・アプリを開きます。
ボタンの割付は `deck.json`（インターフェイスファイル）に書きます。Grok Bot が付属コマンド `deckctl` で書き換えると、本体が変更を検知して即時に反映します。

詳しくは [docs/ProjectDeck_仕様書.md](docs/ProjectDeck_仕様書.md) を参照してください。

## フォルダ構成

```
ProjectDeck/
├── .gitignore               # dist/・build/・bin/・obj/ を除外
├── README.md
├── Directory.Build.props    # 全プロジェクト共通の設定（版数 <Version> はここ）
├── ProjectDeck.slnx         # ソリューション（Visual Studio 2026 で開く）
├── build.ps1                # ビルドの入口：dist\ に配布物を作る
├── config/                  # [Git管理] 配布物に同梱する設定ファイルの原本
│   └── settings.toml
├── docs/                    # [Git管理] 仕様書・Grok用プロンプト・画面モック・引き継ぎ表（保守はこのリポジトリで行う）
├── src/                     # [Git管理] ソースコード
│   ├── ProjectDeck/         #   ランチャー本体（WinForms）
│   ├── ProjectDeck.Core/    #   共通処理（deck.json/TOML の読込・検証・パッチ適用・履歴）
│   └── deckctl/             #   付属コマンド（Grok のローカル実行から使う）
├── tests/
│   └── ProjectDeck.Core.Tests/  # Core の単体テスト（xUnit）
├── build/                   # [Git管理外] 発行の中間ファイル
└── dist/                    # [Git管理外] build.ps1 が作る
    ├── ProjectDeck/         #   ProjectDeck.exe・deckctl.exe・settings.toml・README.md
    └── ProjectDeck-v<版>.zip  # 上のフォルダの中身
```

実行時のデータ（`deck.json`・`settings.toml`・`history\`・`cache\`・`logs\`・`inbox\`）は、exe の隣ではなく `%USERPROFILE%\ProjectDeck\` に置きます。
`settings.toml` が無いときは、exe の隣にある原本（`config\settings.toml` のコピー）を複製して使い始めます。

## 実装状況

| フェーズ | 内容 | 状況 |
|---|---|---|
| 1 | 画面・ページ・アクション実行、deck.json / settings.toml の監視・検証・履歴、ランプ、📌、◎、⚙設定ダイアログ、常駐・ホットキー（表示/非表示）、D&D登録、自動実行、deckctl | 実装済み |
| 2 | 画像URLの取得・キャッシュ（今は url 指定のボタンは関連付けアイコン／文字アイコンで表示） | 未着手 |
| 3 | ダイヤル（音量・マイク・明るさ）。今は割付の表示のみ | 未着手 |

## deckctl の使い方

```powershell
deckctl get                          # ページ一覧
deckctl get --page a-mes             # ページ定義＋_info（行列数・空き位置・次のボタンID）
deckctl apply <パッチ> --dry-run     # 書き込まずに結果だけ確認
deckctl apply <パッチ> [--force]     # 適用（5個以上減る変更は --force が必要）
deckctl validate                     # 現在の deck.json を検証
deckctl undo                         # 1つ前の履歴に戻す
deckctl check-image <URL>            # 画像として取得できるか確認
```

出力はすべて JSON、終了コードは 0：成功／1：検証エラー（deck.json 変更なし）／2：ファイル競合・読み書き失敗。
`--deck <パス>` で対象の deck.json を指定できます（既定は settings.toml の `[interface] path`）。

## 動作確認用のデータフォルダ

環境変数 `PROJECTDECK_HOME` を設定すると、`%USERPROFILE%\ProjectDeck\` の代わりにそのフォルダを使います（本体・deckctl とも）。普段のデータに触れずに試すときに使います。このときは PC起動時の自動実行の登録（レジストリ）に触れず、普段使いのランチャーと同時に起動できます。

```powershell
$env:PROJECTDECK_HOME = "D:\tmp\pdtest"; .\dist\ProjectDeck\ProjectDeck.exe
```

## セットアップ

- .NET 10 SDK（Visual Studio 2026 の「.NET デスクトップ開発」ワークロードに含まれます）
- 開発中は `ProjectDeck.slnx` を Visual Studio で開くか、次のコマンドでビルド・テストします。

```powershell
dotnet build ProjectDeck.slnx
dotnet test ProjectDeck.slnx
```

## ビルド

```powershell
.\build.ps1
```

テストを実行してから、`dist\ProjectDeck\` と `dist\ProjectDeck-v<版>.zip` を作ります。
発行形式はフレームワーク依存（win-x64、ReadyToRun）です。実行する PC には .NET 10 デスクトップランタイムが必要です。
版数を上げるときは `Directory.Build.props` の `<Version>` を書き換えます。
