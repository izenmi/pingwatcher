# networktoys — エージェント向けの入口（この Windows 機での注意）

方針・設計は CLAUDE.md。ただし **CLAUDE.md の「開発コンテナは Linux で dotnet が無く
exe を実行できない」はこの機（izenmi のデスクトップ）では当てはまらない。**
2026-08-23 にローカル一式を整えてあり、ビルド・1200件超の xUnit・`--selftest`・
CI と同じ2構成の publish まで全部ローカルで回せる。

## ローカル環境

- **.NET 10 SDK 10.0.400 が `C:\Users\izenmi\.dotnet` にユーザーローカル導入済み**
  （システム PATH には無い）。bash なら
  `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`、
  PowerShell なら `$env:DOTNET_ROOT="$env:USERPROFILE\.dotnet"`。
- **NuGet のユーザー設定はパッケージソースが空。**設定は書き換えず、restore/publish に
  `-p:RestoreSources=https://api.nuget.org/v3/index.json`（restore は `-s`）を毎回渡す。
- 自己診断（`--selftest`）は exit code だけでなく **crash.log の有無**まで見ること。
- **gh CLI は `C:\Program Files\GitHub CLI\gh.exe`**（PATH に無い。フルパスで呼ぶ。
  ログイン済み izenmi）。**リポジトリのディレクトリで実行**しないと "not a git repository"。
- git の作者情報はリポジトリローカルに `izenmi <izenmi@users.noreply.github.com>` 設定済み。
- `tools/icon/build_icon.sh` の実行権限差分（755→644）が常に出るが、
  こちらの変更ではないので**コミットに含めない**。

## マージの流儀（ユーザーとの取り決め・2026-08-23）

PR を作ったら CI（build ワークフロー）を監視し、**緑になったら確認を待たずに
squash マージしてブランチを削除する**ところまで自動で行う。

- 手順: `gh pr create --base main --fill` → 本文に署名追記 → `gh run watch` で監視 →
  success なら `gh pr merge <n> --squash --delete-branch` → ローカル main を pull。
- squash を使う（merge コミットの無い直線履歴を保つ）。
- CI が落ちたら当然マージせず、直してから再走。
- 例外: 履歴の書き換え・force push・リリースタグ付けなど影響の大きい操作は従来どおり確認。
