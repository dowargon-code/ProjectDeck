# ProjectDeck

Stream Deck のような見た目の、Windows 用の常駐ランチャーです。案件（ページ）ごとに画像付きボタンを並べ、ワンクリックでフォルダ・ファイル・アプリを開きます。
ボタンの割付は `deck.json`（インターフェイスファイル）に書きます。Grok Bot が付属コマンド `deckctl` で書き換えると、本体が変更を検知して即時に反映します。

詳しくは [docs/ProjectDeck_仕様書.md](docs/ProjectDeck_仕様書.md) を参照してください。

## フォルダ構成

```
ProjectDeck/
├── .gitignore               # dist/・build/・bin/・obj/ を除外
├── README.md
├── THIRD_PARTY_NOTICES.md   # 同梱ライブラリのライセンス（配布物にも入る）
├── Directory.Build.props    # 全プロジェクト共通の設定（版数 <Version> はここ）
├── ProjectDeck.slnx         # ソリューション（Visual Studio 2026 で開く）
├── build.ps1                # ビルドの入口：dist\ に配布物を作る
├── config/                  # [Git管理] 配布物に同梱する設定ファイルの原本
│   └── settings.toml
├── docs/                    # [Git管理] 仕様書・Grok用プロンプト・画面モック・引き継ぎ表（保守はこのリポジトリで行う）
├── grokbot/                 # [Git管理] Grok Bot に読み込ませるスキル（SKILL.md。配布物にも入る）
├── src/                     # [Git管理] ソースコード
│   ├── ProjectDeck/         #   ランチャー本体（WinForms）
│   ├── ProjectDeck.Core/    #   共通処理（deck.json/TOML の読込・検証・パッチ適用・履歴）
│   └── deckctl/             #   付属コマンド（Grok のローカル実行から使う）
├── tests/
│   └── ProjectDeck.Core.Tests/  # Core の単体テスト（xUnit）
├── build/                   # [Git管理外] 発行の中間ファイル
└── dist/                    # [Git管理外] build.ps1 が作る
    ├── ProjectDeck/         #   ProjectDeck.exe・deckctl.exe・settings.toml・README.md・THIRD_PARTY_NOTICES.md・grokbot\SKILL.md
    └── ProjectDeck-v<版>.zip  # 上のフォルダの中身
```

