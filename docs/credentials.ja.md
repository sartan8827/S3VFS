# 認証とクレデンシャル

[← README に戻る](../README.ja.md) · [English](./credentials.md)

S3VFS が AWS のクレデンシャルをどう解決するか。S3VFS の DPAPI 暗号化
クレデンシャルストア、AWS IAM Identity Center (SSO)、AWS CLI 2.32+ の
`aws login` を扱います。

---

## AWS 認証情報の管理

S3VFS は AWS SDK 標準の認証情報チェーン (環境変数 / 共有プロファイル
`~/.aws/credentials` / IAM ロール / IMDS) を利用できるほか、**Windows
Credential Manager 上に独自のユーザー単位の暗号化ストア**を持つことができま
す。シークレットアクセスキー (および任意の STS セッショントークン) は
**DPAPI** の `CurrentUser` スコープで暗号化されたうえで credential blob に
書き込まれるため、**保存したユーザー本人だけが同一マシン上で復号できる**
設計です。

```powershell
# 対話入力で保存 (シークレット入力はマスク表示)
s3vfs credentials set --profile prod

# コマンドラインで全部渡す場合 (対話プロンプトなし)
s3vfs credentials set --profile prod `
  --access-key AKIA... `
  --secret-key ... `
  --session-token ...   # 一時認証情報のときだけ

# 保存済みプロファイルのメタデータ表示 (シークレットは絶対に表示されません)
s3vfs credentials get --profile prod

# S3VFS 管理下のプロファイル一覧
s3vfs credentials list

# 削除
s3vfs credentials remove --profile prod
```

その上でマウント設定にプロファイル名を記述します:

```toml
bucket      = "my-bucket"
root-folder = "C:/Users/you/S3VFS"
aws-profile = "prod"
```

エントリは generic credential として `S3VFS:AWS:<profile>` という target
name で保存され、`LocalMachine` スコープで永続化されます (ログアウト後も
維持)。一方で blob は保存したユーザーの DPAPI 鍵で暗号化されているため、
**別ユーザーや別マシンにエントリをコピーしても復号できません**。S3VFS の
このストアはあくまで「ユーザー単位のローカルキャッシュ」として扱い、AWS
認証情報のバックアップ用途には使わないでください。

#### AWS IAM Identity Center (SSO) でサインイン

AWS IAM Identity Center (旧 AWS SSO) を利用する環境では、AWS CLI 標準の
`aws configure sso` をそのまま使ってください。`~/.aws/config` に
`sso_session` プロファイルが書き込まれ、ベアラトークンは
`~/.aws/sso/cache/` にキャッシュされ、AWS SDK がリクエスト毎にロール
クレデンシャルを自動でリフレッシュします。S3VFS は SDK 共有プロファイル
チェーン経由でこのプロファイルを拾うため、S3VFS 専用の SSO コマンドは
ありません。

1. **SDK 推奨ウィザードを実行** (`sso-session` ブロックとそれを参照する
   プロファイルが書き込まれます。詳細は
   [SDK ドキュメント](https://docs.aws.amazon.com/sdkref/latest/guide/feature-sso-credentials.html#sso-token-config) 参照):
   ```powershell
   aws configure sso --profile prod
   ```
   ウィザードが start URL / region / アカウント / ロールを対話で聞いた
   うえで、以下のような設定を生成します:
   ```ini
   [sso-session my-org]
   sso_start_url = https://my-org.awsapps.com/start
   sso_region    = us-east-1
   sso_registration_scopes = sso:account:access

   [profile prod]
   sso_session   = my-org
   sso_account_id = 123456789012
   sso_role_name  = ReadOnly
   region         = us-east-1
   ```
2. **ベアラトークンが期限切れになったら再認可** (既定で約 8 時間):
   ```powershell
   aws sso login --sso-session my-org
   ```
3. **`s3vfs.toml` から参照**。S3VFS DPAPI ストアに無いプロファイル名は
   SDK 共有プロファイルチェーンへフォールバックして SSO エントリを
   拾います:
   ```toml
   bucket      = "my-bucket"
   root-folder = "C:/Users/you/S3VFS"
   aws-profile = "prod"
   ```

`s3vfs doctor --profile prod` で解決経路 (例: `shared profile 'prod' (sso)`)
を確認できます。SDK チェーンが SSO 経由で配っていることがログから
判別できます。

##### マウント中の自動再認証

SDK が提供する `SSOAWSCredentials` (および `credential_process` /
`AssumeRole` 等の同系統ラッパー) は、リクエスト毎に短期クレデンシャルを
期限切れ前にローテーションします。S3VFS は通常パスでは何もする必要が
ありません。安全網として、SDK のプリエンプトウィンドウをすり抜けて
発生する稀な `ExpiredToken` レスポンス (スリープ復帰や時刻の大きな
ずれなど) は S3VFS が捕捉し、SDK のリフレッシングラッパーに対して
`ClearCredentials()` を呼び出し、リクエストを 1 度だけ再送します。

- **再試行成功**: 新しい有効期限が Information ログに 1 行残るだけで、
  マウントは継続します。
- **再試行失敗** (上流のベアラトークン / リフレッシュトークン自体が
  失効している場合): Windows のバルーン通知 "S3VFS: AWS credentials
  expired" が表示され、`aws sso login` (または `aws login` /
  `s3vfs credentials set`) を再実行して再認証するよう促します。失敗
  リクエストの例外はそのまま呼び出し元に伝播するため、エディタ / シェル
  でも検知できます。

#### `aws login` (AWS CLI 2.32+) でサインイン

IAM Identity Center を使わない環境では、AWS CLI v2.32.0 で追加された
`aws login` (OAuth 2.0 + PKCE フロー) でコンソールサインインを最大 12 時間の
自動更新付き一時クレデンシャルに変換できます。OAuth クライアントとエンド
ポイントは AWS CLI 専用に予約されているため、S3VFS は **AWS 公式推奨の
`credential_process` パターンを介して `~/.aws/config` のプロファイルを SDK
共有プロファイルチェーン経由で参照します**。

1. **AWS CLI v2.32.0 以降をインストール** し、IAM プリンシパルに
   [`SignInLocalDevelopmentAccess`](https://docs.aws.amazon.com/signin/latest/userguide/security-iam-awsmanpol.html)
   マネージドポリシーをアタッチ。
2. **CLI でサインイン**。`login_session` プロファイルが書き込まれ、リフレッシュ
   トークンは `%USERPROFILE%\.aws\login\cache` にキャッシュされます:
   ```powershell
   aws login --profile signin
   ```
3. **`~/.aws/config` に `credential_process` プロファイルを追加**。任意の
   AWS SDK が消費できる形にします (将来的に SDK が `login_session` を
   ネイティブサポートする可能性はありますが、現時点では credential_process
   が確実です):
   ```ini
   [profile signin]
   login_session = arn:aws:iam::123456789012:user/you
   region = us-east-1

   [profile s3vfs-login]
   credential_process = aws configure export-credentials --profile signin --format process
   region = us-east-1
   ```
4. **`s3vfs.toml` から参照**。指定した名前が S3VFS DPAPI ストアにない場合、
   S3VFS は SDK 共有プロファイルチェーンにフォールバックして
   `credential_process` エントリを拾います:
   ```toml
   bucket      = "my-bucket"
   root-folder = "C:/Users/you/S3VFS"
   aws-profile = "s3vfs-login"
   ```

`s3vfs doctor --profile s3vfs-login` は解決経路 (例:
`shared profile 's3vfs-login' (credential_process)`) を表示するため、SDK
デフォルトチェーンではなく共有ファイルから取得できているかを確認できます。

