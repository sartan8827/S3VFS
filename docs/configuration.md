# Configuration

[← Back to README](../README.md) · [日本語版](./configuration.ja.md)

This page covers S3VFS prerequisites, how to start a mount, the small CLI
surface, the full TOML configuration reference (including the
multi-mount layout), and the retry policy. For AWS credential resolution see
[Authentication and credentials](./credentials.md).

---

## Prerequisites

- Windows 10 1809 (build 17763) or later, or Windows 11
- The Windows optional feature **`Client-ProjFS`** must be enabled
- AWS credentials reachable via the standard AWS SDK chain (environment
  variables, shared profile, IAM role, etc.) — see
  [Authentication and credentials](./credentials.md)
- An S3 bucket you have read/write access to
- **Bucket versioning must be Enabled** on the target bucket. `s3vfs`
  refuses to start otherwise — see [Why versioning matters](#why-versioning-matters)
  for the rationale and the `allow-unversioned` escape hatch. The
  credentials must also allow `s3:GetBucketVersioning`.

Enable versioning once with the AWS CLI:

```powershell
aws s3api put-bucket-versioning `
  --bucket my-bucket `
  --versioning-configuration Status=Enabled
```

#### Why versioning matters

Local file edits and deletes inside the virtualization root propagate to S3
as overwrite `PutObject` and `DeleteObject` calls. Without bucket versioning
those operations are **destructive and irreversible**: a deleted object is
gone, an overwrite leaves no prior copy. Versioning turns each of those
calls into a new version + delete-marker pair, so a misclick in Explorer or
a runaway script remains recoverable.

If `s3vfs` detects that the configured bucket has versioning disabled (or
suspended) it refuses to start with a copy-pasteable `aws s3api
put-bucket-versioning` command, the bucket name, and a link back to this
section.

For CI runs or disposable buckets where the bucket is recreated per-job
and the recoverability story does not apply, set `allow-unversioned = true`
in `s3vfs.toml` to bypass the safety check.

Enable ProjFS once, in an elevated PowerShell session:

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS -All
```

## Run

S3VFS reads every per-mount setting from a TOML configuration file (see
[Configuration file](#configuration-file) for the full key reference and the
multi-mount layout). The shortest possible config is:

```toml
# ./s3vfs.toml
bucket      = "my-bucket"
root-folder = "C:/Users/you/S3VFS"
```

With that file in the current directory (or at
`%APPDATA%\S3VFS\config.toml`):

```powershell
s3vfs help                       # list the supported commands and options (same as bare `s3vfs`)
s3vfs mount                      # start every configured mount
s3vfs mount --name personal      # start one mount by name
```

Open the configured root folder in Explorer and the bucket contents appear.

## Command-line surface

S3VFS is intentionally configuration-driven: every per-mount setting
(`bucket`, `root-folder`, `region`, `aws-profile`,
`retry-max-attempts`, …) lives only in `s3vfs.toml`. The command line
exposes just three things:

| Surface | Purpose |
| --- | --- |
| Sub-commands (`help`, `mount`, `lost-and-found`) | Show the supported commands and options (also printed by a bare `s3vfs`), pick which mount(s) to start, or recover quarantined files. |
| `--name <mount>` | Selects a single entry from the `[[mount]]` array on `s3vfs mount`; when omitted, every entry is started. |
| `--verbose`, `--log-format` | Process-level overrides for one-off debugging. The TOML keys (`verbose`, `log-format`) are still honoured; the CLI flags simply win when both are present. |

To project only a sub-tree of a bucket — for example
`s3://my-bucket/team-a/` — set `prefix = "team-a/"` in the mount entry.
The virtualization root then mirrors that prefix as its own logical root:
listings, hydration, writes, deletes, and renames all stay scoped to objects
under the prefix, and the rest of the bucket is invisible.


## Configuration file

Mount settings live exclusively in a TOML configuration file. Up to three
sources are merged in **increasing-priority order** — later sources override
earlier ones on a per-key basis:

1. **`s3vfs.toml` next to `s3vfs.exe`** (lowest priority — acts as the
   packaged baseline). Resolved via `AppContext.BaseDirectory`, so the lookup
   is independent of the current working directory.
2. **`%APPDATA%\S3VFS\config.toml`** (per-user, machine-global). Operators
   typically keep per-user settings (`aws-profile`, log preferences) here.
3. **`--config <path>`** (highest priority). Useful when an operator wants to
   pick between several profile files without editing the standard locations.
   Unlike sources #1 and #2, a missing path here is a hard error rather than
   a silent skip.

Process-level CLI flags (`--verbose`, `--log-format`) override the merged
config file values when supplied.

```toml
# ./s3vfs.toml or %APPDATA%\S3VFS\config.toml
bucket               = "my-bucket"
root-folder          = "C:/Users/you/S3VFS"
endpoint-url         = "http://localhost:4566"   # optional
region               = "ap-northeast-1"          # optional
prefix               = "team-a/"                 # optional
aws-profile          = "prod"                    # optional
retry-max-attempts   = 3                         # optional, 1 disables retries
log-format           = "text"                    # optional, "text" or "json"
allow-unversioned    = false                     # DANGER: skip the bucket-versioning safety check
verbose              = false
sync-interval-seconds = 30
change-source        = "polling"                 # "polling" | "events"
sync-mode            = "on-demand"               # "on-demand" | "full" — only used by polling
event-queue          = ""                        # SQS URL/name, required for events
```

A ready-to-edit sample is shipped as
[`s3vfs.toml.example`](./s3vfs.toml.example) at the repo root and is also
copied next to `s3vfs.exe` on `dotnet publish`, so you can rename it to
`s3vfs.toml` (or `%APPDATA%\S3VFS\config.toml`) and uncomment the keys you
need.

Both kebab-case (`root-folder`) and snake_case (`root_folder`) keys are
accepted; kebab is preferred. With a config file in place, a typical mount
is just:

```powershell
s3vfs mount                 # all options sourced from s3vfs.toml
```

#### Multiple mounts in a single config

A configuration file can declare more than one mount under the `[[mount]]`
table-array syntax, each with its own bucket / root-folder / region / etc.
The process-level keys (`verbose`, `log-format`) stay at the top level and
apply to every mount:

```toml
# ./s3vfs.toml — multiple mounts
verbose   = false
log-format = "json"

[[mount]]
name        = "personal"
bucket      = "my-personal"
root-folder = "C:/Users/you/S3VFS-personal"

[[mount]]
name        = "work"
bucket      = "my-work"
root-folder = "C:/Users/you/S3VFS-work"
prefix      = "team-a/"
aws-profile = "prod-readonly"
```

Each `[[mount]]` entry takes the same per-mount keys as the legacy single-
mount form (everything except `verbose` and `log-format`). The `name` is
required to be unique within the file; entries without an explicit `name`
are tagged `mount[0]`, `mount[1]`, etc. Mixing top-level mount keys with
`[[mount]]` entries in the same file is rejected so the precedence stays
unambiguous — pick one form per file.

When a file declares 2+ mounts, `s3vfs mount` starts all of them unless
`--name` narrows it down to one:

```powershell
s3vfs mount                     # start every [[mount]] in this process
s3vfs mount --name personal     # start a single named mount
s3vfs mount --name work         # start a different mount from the same config
```

Each mount runs its own `ProjFsProvider`; logs from a given mount land in
the `S3VFS.Mount.<name>` category so text / JSON formatters surface which
mount each line came from. Pressing Enter on the host process disposes
every mount in reverse start order.

## Retry policy

Transient object-store failures are retried by the AWS SDK pipeline. S3VFS
configures the client with `RetryMode.Adaptive` (the SDK's adaptive
client-side throttling, which combines the standard exponential backoff with
a token bucket that suppresses request bursts when the service signals
overload) and `MaxErrorRetry = retry-max-attempts − 1`. The SDK's built-in
retry classifier decides which failures are eligible:

| Failure | Retried? | Notes |
| --- | --- | --- |
| HTTP 5xx (`500`, `502`, `503`, `504`, …) | Yes | Server-side / load-balancer errors. Treated as transient by the SDK. |
| HTTP 408 `Request Timeout` | Yes | Server-side timeout; the SDK retries with backoff. |
| `Throttling` / `ThrottlingException` / `RequestThrottled*` / `TooManyRequestsException` / `ProvisionedThroughputExceededException` / `RequestLimitExceeded` / `SlowDown` | Yes | AWS throttling family. Adaptive mode also slows the next request via the token bucket. |
| `RequestTimeout` / network errors / connection resets | Yes | Local socket / connection errors. |
| HTTP 4xx other than 408 (`400`, `401`, `403`, `404`, `409`, `412`, …) | No | Caller-side errors (bad request, missing object, permissions). Surfaced immediately. |
| `OperationCanceledException` / `TaskCanceledException` | No | Cancellation propagates without retry. |

The schedule is owned by the SDK: it uses exponential backoff with jitter
inside `MaxErrorRetry` retries. When `retry-max-attempts` is `1` the SDK
performs zero retries (the first attempt is the only one). The SDK's
`TransferUtility` retries individual multipart parts on its own — under
`retry-max-attempts = 3` a single failing part can be re-uploaded up to
three times without restarting the whole multi-GiB upload.

```toml
bucket             = "my-bucket"
root-folder        = "C:/Users/you/S3VFS"
retry-max-attempts = 5         # 5 total attempts (1 initial + 4 retries)
```
