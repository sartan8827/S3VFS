# 認証とクレデンシャル

[← README に戻る](../README.ja.md) · [English](./credentials.md)

S3VFS が AWS のクレデンシャルをどう解決するか。アクセスキーのプロファイル、
AWS IAM Identity Center (SSO)、AWS CLI 2.32+ の `aws login` を扱います。

---

## AWS 認証情報の管理

S3VFS はAWS SDK 標準のクレデンシャル解決をそのまま使います。

- **`aws-profile` を省略した場合**: SDK のデフォルトチェーン (環境変数、
  `~/.aws/credentials` / `~/.aws/config` の `default` プロファイル、IAM ロール、
  IMDS) を使います。
- **`aws-profile` を指定した場合**: 共有 AWS ファイル (`~/.aws/config`、
  `~/.aws/credentials`) から指定したプロファイルを読み込みます。プロファイルが
  存在しない場合は、どのマウントかを示すエラーで起動を中止します。

アクセスキーをそのまま使う場合は AWS CLI でプロファイルを作成します。

```powershell
aws configure --profile prod
```

作成したプロファイルをマウント設定から参照します。

```toml
bucket      = "my-bucket"
root-folder = "C:/Users/you/S3VFS"
aws-profile = "prod"
```

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
3. **`s3vfs.toml` から参照**。プロファイル名を指定すると、SDK 共有
   プロファイルチェーンから SSO エントリを拾います:
   ```toml
   bucket      = "my-bucket"
   root-folder = "C:/Users/you/S3VFS"
   aws-profile = "prod"
   ```

`s3vfs mount` は起動時に解決経路 (例: `using AWS credentials from shared profile 'prod' (sso)`)
をログに出力します。SDK チェーンが SSO 経由で配っていることがログから
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
  expired" が表示され、`aws sso login` (または `aws login`) を
  再実行して再認証するよう促します。失敗
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
4. **`s3vfs.toml` から参照**。他のプロファイルと同様に名前を指定すると、
   S3VFS は SDK 共有プロファイルチェーンから `credential_process`
   エントリを拾います:
   ```toml
   bucket      = "my-bucket"
   root-folder = "C:/Users/you/S3VFS"
   aws-profile = "s3vfs-login"
   ```

`s3vfs mount` は起動時に解決経路 (例:
`shared profile 's3vfs-login' (credential_process)`) をログに出力するため、SDK
デフォルトチェーンではなく共有ファイルから取得できているかを確認できます。

