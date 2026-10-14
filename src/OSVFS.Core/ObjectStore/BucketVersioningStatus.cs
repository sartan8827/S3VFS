namespace OSVFS.ObjectStore;

/// <summary>
/// Coarse versioning state of the linked bucket; only the "Enabled" case
/// satisfies the startup safety check. Anything other than "actively protecting
/// writes" collapses into <see cref="NotEnabled"/>.
/// </summary>
internal enum BucketVersioningStatus
{
    /// <summary>
    /// Versioning has never been configured, or was suspended after being enabled.
    /// </summary>
    NotEnabled,

    /// <summary>
    /// Versioning is actively protecting the bucket against accidental overwrites.
    /// </summary>
    Enabled,
}
