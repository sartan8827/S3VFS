using Microsoft.Extensions.Logging;
using S3VFS.Net;
using S3VFS.ObjectStore;

namespace S3VFS.Configuration;

/// <summary>
/// Materializes a fully-populated <see cref="ProjFsProviderOptions"/> from a
/// parsed <see cref="S3vfsMountConfig"/>, applying defaults and resolving
/// referenced credentials. All per-mount settings are sourced from the config
/// file: the CLI surface no longer accepts mount-level overrides, so the
/// config is the single source of truth.
/// </summary>
internal static class MountOptionsBuilder
{
    /// <summary>
    /// Builds and validates the runtime options for a single mount. Throws
    /// <see cref="S3vfsConfigException"/> when a required field
    /// (<c>bucket</c> / <c>root-folder</c>) is missing, when a referenced AWS
    /// profile cannot be resolved, or when bandwidth / multipart / retry
    /// values fail their bounds checks. The supplied <paramref name="logger"/>
    /// is used only for the "using AWS credentials from profile X" notice.
    /// </summary>
    public static ProjFsProviderOptions Build(
        S3vfsMountConfig mount,
        ILogger logger,
        ICredentialRefreshNotifier? refreshNotifier = null) =>
        Build(mount, DefaultSharedProfileResolver.Instance, logger, refreshNotifier);

    /// <summary>
    /// Test-friendly overload that lets callers swap out the SDK shared-profile
    /// lookup. Production code uses <see cref="DefaultSharedProfileResolver"/>.
    /// </summary>
    internal static ProjFsProviderOptions Build(
        S3vfsMountConfig mount,
        ISharedProfileResolver sharedProfileResolver,
        ILogger logger,
        ICredentialRefreshNotifier? refreshNotifier = null)
    {
        if (string.IsNullOrEmpty(mount.Bucket))
        {
            throw new S3vfsConfigException(
                $"Mount '{mount.Name}': 'bucket' is required. Set 'bucket' inside the [[mount]] " +
                "table (or at the document root for the legacy single-mount form).");
        }

        if (string.IsNullOrEmpty(mount.RootFolder))
        {
            throw new S3vfsConfigException(
                $"Mount '{mount.Name}': 'root-folder' is required. Set 'root-folder' inside the " +
                "[[mount]] table.");
        }

        var credentials = ResolveCredential(
            sharedProfileResolver, mount.AwsProfile, mount.Name, logger);

        BandwidthLimits bandwidthLimits;
        long? multipartThresholdBytes;
        long? multipartPartSizeBytes;
        try
        {
            bandwidthLimits = new BandwidthLimits(
                UpBytesPerSecond: BandwidthSize.Parse(mount.BandwidthUp),
                DownBytesPerSecond: BandwidthSize.Parse(mount.BandwidthDown));
            multipartThresholdBytes = BandwidthSize.Parse(mount.MultipartThreshold);
            multipartPartSizeBytes = BandwidthSize.Parse(mount.MultipartPartSize);
        }
        catch (FormatException ex)
        {
            throw new S3vfsConfigException($"Mount '{mount.Name}': {ex.Message}", ex);
        }

        if (MultipartSettingsValidator.Validate(
                multipartThresholdBytes, multipartPartSizeBytes) is { } error)
        {
            throw new S3vfsConfigException($"Mount '{mount.Name}': {error}");
        }

        if (mount.RetryMaxAttempts is < 1)
        {
            throw new S3vfsConfigException(
                $"Mount '{mount.Name}': 'retry-max-attempts' must be at least 1 " +
                $"(got {mount.RetryMaxAttempts}).");
        }

        if (mount.MaxConcurrentUploads is < 1)
        {
            throw new S3vfsConfigException(
                $"Mount '{mount.Name}': 'max-concurrent-uploads' must be at least 1 " +
                $"(got {mount.MaxConcurrentUploads}).");
        }

        if (mount.MaxConcurrentDownloads is < 1)
        {
            throw new S3vfsConfigException(
                $"Mount '{mount.Name}': 'max-concurrent-downloads' must be at least 1 " +
                $"(got {mount.MaxConcurrentDownloads}).");
        }

        if (mount.MaxMultipartParts is < 1)
        {
            throw new S3vfsConfigException(
                $"Mount '{mount.Name}': 'max-multipart-parts' must be at least 1 " +
                $"(got {mount.MaxMultipartParts}).");
        }

        var changeSource = mount.ChangeSource ?? ChangeSourceKind.Polling;
        if (changeSource is ChangeSourceKind.Events && string.IsNullOrEmpty(mount.EventQueue))
        {
            throw new S3vfsConfigException(
                $"Mount '{mount.Name}': change-source 'events' requires 'event-queue' " +
                "(an SQS queue URL or name). See README for the necessary S3 → EventBridge → SQS setup.");
        }

        return new ProjFsProviderOptions
        {
            Bucket = mount.Bucket,
            VirtRoot = mount.RootFolder,
            EndpointUrl = mount.EndpointUrl,
            Region = mount.Region,
            KeyPrefix = mount.Prefix,
            // Verbose is process-level, not per-mount; the CLI host sets it via the
            // top-level options before instantiating the provider.
            Verbose = false,
            ReadOnly = mount.ReadOnly ?? false,
            SyncIntervalSeconds = mount.SyncIntervalSeconds ?? 30,
            ChangeSource = changeSource,
            SyncMode = mount.SyncMode ?? SyncMode.OnDemand,
            EventQueue = mount.EventQueue,
            Credentials = credentials,
            BandwidthLimits = bandwidthLimits,
            MultipartThresholdBytes = multipartThresholdBytes,
            MultipartPartSizeBytes = multipartPartSizeBytes,
            RetryMaxAttempts = mount.RetryMaxAttempts,
            MaxConcurrentUploads = mount.MaxConcurrentUploads,
            MaxConcurrentDownloads = mount.MaxConcurrentDownloads,
            MaxMultipartParts = mount.MaxMultipartParts,
            AllowUnversioned = mount.AllowUnversioned ?? false,
            RefreshNotifier = refreshNotifier,
        };
    }

