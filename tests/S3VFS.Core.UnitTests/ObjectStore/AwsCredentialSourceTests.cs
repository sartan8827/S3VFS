using Amazon.Runtime;
using S3VFS.ObjectStore;
using Xunit;

namespace S3VFS.Core.UnitTests.ObjectStore;

/// <summary>
/// Verifies <see cref="AwsCredentialSource"/> materialization: the wrapped SDK
/// credentials are returned verbatim so refresh behavior survives.
/// </summary>
public class AwsCredentialSourceTests
{
    [Fact]
    public void FromSdk_returns_wrapped_credentials_unchanged()
    {
        var wrapped = new BasicAWSCredentials("ASIASHARED", "shared-secret");
        var source = AwsCredentialSource.FromSdk(
            wrapped, "shared profile 's3vfs-login' (credential_process)");

        Assert.Same(wrapped, source.Materialize());
        Assert.Same(wrapped, source.Sdk);
    }
}
