# 可観測性

[← README に戻る](../README.ja.md) · [English](./observability.md)

構造化ログとユーザー定義メタデータの往復保持。

---

## 構造化ログ

既定では `s3vfs` は人間可読な 1 行形式のテキストログをコンソールに出力
します。[`s3vfs.toml`](#設定ファイル) で `log-format = "json"` を指定する
(あるいは一時的に `--log-format json` で起動する) と、Datadog / Loki などの
ログ集約基盤が正規表現なしでパースできる構造化ストリームに切り替わります。

```powershell
s3vfs --log-format json    # ファイル設定があっても CLI が一時的に上書きする
```

各ログエントリは 1 つの JSON オブジェクトを 1 行として書き出します
(プラットフォームの改行コードで終端)。フィールド名は
`Microsoft.Extensions.Logging.Console` の JSON フォーマッタが出力する
キーに準拠します:

| フィールド | 説明 |
| --- | --- |
| `Timestamp` | UTC タイムスタンプ。`yyyy-MM-ddTHH:mm:ss.fffZ` 形式 |
| `EventId` | ログエントリの `EventId.Id` (未指定時は `0`) |
| `LogLevel` | `Trace` / `Debug` / `Information` / `Warning` / `Error` / `Critical` |
| `Category` | ロガーのカテゴリ。通常はソース型のフルネーム (`S3VFS`、`S3VFS.ProjFs.ProjFsProvider` など) |
| `Message` | プレースホルダー置換後の最終的なメッセージ文字列 |
| `State` | 元のメッセージテンプレートと、`{Bucket}` などの名前付きプレースホルダーをそれぞれ独立したプロパティとして保持するオブジェクト。下流で構造化フィルタリングに使える |
| `Exception` | 例外が添付されている場合のみ存在。フォーマット済みの例外文字列 |

サンプル (可読性のため整形しているが、実際は 1 行で出力される):

```json
{
  "Timestamp": "2026-05-09T11:22:33.456Z",
  "EventId": 0,
  "LogLevel": "Information",
  "Category": "S3VFS",
  "Message": "Virtualizing s3://my-bucket at C:\\Users\\you\\S3VFS",
  "State": {
    "Message": "Virtualizing s3://my-bucket at C:\\Users\\you\\S3VFS",
    "Bucket": "my-bucket",
    "Root": "C:\\Users\\you\\S3VFS",
    "{OriginalFormat}": "Virtualizing s3://{Bucket} at {Root}"
  }
}
```

## ユーザー定義オブジェクトメタデータの往復保持

S3 のオブジェクトはユーザー定義ヘッダー (`x-amz-meta-*`) を任意個持てます。
タグや作者名、アプリ固有のマーカーなどがその例です。S3VFS はこれらの
ヘッダーを hydrate と再アップロードを跨いで保持し、ローカル編集によって
失われないようにします。

プレースホルダー作成時、S3VFS は `HeadObject` でバケット側のユーザー
メタデータを取得し、各エントリを `:s3vfs-user-meta` という Windows NTFS の
**代替データストリーム (ADS)** にミラーします。フォーマットは UTF-8 で
1 行あたり `key=value` のプレーンテキストです。キーは S3 の wire と同じく
小文字に正規化されます。

ローカル編集をアップロードする際、S3VFS は同じ ADS を読み戻し、
`PutObject` (またはマルチパート) リクエストに `x-amz-meta-*` として
そのまま添付します。これによりローカル編集サイクル前後でヘッダーが
ビット単位で保たれます。新規作成のローカルファイルにはストリームが
存在しないため、従来どおりユーザーメタデータなしでアップロードされます。

ミラーされたメタデータは PowerShell から確認できます。

```powershell
# プレースホルダーに付与されたストリームを一覧
Get-Item C:\Users\you\S3VFS\meta\file.txt -Stream *

# メタデータを表示 (UTF-8、1 行 1 key=value)
Get-Content C:\Users\you\S3VFS\meta\file.txt -Stream s3vfs-user-meta
```

AWS は `x-amz-meta-*` の合計サイズを **1 オブジェクトあたり 2 KiB** に
制限しています。S3VFS は同じ上限でアップロード前に検証を行い、ネットワーク
往復を待たず即座にエラーを返します。S3 が不透明な 400 を返すのを待つ
必要はありません。

