# Observability

[← Back to README](../README.md) · [日本語版](./observability.ja.md)

Structured logging and user-defined metadata round-trip.

---

## Structured logging

By default `s3vfs` writes single-line, human-readable log entries to the
console. Set `log-format = "json"` in
[`s3vfs.toml`](#configuration-file) (or pass `--log-format json` for an
ad-hoc override) to switch to a structured stream that log shippers such
as Datadog or Loki can parse without regex:

```powershell
s3vfs --log-format json    # one-off override; the file value applies otherwise
```

Each log entry is written as a single line (terminated with the platform
line separator) carrying one JSON object. Field names follow the keys
produced by `Microsoft.Extensions.Logging.Console`'s JSON formatter:

| Field | Description |
| --- | --- |
| `Timestamp` | UTC timestamp in `yyyy-MM-ddTHH:mm:ss.fffZ` format. |
| `EventId` | The `EventId.Id` of the log entry (`0` when not set). |
| `LogLevel` | `Trace`, `Debug`, `Information`, `Warning`, `Error`, or `Critical`. |
| `Category` | Logger category — typically the source type's full name (`S3VFS`, `S3VFS.ProjFs.ProjFsProvider`, ...). |
| `Message` | Final formatted message after structured-template substitution. |
| `State` | Object containing the original message template and each named placeholder (e.g. `{Bucket}`) as a separate property — preserved as structured data for downstream filtering. |
| `Exception` | Present only when an exception was attached; carries the formatted exception text. |

Sample line (pretty-printed here for readability — on the wire it is one
line):

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

## User-defined object metadata round-trip

S3 lets every object carry an arbitrary number of user-defined headers (the
`x-amz-meta-*` family) — for example a `tag`, an `author`, or any
application-specific marker the upload tool wrote. S3VFS preserves these
headers across hydration and re-upload so a local edit doesn't strip them.

When a placeholder is created, S3VFS reads the bucket-side user metadata via
`HeadObject` and mirrors every entry into a Windows NTFS **alternate data
stream** named `:s3vfs-user-meta` attached to the placeholder. The stream is
plain UTF-8 with one `key=value` pair per line. Names are normalized to
lowercase (matching the case S3 uses on the wire).

When a local edit is uploaded, S3VFS reads the same ADS back out and
reattaches every entry as `x-amz-meta-*` on the `PutObject` (or multipart)
request, so the headers survive the local edit cycle bit-for-bit. Newly
created local files have no stream attached and upload with no user
metadata, exactly as before.

You can inspect the mirrored metadata from PowerShell:

```powershell
# List streams attached to a hydrated placeholder
Get-Item C:\Users\you\S3VFS\meta\file.txt -Stream *

# Dump the metadata (UTF-8, one key=value per line)
Get-Content C:\Users\you\S3VFS\meta\file.txt -Stream s3vfs-user-meta
```

AWS limits the combined `x-amz-meta-*` name+value byte count to **2 KiB per
object**. S3VFS pre-validates uploads against the same limit and surfaces a
clear error before the network round-trip, instead of forwarding an
oversized request that S3 would reject with an opaque 400.