実行時のデータ（`deck.json`・`settings.toml`・`history\`・`cache\`・`logs\`・`inbox\`）は、exe の隣ではなく `%USERPROFILE%\ProjectDeck\` に置きます。
Grok などの AI のコマンドで使う deckctl は、ランチャーが起動のたびに `%USERPROFILE%\ProjectDeck\deckctl\` へコピーしたものです（本体の置き場所が変わってもパスが変わらない）。
`settings.toml` が無いときは、exe の隣にある原本（`config\settings.toml` のコピー）を複製して使い始めます。

## 実装状況

| フェーズ | 内容 | 状況 |
|---|---|---|
| 1 | 画面・ページ・アクション実行、deck.json / settings.toml の監視・検証・履歴、ランプ、📌、◎、⚙設定ダイアログ、常駐・ホットキー（表示/非表示）、D&D登録、ボタンのドラッグ移動、画像ファイルのドロップで画像設定、ページの追加・名前・色の変更、自動実行、deckctl | 実装済み |
| 2 | 画像URLの取得・加工（256×256）・キャッシュ（`cache\images\`）、取得失敗時の代替アイコン、右クリック「画像を再取得」、WebP 対応 | 実装済み |
| 3 | ダイヤル（音量・マイク・アプリ別音量・明るさ）。ホイールで調整、Shift で1%刻み、クリックでミュート | 実装済み |

## deckctl の使い方

```powershell
deckctl get                          # ページ一覧とダイヤルの割り付け（dials）
deckctl get --page a-mes             # ページ定義＋_info（行列数・空き位置・次のボタンID）
deckctl apply <パッチ> --dry-run     # 書き込まずに結果だけ確認
deckctl apply <パッチ> [--force]     # 適用（5個以上減る変更は --force が必要）
deckctl validate                     # 現在の deck.json を検証
deckctl undo                         # 1つ前の履歴に戻す
deckctl rename a-mes-001 "仕様書"     # ボタンの表示名だけを変える（画像・動作はそのまま）
deckctl dial set 1 master_volume --step 2          # ダイヤルを1つ割り付け（ほかのダイヤルはそのまま）
deckctl dial set 2 app_volume --process spotify    # アプリ別音量は --process が必須
deckctl dial remove 2                # ダイヤルを1つ外す
deckctl check-image <URL>            # 画像として使えるか確認（形式・大きさ・短辺64px以上）
```

出力はすべて JSON、終了コードは 0：成功／1：検証エラー（deck.json 変更なし）／2：ファイル競合・読み書き失敗。
`--deck <パス>` で対象の deck.json を指定できます（既定は settings.toml の `[interface] path`）。

## 動作確認用のデータフォルダ

環境変数 `PROJECTDECK_HOME` を設定すると、`%USERPROFILE%\ProjectDeck\` の代わりにそのフォルダを使います（本体・deckctl とも）。普段のデータに触れずに試すときに使います。このときは PC起動時の自動実行の登録（レジストリ）に触れず、普段使いのランチャーと同時に起動できます。ただし deck.json の場所は settings.toml の `[interface] path` で決まるため、**そのフォルダに settings.toml を置いて path もそこに向けるか、deckctl に `--deck <パス>` を付けてください**（settings.toml が無いと既定の `%USERPROFILE%\ProjectDeck\deck.json`、つまり普段の deck.json を使います）。

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

## 配布・更新

自作ツールの運用に合わせて、GitHub の Releases に zip を登録し、`C:\Tools\ProjectDeck\` に置いて使います。

### リリースを作る

1. `Directory.Build.props` の `<Version>` を上げて、PR でマージする
2. `main` で `.\build.ps1` を実行し、`dist\ProjectDeck-v<版>.zip` を作る
3. GitHub の Releases に、タグ `v<版>`・タイトル `ProjectDeck <版>` で zip を登録する

### 初めて置くとき

1. 開発用の `dist` から起動しているランチャーがあれば、≡メニュー →「終了」で閉じる
2. `ProjectDeck-v<版>.zip` を `C:\Tools\ProjectDeck\` に展開する
3. `C:\Tools\ProjectDeck\ProjectDeck.exe` を起動する
   - PC起動時の自動実行の登録が、この exe に自動で切り替わる
   - Grok が使う deckctl のコピー（`%USERPROFILE%\ProjectDeck\deckctl\`）も、この版に自動で更新される

### Grok Bot にスキルを読ませる

Grok Bot からランチャーのボタンを編集するには、付属のスキルを Grok Bot に登録します。

1. `C:\Tools\ProjectDeck\grokbot\SKILL.md`（リポジトリでは `grokbot/SKILL.md`）を Grok Bot とのチャットに添付する
2. 「このファイルを読んで、スキルとして登録して」と頼む（登録済みなら「この内容でスキルを置き換えて」）
3. 操作する Windows PC の接続と、`deckctl` の場所を確認してもらう
   - 「ランチャーのページ一覧を見せて」と頼み、ページ名とボタン数が返ってくれば準備完了
   - Grok Bot は `%USERPROFILE%\ProjectDeck\deckctl\deckctl.exe` を使う。ランチャーを一度起動していないとまだ無いので、先に `ProjectDeck.exe` を起動しておく

スキルの要点と、直すときの手順は `docs/Grok用プロンプト.md` の2章にあります。

### 更新するとき

1. ランチャーを ≡メニュー →「終了」で閉じる
2. 新しい zip の中身で `C:\Tools\ProjectDeck\` を上書きする
3. `ProjectDeck.exe` を起動する

ボタンの割付（deck.json）・設定（settings.toml）・履歴などは `%USERPROFILE%\ProjectDeck\` にあるので、上書きしても消えません。
`C:\Tools\ProjectDeck\settings.toml` は初回用の原本で、普段は使われません。
