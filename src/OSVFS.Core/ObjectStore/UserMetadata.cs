using System.Text;

namespace OSVFS.ObjectStore;

/// <summary>
/// Helpers for the user-defined metadata that S3 exposes as <c>x-amz-meta-*</c>.
/// Centralizes the lowercase-key normalization; the per-object byte ceiling is
/// supplied through <see cref="IObjectStoreBackend.UserMetadataMaxBytes"/> at
/// validation time.
/// </summary>
internal static class UserMetadata
{
    /// <summary>
    /// Empty marker returned by <see cref="Normalize"/> when the input is null/empty,
    /// so callers can compare by reference without allocating.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Produces a lowercased-key, ordinal-comparing copy of <paramref name="metadata"/>,
    /// or <see cref="Empty"/> when the input is null/empty. AWS normalizes user
    /// metadata names to lowercase on the wire, so storing the same shape locally
    /// keeps round-trips bit-identical.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Normalize(
        IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0) return Empty;

        var copy = new Dictionary<string, string>(metadata.Count, StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            if (string.IsNullOrEmpty(key)) continue;
            copy[key.ToLowerInvariant()] = value ?? string.Empty;
        }
        return copy;
    }

    /// <summary>
    /// Throws <see cref="UserMetadataTooLargeException"/> when the combined UTF-8
    /// byte count of the (already-normalized) names and values exceeds
    /// <paramref name="maxBytes"/>. The limit is provider-specific and is supplied
    /// by the active backend through
    /// <see cref="IObjectStoreBackend.UserMetadataMaxBytes"/>; callers should run
    /// this against the same map they will hand to the backend so the validation
    /// matches what the provider sees.
    /// </summary>
    public static void EnsureWithinSizeLimit(
        IReadOnlyDictionary<string, string> metadata, int maxBytes)
    {
        var total = 0;
        foreach (var (key, value) in metadata)
        {
            total += Encoding.UTF8.GetByteCount(key);
            total += Encoding.UTF8.GetByteCount(value ?? string.Empty);
            if (total > maxBytes)
            {
                throw new UserMetadataTooLargeException(total, maxBytes);
            }
        }
    }
}

/// <summary>
/// Thrown when a caller asks the backend to attach more user metadata than the
/// per-object limit allows (S3: 2 KiB). The limit is carried on
/// <see cref="LimitBytes"/>.
/// </summary>
internal sealed class UserMetadataTooLargeException(int actualBytes, int limitBytes)
    : InvalidOperationException(
        $"User metadata is {actualBytes} bytes, exceeding the {limitBytes}-byte per-object limit.")
{
    /// <summary>
    /// Combined UTF-8 byte count of the supplied name/value pairs at the moment
    /// the limit was breached.
    /// </summary>
    public int ActualBytes { get; } = actualBytes;

    /// <summary>
    /// Provider-specific byte ceiling that was breached. Equal to the
    /// <see cref="IObjectStoreBackend.UserMetadataMaxBytes"/> of the backend
    /// that ran the validation.
    /// </summary>
    public int LimitBytes { get; } = limitBytes;
}
