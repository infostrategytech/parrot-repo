using Parrot.Application.Agents;
using Parrot.Domain.Exceptions;

namespace Test.Parrot.Api.Tests.Services.Agents;

/// <summary>
/// The AI service's waiting list for one workspace, in memory, as its guide describes it: only open items are
/// listed; resolving closes an item as handled by the token's person, and one already closed comes back unchanged.
/// </summary>
public sealed class FakeWaitingListClient : IWaitingListClient
{
    public List<WaitingListItem> Items { get; } = [];

    /// <summary>The person each session token belongs to, as the service reads it from the token's sub.</summary>
    public Dictionary<string, string> People { get; } = [];

    /// <summary>The tokens each list read was tried with, in order, the ones that failed too.</summary>
    public List<string> ListReads { get; } = [];

    public List<string> Resolved { get; } = [];

    /// <summary>Every answer the service took, with the item it closed.</summary>
    public List<(string ItemId, WaitingListAnswer Answer)> Answers { get; } = [];

    /// <summary>Thrown by every call when set: the service down, refusing or over its limit.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Runs as a resolve begins: lets a test close the item first, as a teammate or a team reply would.</summary>
    public Action<WaitingListItem>? BeforeResolve { get; set; }

    /// <summary>Runs as an answer begins: lets a test take the item away first, as the service forgetting it would.</summary>
    public Action<WaitingListItem>? BeforeAnswer { get; set; }

    public Task<IReadOnlyList<WaitingListItem>> ListOpenAsync(string userToken, CancellationToken cancellationToken = default)
    {
        ListReads.Add(userToken);
        ThrowIfFailing();
        return Task.FromResult<IReadOnlyList<WaitingListItem>>(Items.Where(i => i.Status == WaitingListItem.OpenStatus).ToList());
    }

    public Task<WaitingListItem> GetAsync(string userToken, string itemId, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        return Task.FromResult(Find(itemId));
    }

    public Task<WaitingListItem> ResolveAsync(string userToken, string itemId, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        WaitingListItem item = Find(itemId);
        BeforeResolve?.Invoke(item);
        item = Find(itemId);
        if (item.Status != WaitingListItem.OpenStatus) return Task.FromResult(item);

        Resolved.Add(itemId);
        WaitingListItem closed = item with { Status = "resolved", Resolution = WaitingListItem.Handled, ResolvedBy = People[userToken] };
        Items[Items.IndexOf(item)] = closed;
        return Task.FromResult(closed);
    }

    public Task AnswerAsync(string userToken, string itemId, WaitingListAnswer answer, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        BeforeAnswer?.Invoke(Find(itemId));
        WaitingListItem item = Find(itemId);

        Answers.Add((itemId, answer));
        Items[Items.IndexOf(item)] = item with { Status = "resolved", Resolution = "answered", ResolvedBy = People[userToken] };
        return Task.CompletedTask;
    }

    /// <summary>Closes the item as something else would (a team reply, another person), without going through this client's resolve.</summary>
    public void Close(string itemId, string resolution, string? by)
    {
        WaitingListItem item = Find(itemId);
        Items[Items.IndexOf(item)] = item with { Status = "resolved", Resolution = resolution, ResolvedBy = by };
    }

    private WaitingListItem Find(string itemId) =>
        Items.SingleOrDefault(i => i.Id == itemId) ?? throw new NotFoundException("nothing with this id is waiting in this workspace");

    private void ThrowIfFailing()
    {
        if (Failure is not null) throw Failure;
    }
}
