# Authentication and credentials

[← Back to README](../README.md) · [日本語版](./credentials.ja.md)

How S3VFS resolves AWS credentials. Covers access-key profiles, AWS IAM
Identity Center (SSO), and AWS CLI 2.32+ `aws login`.

---

## Managing AWS credentials

S3VFS relies on the standard AWS SDK credential resolution.

- **`aws-profile` omitted**: the SDK default chain is used (environment
  variables, the `default` profile in `~/.aws/credentials` /
  `~/.aws/config`, IAM role, IMDS).
- **`aws-profile` set**: the named profile is loaded from the shared AWS
  files (`~/.aws/config`, `~/.aws/credentials`). Start-up fails with an
  error naming the mount when the profile does not exist.

For plain access keys, create the profile with the AWS CLI:

```powershell
aws configure --profile prod
```

Then reference the profile in your mount config:

```toml
bucket      = "my-bucket"
root-folder = "C:/Users/you/S3VFS"
aws-profile = "prod"
```

#### Sign in via AWS IAM Identity Center (SSO)

For environments that use AWS IAM Identity Center (formerly AWS SSO), use
the AWS CLI's built-in `aws configure sso` flow — it writes an
`sso_session` profile to `~/.aws/config`, caches the bearer token under
`~/.aws/sso/cache/`, and the AWS SDK auto-refreshes the role credentials
on every request. S3VFS picks the profile up through the SDK shared-profile
chain, so there is no S3VFS-specific SSO command to learn.

1. **Run the SDK's wizard** (writes an `sso-session` block + a profile
   referencing it; see
   [SDK docs](https://docs.aws.amazon.com/sdkref/latest/guide/feature-sso-credentials.html#sso-token-config)):
   ```powershell
   aws configure sso --profile prod
   ```
   The wizard prompts for the start URL, region, account, and role and
   produces something like:
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
2. **(Re-)authorize the bearer token** any time it expires (~8 h by default):
   ```powershell
   aws sso login --sso-session my-org
   ```
3. **Reference the profile from `s3vfs.toml`** exactly like any other
   profile name. S3VFS resolves it through the shared profile chain and
   picks up the SSO entry:
   ```toml
   bucket      = "my-bucket"
   root-folder = "C:/Users/you/S3VFS"
   aws-profile = "prod"
   ```

At start-up `s3vfs mount` logs the resolution path
(e.g. `using AWS credentials from shared profile 'prod' (sso)`) so you can
confirm the SDK chain is serving the credentials.

##### Automatic refresh while mounted

The SDK's `SSOAWSCredentials` (and the matching wrappers for
`credential_process`, `AssumeRole`, …) all roll their short-term
credentials over before expiry on every signed request — S3VFS does not
need to do anything for the happy path. As an additional safety net,
S3VFS catches the rare on-the-wire `ExpiredToken` response that slips
past the SDK's preempt window (machine sleep / resume, large clock skew),
calls `ClearCredentials()` on the SDK's refreshing wrapper, and retries
the request once.

- **Retry succeeds**: a single Information log line records the new
  expiration; the mount keeps running.
- **Retry fails** (the upstream bearer / refresh token has itself
  expired): a Windows balloon-tip notification "S3VFS: AWS credentials
  expired" tells the operator to re-run `aws sso login` (or
  `aws login`) to re-authenticate, and the
  failed request's exception propagates back to the caller.

#### Sign in via `aws login` (AWS CLI 2.32+)

For environments **not** using IAM Identity Center, AWS CLI v2.32.0 introduced
`aws login` — an OAuth 2.0 + PKCE flow that converts your AWS Management Console
sign-in into auto-refreshing temporary credentials (up to 12 hours). The OAuth
client and endpoints are reserved for the AWS CLI itself, so S3VFS integrates by
**referencing the resulting `~/.aws/config` profile through the SDK
shared-profile chain** (the AWS-recommended `credential_process` pattern).

1. **Install AWS CLI v2.32.0 or later** and attach the
   [`SignInLocalDevelopmentAccess`](https://docs.aws.amazon.com/signin/latest/userguide/security-iam-awsmanpol.html)
   managed policy to your IAM principal.
2. **Sign in** with the CLI; this writes a `login_session` profile and caches
   the refresh token under `%USERPROFILE%\.aws\login\cache`:
   ```powershell
   aws login --profile signin
   ```
3. **Wire it into `~/.aws/config`** as a `credential_process` profile so any
   AWS SDK can consume it (newer SDKs may eventually support `login_session`
   natively, but `credential_process` works today):
   ```ini
   [profile signin]
   login_session = arn:aws:iam::123456789012:user/you
   region = us-east-1

   [profile s3vfs-login]
   credential_process = aws configure export-credentials --profile signin --format process
   region = us-east-1
   ```
4. **Reference it from `s3vfs.toml`** like any other profile — S3VFS resolves
   the name through the SDK shared-profile chain and picks up the
   `credential_process` entry:
   ```toml
   bucket      = "my-bucket"
   root-folder = "C:/Users/you/S3VFS"
   aws-profile = "s3vfs-login"
   ```

At start-up `s3vfs mount` logs the resolution path so you can
confirm the credentials came from the shared file (e.g.
`shared profile 's3vfs-login' (credential_process)`) rather than the SDK
default chain.

