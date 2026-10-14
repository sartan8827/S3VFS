using S3VFS.ObjectStore.S3;

namespace S3VFS;

/// <summary>
/// Validates the multipart-upload knobs (<c>multipart-threshold</c> /
/// <c>multipart-part-size</c> in <c>s3vfs.toml</c>) against the S3 bounds.
/// Returns a human-readable error string on violation, or null when the
/// settings are acceptable. Both inputs may be null, meaning "use the backend
/// default".
/// </summary>
internal static class MultipartSettingsValidator
{
    /// <summary>
    /// Returns null when the supplied threshold and part size satisfy the S3
    /// limits, or a description of the first violation. The threshold must be
    /// positive; the part size must fall within
    /// <see cref="S3Backend.MinMultipartPartSizeBytes"/>..<see cref="S3Backend.MaxMultipartPartSizeBytes"/>.
    /// </summary>
    public static string? Validate(long? thresholdBytes, long? partSizeBytes)
    {
        if (thresholdBytes is <= 0)
        {
            return $"'multipart-threshold' must be positive (got {thresholdBytes}).";
        }
        if (partSizeBytes is { } size)
        {
            if (size < S3Backend.MinMultipartPartSizeBytes)
            {
                return $"'multipart-part-size' must be at least 5 MiB (got {size}).";
            }
            if (size > S3Backend.MaxMultipartPartSizeBytes)
            {
                return $"'multipart-part-size' must be at most 5 GiB (got {size}).";
            }
        }
        return null;
    }
}
