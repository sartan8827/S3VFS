# Troubleshooting

[← Back to README](../README.md) · [日本語版](./troubleshooting.ja.md)

How to read start-up errors and the `s3vfs lost-and-found` recovery CLI.

---

## Start-up errors

When a mount refuses to start, `s3vfs mount` logs the cause and exits with
code **2**. Add `--verbose` for the full exception detail. Common messages:

| Log message | Cause and fix |
| --- | --- |
| `Windows ProjFS is not enabled …` / `… is disabled …` / `… only partially installed …` | The `Client-ProjFS` optional feature is missing or disabled. Run the `Enable-WindowsOptionalFeature` command printed in the message from an elevated PowerShell. |
| `StartVirtualizing failed: <HRESULT>` | The PrjFlt service is stopped or blocked by antivirus / EDR. Check `sc query PrjFlt` and Event Viewer → System for PrjFlt entries. |
| `Mount '<name>': failed to initialize virtualization root …` | The `root-folder` could not be created or marked as a ProjFS root (permissions, invalid path, or a folder already owned by another provider). |
| `Failed to read bucket versioning state for <bucket>` | The first S3 call failed. The attached exception says why: missing / expired credentials, `AccessDenied` (the principal lacks `s3:GetBucketVersioning`), a wrong `region` / `endpoint-url`, or a bucket that does not exist. |
| Bucket versioning banner | Versioning is suspended or was never enabled. Run the `aws s3api put-bucket-versioning` command printed in the message. |

Each mount also logs `Mount '<name>': using AWS credentials from <source>`
at start-up, so you can confirm which profile or SDK chain entry supplied
the credentials.

When `s3vfs mount` starts several mounts and one of them fails, the mounts
that already started are shut down and the process exits with code 2.

## Recovering quarantined files (`lost-and-found`)

When a remote change collides with an unsynced local edit, the watcher
copies the dirty local file into the mount's `.s3vfs-lost+found`
directory before overwriting it with the remote (authoritative) version.
The `s3vfs lost-and-found` sub-command lets you inspect and recover
those copies without leaving the shell:

```powershell
# Show every quarantined file (newest first), with the original path and size
s3vfs lost-and-found list

# When s3vfs.toml defines several mounts, pick one by --name
s3vfs lost-and-found list --name docs

# Diff a quarantined copy against the current remote object.
# Text files: external `git diff --no-index --color`.
# Binary files (NUL byte in the first 8 KiB): SHA-256 + size summary.
s3vfs lost-and-found diff 20260510T123456789Z_docs%2Fnotes.md

# Copy a quarantined file back out to a chosen path
# (default: ./<original-basename> in the current working directory)
s3vfs lost-and-found restore 20260510T123456789Z_docs%2Fnotes.md `
  --target C:\Users\you\Desktop\notes-recovered.md
```

The first column from `list` (`FILENAME`) is the identifier consumed by
`diff` and `restore`; copy-paste it verbatim. The filename encoding is
`<UTC timestamp>_<URL-escaped original path>`, so `list` always prints
the decoded `ORIGINAL-PATH` alongside it. `restore` refuses to clobber an
existing destination unless you pass `--force`. `diff` requires `git` on
`PATH`; without it the command falls back to the binary summary.

