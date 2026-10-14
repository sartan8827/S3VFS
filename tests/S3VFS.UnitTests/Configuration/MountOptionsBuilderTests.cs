using Amazon.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using S3VFS.Configuration;
using Xunit;

namespace S3VFS.UnitTests.Configuration;

/// <summary>
/// Unit tests for <see cref="MountOptionsBuilder"/>: validation, defaults,
/// and credential resolution. Mount-level CLI overrides are no longer part of
/// the surface, so the builder is exercised exclusively through
/// <see cref="S3vfsMountConfig"/> values.
/// </summary>
public class MountOptionsBuilderTests
{
    [Fact]
    public void Build_uses_mount_config_values()
    {
        var mount = new S3vfsMountConfig
        {
            Name = "alpha",
            Bucket = "alpha-bucket",
            RootFolder = @"C:\mounts\alpha",
            Region = "ap-northeast-1",
            Prefix = "team-a/",
            ReadOnly = true,
            SyncIntervalSeconds = 60,
            ChangeSource = ChangeSourceKind.Polling,
            SyncMode = SyncMode.Full,
            RetryMaxAttempts = 5,
        };

        var options = MountOptionsBuilder.Build(mount, NullLogger.Instance);

        Assert.Equal("alpha-bucket", options.Bucket);
        Assert.Equal(@"C:\mounts\alpha", options.VirtRoot);
        Assert.Equal("ap-northeast-1", options.Region);
        Assert.Equal("team-a/", options.KeyPrefix);
        Assert.True(options.ReadOnly);
        Assert.Equal(60, options.SyncIntervalSeconds);
        Assert.Equal(SyncMode.Full, options.SyncMode);
        Assert.Equal(5, options.RetryMaxAttempts);
    }

    [Fact]
    public void Build_throws_when_bucket_missing()
    {
        var mount = new S3vfsMountConfig
        {
            Name = "alpha",
            RootFolder = @"C:\mounts\alpha",
        };

        var ex = Assert.Throws<S3vfsConfigException>(() =>
            MountOptionsBuilder.Build(mount, NullLogger.Instance));
        Assert.Contains("alpha", ex.Message, StringComparison.Ordinal);
        Assert.Contains("bucket", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_throws_when_root_folder_missing()
    {
        var mount = new S3vfsMountConfig
        {
            Name = "alpha",
            Bucket = "alpha-bucket",
        };

        var ex = Assert.Throws<S3vfsConfigException>(() =>
            MountOptionsBuilder.Build(mount, NullLogger.Instance));
        Assert.Contains("root-folder", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_throws_when_events_change_source_lacks_event_queue()
    {
        var mount = new S3vfsMountConfig
        {
            Name = "alpha",
            Bucket = "alpha-bucket",
            RootFolder = @"C:\mounts\alpha",
            ChangeSource = ChangeSourceKind.Events,
        };

        var ex = Assert.Throws<S3vfsConfigException>(() =>
            MountOptionsBuilder.Build(mount, NullLogger.Instance));
        Assert.Contains("event-queue", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_throws_when_retry_max_attempts_below_one()
    {
        var mount = new S3vfsMountConfig
        {
            Name = "alpha",
            Bucket = "alpha-bucket",
            RootFolder = @"C:\mounts\alpha",
            RetryMaxAttempts = 0,
        };

        var ex = Assert.Throws<S3vfsConfigException>(() =>
            MountOptionsBuilder.Build(mount, NullLogger.Instance));
        Assert.Contains("retry-max-attempts", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_resolves_aws_profile_through_shared_profile_store()
    {
        var mount = new S3vfsMountConfig
        {
            Name = "alpha",
            Bucket = "alpha-bucket",
            RootFolder = @"C:\mounts\alpha",
            AwsProfile = "s3vfs-login",
        };
        var sdkCredentials = new BasicAWSCredentials("ASIASHARED", "shared-secret");
        var sharedResolver = new FakeSharedProfileResolver
        {
            Result = new SharedProfileResolution(
                sdkCredentials, "shared profile 's3vfs-login' (credential_process)"),
        };

        var options = MountOptionsBuilder.Build(
            mount, sharedResolver, NullLogger.Instance);

        Assert.Same(sdkCredentials, options.Credentials);
        Assert.Contains("credential_process", options.CredentialDescription);
        Assert.Equal(1, sharedResolver.Calls);
        Assert.Equal("s3vfs-login", sharedResolver.LastProfileName);
    }

    [Fact]
    public void Build_throws_when_aws_profile_is_not_found()
    {
        var mount = new S3vfsMountConfig
        {
            Name = "alpha",
            Bucket = "alpha-bucket",
            RootFolder = @"C:\mounts\alpha",
            AwsProfile = "absent",
        };

        var ex = Assert.Throws<S3vfsConfigException>(() =>
            MountOptionsBuilder.Build(
                mount, new FakeSharedProfileResolver(), NullLogger.Instance));
        Assert.Contains("absent", ex.Message, StringComparison.Ordinal);
        Assert.Contains("aws login", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_applies_built_in_defaults_when_mount_omits_them()
    {
        var mount = new S3vfsMountConfig
        {
            Name = "default",
            Bucket = "b",
            RootFolder = @"C:\b",
        };

        var options = MountOptionsBuilder.Build(mount, NullLogger.Instance);

        Assert.False(options.ReadOnly);
        Assert.Equal(30, options.SyncIntervalSeconds);
        Assert.Equal(ChangeSourceKind.Polling, options.ChangeSource);
        Assert.Equal(SyncMode.OnDemand, options.SyncMode);
        Assert.False(options.AllowUnversioned);
    }
}
