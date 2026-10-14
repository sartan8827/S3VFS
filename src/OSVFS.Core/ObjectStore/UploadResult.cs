namespace OSVFS.ObjectStore;

/// <summary>
/// Result of a successful upload, used to update the watcher's snapshot so the next
/// poll doesn't re-import our own write.
/// </summary>
internal readonly record struct UploadResult(string ETag, string VersionId, long Size, DateTimeOffset LastModified);