    /// <summary>
    /// Resolves an <c>aws-profile</c> name through the shared AWS profile store
    /// via <paramref name="sharedProfileResolver"/> (covers static keys in
    /// <c>~/.aws/credentials</c>, <c>credential_process</c> profiles produced by
    /// <c>aws login</c>, <c>sso_session</c> profiles written by
    /// <c>aws configure sso</c>, and assume-role chains). The SDK handles refresh
    /// for these natively. A miss is reported as an
    /// <see cref="S3vfsConfigException"/> naming the mount so multi-mount runs
    /// surface which entry blew up.
    /// </summary>
    private static AwsCredentialSource? ResolveCredential(
        ISharedProfileResolver sharedProfileResolver,
        string? profileName,
        string mountName,
        ILogger logger)
    {
        if (string.IsNullOrEmpty(profileName)) return null;

        var shared = sharedProfileResolver.Resolve(profileName);
        if (shared is not null)
        {
            logger.LogInformation(
                "Mount '{Mount}': using AWS credentials from {Source}.", mountName, shared.Description);
            return AwsCredentialSource.FromSdk(shared.Credentials, shared.Description);
        }

        throw new S3vfsConfigException(
            $"Mount '{mountName}': AWS profile '{profileName}' was not found in the shared AWS " +
            "profile store (~/.aws/config, ~/.aws/credentials). Configure the profile with the " +
            "AWS CLI, e.g. 'aws configure --profile <name>' for access keys, " +
            "'aws configure sso --profile <name>' for IAM Identity Center, or " +
            "'aws login --profile <name>' for AWS CLI 2.32+.");
    }
}
