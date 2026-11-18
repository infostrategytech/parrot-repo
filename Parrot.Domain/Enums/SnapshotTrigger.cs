namespace Parrot.Domain.Enums;

/// <summary>
/// Why a business-knowledge snapshot was sent. The names go to Omni as they are
/// and the numbers are stored on business sources, so neither may change.
/// </summary>
public enum SnapshotTrigger
{
    ChannelConnected = 1,
    ChannelDisconnected = 2,

    /// <summary>The owner's "look again": Omni re-reads every channel, skipping its 7-day rule (a paid read each).</summary>
    ManualRefresh = 3,

    /// <summary>Background work with no request behind it, such as sources seeded for existing connections.</summary>
    Scheduled = 4,

    WorkspaceCreated = 5,

    /// <summary>The owner synced the WhatsApp catalogue. Omni keeps its 7-day rule; the catalogue itself comes on catalog.items.</summary>
    CatalogSynced = 6,
}
