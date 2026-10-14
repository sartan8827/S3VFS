using Amazon.Runtime;

namespace S3VFS.ObjectStore;

/// <summary>
/// Pairs a refreshing <see cref="AWSCredentials"/> instance handed back by the AWS
/// SDK (for shared <c>~/.aws/config</c> profiles, <c>credential_process</c>, SSO,
/// assume-role chains, etc.) with a description of where it came from. Backends
/// call <see cref="Materialize"/> to obtain the SDK credential object to feed into
/// <see cref="AmazonServiceClient"/>s.
/// </summary>
internal sealed class AwsCredentialSource
{
    /// <summary>
    /// SDK-resolved credentials (e.g. <see cref="ProcessAWSCredentials"/>).
    /// </summary>
    public AWSCredentials Sdk { get; }

    /// <summary>
    /// Human-readable description of the resolution path (e.g.
    /// <c>"shared profile 's3vfs-login' (sso)"</c>).
    /// Surfaced in the mount-startup log message.
    /// </summary>
    public string Description { get; }

    private AwsCredentialSource(AWSCredentials sdk, string description)
    {
        Sdk = sdk;
        Description = description;
    }

    /// <summary>
    /// Wraps SDK-resolved credentials returned by <c>CredentialProfileStoreChain</c>
    /// or any other refreshing provider; the caller keeps ownership of refresh.
    /// </summary>
    public static AwsCredentialSource FromSdk(AWSCredentials credentials, string description)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        return new AwsCredentialSource(credentials, description);
    }

    /// <summary>
    /// Returns the wrapped SDK credentials so their refresh behavior is preserved.
    /// </summary>
    public AWSCredentials Materialize() => Sdk;
}
