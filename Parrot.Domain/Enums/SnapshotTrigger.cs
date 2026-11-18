namespace Parrot.Domain.Enums;

/// <summary>
/// Why a business-knowledge snapshot was sent. The names go to Omni as they are
/// and the numbers are stored on business sources, so neither may change.
/// </summary>
public enum SnapshotTrigger
{
    ChannelConnected = 1,
    ChannelDisconnected = 2,
    ManualRefresh = 3,

    /// <summary>Background work with no request behind it, such as sources seeded for existing connections.</summary>
    Scheduled = 4,

    WorkspaceCreated = 5,
}
