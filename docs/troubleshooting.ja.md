# トラブルシューティング

[← README に戻る](../README.ja.md) · [English](./troubleshooting.md)

起動時エラーの読み方と `s3vfs lost-and-found` 退避ファイル復元 CLI の
使い方。

---

## 起動時のエラー

マウントが起動できない場合、`s3vfs mount` は原因をログに出力して終了
コード **2** で終了します。例外の詳細まで見たいときは `--verbose` を
付けてください。主なメッセージは次のとおりです。

| ログメッセージ | 原因と対処 |
| --- | --- |
| `Windows ProjFS is not enabled …` / `… is disabled …` / `… only partially installed …` | Windows のオプション機能 `Client-ProjFS` が未導入または無効です。メッセージ中の `Enable-WindowsOptionalFeature` コマンドを管理者権限の PowerShell で実行してください。 |
| `StartVirtualizing failed: <HRESULT>` | PrjFlt サービスが停止しているか、アンチウイルス / EDR にブロックされています。`sc query PrjFlt` とイベントビューアー → システムの PrjFlt エントリを確認してください。 |
| `Mount '<name>': failed to initialize virtualization root …` | `root-folder` を作成できないか、ProjFS ルートとしてマークできません (権限不足、不正なパス、別プロバイダーが使用中のフォルダーなど)。 |
| `Failed to read bucket versioning state for <bucket>` | 最初の S3 呼び出しが失敗しています。添付の例外に理由が出ます: 認証情報がない / 期限切れ、`AccessDenied` (`s3:GetBucketVersioning` 権限不足)、`region` / `endpoint-url` の誤り、バケットが存在しない など。 |
| バケットバージョニングのバナー | バージョニングが Suspended または未設定です。メッセージ中の `aws s3api put-bucket-versioning` コマンドを実行してください。 |

起動時には各マウントが `Mount '<name>': using AWS credentials from <source>`
をログに出すので、どのプロファイル / SDK チェーンから認証情報を取得したか
を確認できます。

`s3vfs mount` で複数マウントを起動していていずれかが失敗した場合、起動
済みのマウントも停止したうえで終了コード 2 で終了します。

## 隔離された退避ファイルの確認・復元 (`lost-and-found`)

リモート側の変更が同期されていないローカル編集と衝突したとき、ウォッ
チャーは「リモート側が真」というポリシーに従ってローカルファイルを上書
きしますが、上書き直前にダーティなローカルコピーをマウント直下の
`.s3vfs-lost+found` ディレクトリへ退避します。`s3vfs lost-and-found`
サブコマンドを使うと、シェルだけでこれらの退避ファイルを確認し復元
できます。

```powershell
# 退避ファイル一覧 (新しい順)。元のパスとサイズも表示されます
s3vfs lost-and-found list

# s3vfs.toml に複数マウントがある場合は --name で 1 つを選択
s3vfs lost-and-found list --name docs

# 退避ファイルと現在のリモートオブジェクトを diff
# テキスト: 外部 `git diff --no-index --color`
# バイナリ (先頭 8 KiB に NUL バイトあり): SHA-256 とサイズの比較
s3vfs lost-and-found diff 20260510T123456789Z_docs%2Fnotes.md

# 退避ファイルを任意の場所にコピー
# (--target を省略するとカレントディレクトリへ元ファイル名で保存)
s3vfs lost-and-found restore 20260510T123456789Z_docs%2Fnotes.md `
  --target C:\Users\you\Desktop\notes-recovered.md
```

`list` の 1 列目 (`FILENAME`) が `diff` / `restore` で渡す識別子です。
そのままコピー＆ペーストしてください。ファイル名は `<UTC タイムスタン
プ>_<URL エスケープ済みの元パス>` 形式なので、`list` は併せて復号後の
`ORIGINAL-PATH` も表示します。`restore` は既存ファイルを上書きしないた
め、強制上書きしたい場合は `--force` を追加してください。`diff` はテ
キスト比較に `git` を使用するため、`PATH` に `git` が無い場合はバイナ
リ用のサマリ表示にフォールバックします。

