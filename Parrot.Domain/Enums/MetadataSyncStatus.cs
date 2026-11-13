namespace Parrot.Domain.Enums;

/// <summary>Lifecycle of the background metadata fetch for a business source.</summary>
public enum MetadataSyncStatus
{
    /// <summary>Source created; metadata has not been attempted yet.</summary>
    Pending = 1,

    /// <summary>A worker has claimed this source; <c>MetadataSyncLockedUntil</c> is set.</summary>
    Processing = 2,

    /// <summary>Metadata was successfully retrieved and is stored in <c>Text</c> / <c>DisplayName</c>.</summary>
    Completed = 3,

    /// <summary>Transient failure; <c>NextMetadataSyncAttemptAt</c> holds the next retry time.</summary>
    RetryScheduled = 4,

    /// <summary>Max retries exhausted. Manual intervention or a permission fix required to reset.</summary>
    Failed = 5,

    /// <summary>Permanent failure (missing permission, unsupported account type). Will not be retried.</summary>
    Unavailable = 6,
}
