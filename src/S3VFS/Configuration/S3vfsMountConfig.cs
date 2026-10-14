using S3VFS.ObjectStore;

namespace S3VFS.Configuration;

/// <summary>
/// Strongly-typed view of a single mount entry inside <c>s3vfs.toml</c>.
/// Each entry corresponds to one virtualization root and one ProjFs provider
/// instance at runtime. Fields are nullable because they fall back to the
/// CLI / built-in defaults when omitted from the file.
/// </summary>
internal sealed class S3vfsMountConfig
{
    /// <summary>
    /// Name used to address this mount on the CLI (<c>s3vfs mount --name &lt;name&gt;</c>)
    /// and in log entries. Must be unique within a config file. Defaults to
    /// <c>"default"</c> when the legacy single-mount form is used.
    /// </summary>
    public string Name { get; init; } = "default";

    /// <summary>
    /// S3 bucket projected into the virtualization root.
    /// </summary>
    public string? Bucket { get; init; }

    /// <summary>
    /// Local directory marked as the ProjFS virtualization root.
    /// </summary>
    public string? RootFolder { get; init; }

    /// <summary>
    /// Endpoint override for S3-compatible servers (LocalStack, MinIO).
    /// </summary>
    public string? EndpointUrl { get; init; }

    /// <summary>
    /// AWS region; null falls back to the SDK's default resolution chain.
    /// </summary>
    public string? Region { get; init; }

    /// <summary>
    /// Optional key prefix; only objects beneath it are projected into the root.
    /// </summary>
    public string? Prefix { get; init; }

    /// <summary>
    /// When true, ignore notification-driven local mutations and disable the change watcher.
    /// </summary>
    public bool? ReadOnly { get; init; }

    /// <summary>
    /// Polling interval (seconds) for the change watcher; zero disables it.
    /// </summary>
    public int? SyncIntervalSeconds { get; init; }

    /// <summary>
    /// Which change-detection strategy the watcher uses (polling or events).
    /// </summary>
    public ChangeSourceKind? ChangeSource { get; init; }

    /// <summary>
    /// Polling reconciliation strategy — on-demand (per visited directory) or
    /// full (whole bucket). Default <see cref="SyncMode.OnDemand"/>; only
    /// consulted when <see cref="ChangeSource"/> is <see cref="ChangeSourceKind.Polling"/>.
    /// </summary>
    public SyncMode? SyncMode { get; init; }

    /// <summary>
    /// SQS queue URL or queue name carrying EventBridge S3 notifications.
    /// </summary>
    public string? EventQueue { get; init; }

    /// <summary>
    /// AWS shared profile name (~/.aws/config, ~/.aws/credentials); null means use the SDK's default chain.
    /// </summary>
    public string? AwsProfile { get; init; }

    /// <summary>
    /// Total attempts (initial + retries) the AWS SDK makes on transient
    /// failures. Null falls back to the backend default.
    /// </summary>
    public int? RetryMaxAttempts { get; init; }

    /// <summary>
    /// When true, skip the bucket-versioning safety check and instead emit a
    /// repeated warning. Intended for CI / disposable buckets only.
    /// </summary>
    public bool? AllowUnversioned { get; init; }

    /// <summary>
    /// Returns a copy of this mount with non-null values from <paramref name="overlay"/>
    /// taking precedence per key. The <see cref="Name"/> is taken from
    /// <paramref name="overlay"/> when non-empty so a project-local override can
    /// rename a user-global mount entry.
    /// </summary>
    public S3vfsMountConfig MergeOverlay(S3vfsMountConfig overlay) => new()
    {
        Name = string.IsNullOrEmpty(overlay.Name) ? Name : overlay.Name,
        Bucket = overlay.Bucket ?? Bucket,
        RootFolder = overlay.RootFolder ?? RootFolder,
        EndpointUrl = overlay.EndpointUrl ?? EndpointUrl,
        Region = overlay.Region ?? Region,
        Prefix = overlay.Prefix ?? Prefix,
        ReadOnly = overlay.ReadOnly ?? ReadOnly,
        SyncIntervalSeconds = overlay.SyncIntervalSeconds ?? SyncIntervalSeconds,
        ChangeSource = overlay.ChangeSource ?? ChangeSource,
        SyncMode = overlay.SyncMode ?? SyncMode,
        EventQueue = overlay.EventQueue ?? EventQueue,
        AwsProfile = overlay.AwsProfile ?? AwsProfile,
        RetryMaxAttempts = overlay.RetryMaxAttempts ?? RetryMaxAttempts,
        AllowUnversioned = overlay.AllowUnversioned ?? AllowUnversioned,
    };
}
