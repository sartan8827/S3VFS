# S3VFS — S3 Virtual File System for Windows

[日本語 README](./README.ja.md)

[![CI](https://github.com/sartan123/S3Files-for-Windows/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/sartan123/S3Files-for-Windows/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](./LICENSE)

S3VFS mounts a cloud object-store bucket as an ordinary local folder on
Windows, with on-demand hydration and two-way synchronization, built on
[Windows Projected File System (ProjFS)][projfs]. It is a **driver-free
alternative to `rclone mount`** on Windows: ProjFS ships as an optional
feature in Windows 10 1809+ and Windows 11, so S3VFS does not need WinFsp
(or any other third-party kernel driver) to be installed.

S3VFS supports **Amazon S3** only — see
[Supported backends](#supported-backends) below.

[projfs]: https://learn.microsoft.com/en-us/windows/win32/projfs/projected-file-system

## Overview

S3VFS exposes a cloud object-store bucket through Windows Explorer the same
way OneDrive Files On-Demand exposes cloud files: directory entries are
visible without a full download, file contents are hydrated on first open,
local writes / deletes / renames are propagated back to the bucket, and
external changes are picked up by a background poller.

ProjFS — the Windows kernel-mode component that also powers OneDrive Files
On-Demand and VFS for Git — is the kernel side here. `s3vfs` itself runs as
a normal user-mode process: there is no custom driver to install or sign.

## Compared to `rclone mount`

`rclone` is the de-facto way to mount object storage on Windows, and its
broad backend coverage remains unmatched. S3VFS is a narrower tool: it
focuses on the Windows experience and trades backend breadth for a
zero-third-party-driver install path.

| | S3VFS | `rclone mount` |
| --- | --- | --- |
| Kernel component | Windows-built-in **ProjFS** (enable an optional feature; no driver install) | **WinFsp** — separate kernel driver, MSI install required |
| Install footprint | Single signed `s3vfs.exe` (Native AOT) | `rclone.exe` + WinFsp MSI |
| AppLocker / WDAC fit | No third-party kernel driver to allow-list | Requires WinFsp kernel driver to be allowed by policy |
| Explorer integration | Native ProjFS placeholders — the same "online-only" model OneDrive uses | FUSE-style mount; files appear as fully-present |
| Backends | Amazon S3 only | 70+ backends |
| Runtime dependency | None (Native AOT) | None (single Go binary) |

If you need a backend S3VFS does not support, keep using rclone. If you
want object storage to feel like OneDrive on Windows without installing a
kernel driver, S3VFS is for you.

## Supported backends

S3VFS targets **Amazon S3** only. The S3 implementation lives in
[`S3Backend`](src/S3VFS.Core/ObjectStore/S3/S3Backend.cs), and push-mode
change notifications use S3 → EventBridge → SQS. The `endpoint-url` key
exists mainly so the integration tests can run against LocalStack; other
S3-compatible services are not a supported target.

## Documentation

The detailed reference is split into focused pages under
[`docs/`](docs/):

- **[Configuration](docs/configuration.md)** — prerequisites, the small
  CLI surface, the full `s3vfs.toml` key reference, and the multi-mount
  layout.
- **[Performance tuning](docs/tuning.md)** — bandwidth ceilings,
  multipart upload thresholds, request concurrency, the HTTP transport,
  and retry policy.
- **[Synchronization and change events](docs/sync-and-events.md)** —
  polling vs. push-mode change detection, on-demand sync, read-only
  mounts, and SQS setup.
- **[Observability](docs/observability.md)** — structured logging and
  user-metadata round-trip.
- **[Authentication and credentials](docs/credentials.md)** — AWS
  shared profile / SSO / `aws login`.
- **[Troubleshooting](docs/troubleshooting.md)** — reading start-up
  errors and the `s3vfs lost-and-found` recovery CLI.

## Quick start

The shortest possible mount config is two keys:

```toml
# ./s3vfs.toml
bucket      = "my-bucket"
root-folder = "C:/Users/you/S3VFS"
```

With AWS credentials available through the SDK chain and bucket
versioning turned on (see
[Configuration → Prerequisites](docs/configuration.md#prerequisites) for
the full list), start the mount:

```powershell
s3vfs help                       # list the supported commands and options (same as bare `s3vfs`)
s3vfs mount                      # start every configured mount
s3vfs mount --name personal      # start one mount by name
```

Open the configured root folder in Explorer and the bucket contents
appear.

## Architecture

`s3vfs` is a user-mode ProjFS provider. `PrjFlt.sys` (the Windows ProjFS
filter driver, shipped by Microsoft as part of the OS) is the kernel side,
and `s3vfs` is the provider that hydrates entries from the configured
object store and propagates local changes back.

```
 ┌─────────────────────┐  StartDirectoryEnumeration / GetPlaceholderInfo
 │  Windows Shell      │  GetFileData
 │  (PrjFlt.sys)       │ ───────────────────────────────────┐
 └─────────┬───────────┘                                    │
           │ placeholders                                   ▼
           │ + hydrated bytes                    ┌─────────────────────┐
 ┌─────────▼───────────┐  WriteFileData /        │  ProjFsProvider     │
 │  C:\…\S3VFS         │  WritePlaceholderInfo   │  (IRequiredCallbacks)│
 │  (virtualization    │ ←──────────────────────│                     │
 │   root)             │                         └────┬──────┬─────────┘
 └─────────┬───────────┘                              │      │ AWS SDK
           │ local writes                             ▼      ▼
           │ (notification callbacks)           ┌──────────────┐
 ┌─────────▼───────────┐  PUT / DELETE / COPY   │  S3 bucket   │
 │ NotificationCallbacks│ ─────────────────────→│              │
 └─────────────────────┘                        └──────┬───────┘
                                                       │
 ┌─────────────────────┐  ListObjectsV2 (poll)         │
 │ ObjectStoreChange   │ ←─────────────────────────────┘
 │ Watcher             │       SQS ReceiveMessage
 │  + LostAndFound     │ ←─────────  EventBridge ←──── (optional)
 └─────────────────────┘
```

Roughly:

- [`ProjFsProvider`](src/S3VFS/ProjFs/ProjFsProvider.cs) implements
  `IRequiredCallbacks` from the managed ProjFS wrapper. Directory enumeration,
  placeholder metadata, and on-demand hydration all flow through here.
- [`NotificationCallbacks`](src/S3VFS/ProjFs/NotificationCallbacks.cs)
  receives ProjFS notifications for local writes / deletes / renames and
  forwards them to the object-store backend.
- [`S3Backend`](src/S3VFS.Core/ObjectStore/S3/S3Backend.cs) wraps AWSSDK.S3
  behind the [`IObjectStoreBackend`](src/S3VFS.Core/ObjectStore/IObjectStoreBackend.cs)
  with the small, ProjFS-shaped surface the provider needs (list, head,
  range read, upload, delete, rename-by-copy). Uploads at or above the
  configured `multipart-threshold` (default 16 MiB) are routed through
  `TransferUtility` so large files are split into `multipart-part-size`
  chunks (default 5 MiB) and uploaded in parallel. It lives in a cross-platform Core library so
  integration tests can run against LocalStack on Linux without pulling in
  the Windows-only ProjFS bindings. When `prefix` is set, the backend
  transparently rewrites virtualization-root-relative paths into the
  full bucket key (`<prefix>/<path>`) on every API call.
- [`ObjectStoreChangeWatcher`](src/S3VFS.Core/Sync/ObjectStoreChangeWatcher.cs)
  applies external bucket changes back into ProjFS. Changes are discovered
  through pluggable [`IChangeSource`](src/S3VFS.Core/Sync/IChangeSource.cs)
  implementations:
  [`OnDemandPollingChangeSource`](src/S3VFS.Core/Sync/OnDemandPollingChangeSource.cs)
  (the default) re-lists only the directories the user has visited via
  ProjFS using `Delimiter='/'`,
  [`PollingChangeSource`](src/S3VFS.Core/Sync/PollingChangeSource.cs)
  re-lists the entire bucket on a fixed cadence and diffs against an
  in-memory snapshot (selected by `sync-mode = "full"`), and
  [`SqsChangeSource`](src/S3VFS.Core/Sync/Sqs/SqsChangeSource.cs)
  long-polls an SQS queue carrying EventBridge S3 notifications. The object
  store is treated as the source of truth: if a remote change collides with
  an unsynced local edit, the local copy is moved to a `.s3vfs-lost+found`
  quarantine directory.

## Building

### Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (the exact
  version is pinned in [`global.json`](./global.json))
- Visual Studio 2022 or Build Tools with the **"Desktop development with
  C++"** workload — required for `link.exe` and the Windows SDK libraries
  used by Native AOT publishing
- Windows x64 (the host project pins `RuntimeIdentifier=win-x64` because
  ProjFS is Windows-only)

### Debug build

```powershell
dotnet build S3VFS.slnx -c Debug
dotnet run --project src\S3VFS    # mount config supplied via s3vfs.toml
```

### Release build (Native AOT, single binary)

```powershell
dotnet publish src\S3VFS -c Release -r win-x64 -o publish\win-x64
```

The output is a self-contained `s3vfs.exe`. End users do **not** need the
.NET runtime installed.

### Tests

```powershell
# Unit tests (Windows or Linux)
dotnet test tests\S3VFS.Core.UnitTests

# Integration tests against LocalStack (requires Docker)
dotnet test tests\S3VFS.Core.IntegrationTests
```

The integration test project targets `net10.0` and only references the
cross-platform `S3VFS.Core` library, so it can run on Linux CI runners
against [LocalStack](https://github.com/localstack/localstack) via
[Testcontainers](https://dotnet.testcontainers.org/).

## Why C# / .NET?

ProjFS is a Windows kernel feature; any client must talk to it through native
APIs. Rust, Go, and C++ are all reasonable choices, but C# wins here for two
specific reasons:

1. **Microsoft ships an official managed wrapper for ProjFS.** The
   [`Microsoft.Windows.ProjFS`][projfs-nuget] NuGet package is the same
   binding used by Microsoft's own [SimpleProvider sample][simple-provider]
   and by VFS for Git. We can implement `IRequiredCallbacks` in C# and let
   the wrapper handle the COM/P-Invoke boundary, instead of hand-rolling
   the bindings ourselves.
2. **Native AOT removes the runtime tax.** A long-running user-mode
   filesystem provider has tight latency requirements: every directory
   listing and every byte of `GetFileData` is on the user's hot path.
   Publishing with `PublishAot=true` produces a single, statically compiled
   `s3vfs.exe` with no JIT, no ReadyToRun, and no managed runtime install
   on the end user's machine — the startup and per-call cost is comparable
   to a native binary while we keep C#'s ergonomics for the cloud SDKs and
   the ProjFS callbacks.

The cross-platform pieces (`S3VFS.Core`) target plain `net10.0` and stay
AOT-compatible (`IsAotCompatible=true`), which is what lets LocalStack-based
integration tests run on Linux CI.

[projfs-nuget]: https://www.nuget.org/packages/Microsoft.Windows.ProjFS
[simple-provider]: https://github.com/microsoft/ProjFS-Managed-API

## References

- [Windows Projected File System (ProjFS) overview][projfs]
- [Microsoft `ProjFS-Managed-API` SimpleProvider sample][simple-provider]
- [.NET Native AOT deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- [AWS SDK for .NET — S3](https://docs.aws.amazon.com/sdk-for-net/v3/developer-guide/s3-apis-intro.html)
- [`rclone`](https://rclone.org/) — comparable cross-platform mount utility;
  S3VFS is positioned as the no-extra-driver Windows-only alternative
- [WinFsp](https://winfsp.dev/) — the kernel driver `rclone mount` depends
  on, which S3VFS replaces with the Windows-built-in ProjFS feature

## License
