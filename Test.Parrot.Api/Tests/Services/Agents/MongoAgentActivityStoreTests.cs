using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Parrot.Application.Agents;
using Parrot.Infrastructure.Agents;
using Parrot.Infrastructure.Messaging;

namespace Test.Parrot.Api.Tests.Services.Agents;

// The AI Operations screen reads these documents. Runs only against a disposable MongoDB
// (PARROT_TEST_MONGO); every test uses a database of its own and drops it afterwards.
public class MongoAgentActivityStoreTests
{
    public sealed class MongoFactAttribute : FactAttribute
    {
        public MongoFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("PARROT_TEST_MONGO") is null)
                Skip = "Set PARROT_TEST_MONGO to a disposable local MongoDB instance.";
        }
    }

    private static readonly DateTime T0 = new(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc);

    private static async Task WithStore(Func<MongoAgentActivityStore, Task> test)
    {
        var client = new MongoClient(Environment.GetEnvironmentVariable("PARROT_TEST_MONGO"));
        string database = "parrot_test_" + Guid.NewGuid().ToString("N");
        try
        {
            await test(new MongoAgentActivityStore(client, Options.Create(new MongoDbSettings { Database = database, Collection = "messages" })));
        }
        finally
        {
            await client.DropDatabaseAsync(database);
        }
    }

    private static AgentActivityRecord Turn(string owner, string turnId, DateTime at, string status = AgentActivityTypes.TurnStatus.Completed) => new()
    {
        OwnerUserId = owner, Type = AgentActivityTypes.TurnCompleted, EventKey = $"{turnId}:{status}", TurnId = turnId,
        ConversationKey = "whatsapp:2348012345678:2349011112222", Decision = "PERMIT", Confidence = 0.92, Status = status,
        Channel = "whatsapp", Reply = "Your refund is on its way.", OccurredAt = at, Payload = "{}",
    };

    private static AgentActivityRecord Approval(string owner, string approvalId, DateTime at) => new()
    {
        OwnerUserId = owner, Type = AgentActivityTypes.ApprovalRequested, EventKey = approvalId, ApprovalId = approvalId,
        TurnId = "turn_9", Tool = "payments.refund", Reason = "VIP refund after preparation", OccurredAt = at, Payload = "{}",
    };

    [MongoFact]
    public async Task ARedeliveredEventIsStoredOnce()
    {
        await WithStore(async store =>
        {
            Assert.True(await store.InsertAsync(Turn("owner-a", "turn_1", T0)));
            Assert.False(await store.InsertAsync(Turn("owner-a", "turn_1", T0)));
            Assert.Single(await store.ListAsync("owner-a", [AgentActivityTypes.TurnCompleted], T0.AddHours(-1), T0.AddHours(1)));
        });
    }

    [MongoFact]
    public async Task OneOwnersEventNeverStandsInForAnothersOrShowsUpForThem()
    {
        await WithStore(async store =>
        {
            Assert.True(await store.InsertAsync(Turn("owner-a", "turn_1", T0)));
            Assert.True(await store.InsertAsync(Turn("owner-b", "turn_1", T0)));  // same key, other tenant: not a duplicate

            var a = await store.ListAsync("owner-a", [AgentActivityTypes.TurnCompleted], T0.AddHours(-1), T0.AddHours(1));
            var c = await store.ListAsync("owner-c", [AgentActivityTypes.TurnCompleted], T0.AddHours(-1), T0.AddHours(1));
            Assert.All(a, r => Assert.Equal("owner-a", r.OwnerUserId));
            Assert.Single(a);
            Assert.Empty(c);
            Assert.Empty(await store.ListByTurnIdsAsync("owner-c", ["turn_1"]));
        });
    }

    [MongoFact]
    public async Task ListsRespectTheWindowAndOrder()
    {
        await WithStore(async store =>
        {
            await store.InsertAsync(Turn("owner-a", "turn_1", T0));
            await store.InsertAsync(Turn("owner-a", "turn_2", T0.AddMinutes(5)));
            await store.InsertAsync(Turn("owner-a", "turn_3", T0.AddDays(-2)));

            var window = await store.ListAsync("owner-a", [AgentActivityTypes.TurnCompleted], T0.AddHours(-1), T0.AddHours(1));
            Assert.Equal(["turn_1", "turn_2"], window.Select(r => r.TurnId));
            var recent = await store.ListRecentAsync("owner-a", AgentActivityTypes.TurnCompleted, 2);
            Assert.Equal(["turn_2", "turn_1"], recent.Select(r => r.TurnId));
            var first = Assert.Single(await store.ListByTurnIdsAsync("owner-a", ["turn_1"]));
            Assert.Equal(0.92, first.Confidence);
            Assert.Equal(DateTimeKind.Utc, first.OccurredAt.Kind);
        });
    }

    // The waiting list and the inbox read the open items oldest first, whatever order they arrived in.
    [MongoFact]
    public async Task ListPendingAsync_ReturnsTheOpenItemsOldestFirst()
    {
        await WithStore(async store =>
        {
            await store.InsertAsync(Approval("owner-a", "apr_2", T0.AddMinutes(2)));
            await store.InsertAsync(Approval("owner-a", "apr_0", T0));
            await store.InsertAsync(Approval("owner-a", "apr_1", T0.AddMinutes(1)));

            Assert.Equal(["apr_0", "apr_1", "apr_2"], (await store.ListPendingAsync("owner-a")).Select(r => r.ApprovalId));
        });
    }

    [MongoFact]
    public async Task AnApprovalLeavesTheWaitingListOnceDecidedAndOnlyOnce()
    {
        await WithStore(async store =>
        {
            await store.InsertAsync(Approval("owner-a", "apr_1", T0));
            Assert.Single(await store.ListPendingAsync("owner-a"));
            Assert.Empty(await store.ListPendingAsync("owner-b"));

            Assert.Equal(0, await store.MarkApprovalDecidedAsync("owner-b", "apr_1", "approved", "mallory", null, T0.AddMinutes(1)));
            Assert.Equal(1, await store.MarkApprovalDecidedAsync("owner-a", "apr_1", "approved", "manager:ngozi", "refund in full", T0.AddMinutes(2)));
            Assert.Equal(0, await store.MarkApprovalDecidedAsync("owner-a", "apr_1", "denied", "manager:ngozi", null, T0.AddMinutes(3)));

            Assert.Empty(await store.ListPendingAsync("owner-a"));
            var decided = Assert.Single(await store.ListRecentAsync("owner-a", AgentActivityTypes.ApprovalRequested, 5));
            Assert.Equal("approved", decided.Resolution);
            Assert.Equal("refund in full", decided.Instruction);
        });
    }
}
