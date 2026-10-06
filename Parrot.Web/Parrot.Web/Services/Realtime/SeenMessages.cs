namespace Parrot.Web.Services.Realtime;

/// <summary>
/// Remembers the message ids already shown, so a push that arrives twice
/// (the bus redelivers after a crash) is shown once. Bounded: only the most
/// recent ids are kept, which is enough for redeliveries, which come close
/// together.
/// </summary>
public sealed class SeenMessages(int capacity = 1000)
{
    private readonly HashSet<string> _ids = new();
    private readonly Queue<string> _order = new();
    private readonly object _gate = new();

    /// <summary>True the first time an id is seen, false on every repeat.</summary>
    public bool TryAdd(string messageId)
    {
        lock (_gate)
        {
            if (!_ids.Add(messageId)) return false;
            _order.Enqueue(messageId);
            if (_order.Count > capacity) _ids.Remove(_order.Dequeue());
            return true;
        }
    }
}
