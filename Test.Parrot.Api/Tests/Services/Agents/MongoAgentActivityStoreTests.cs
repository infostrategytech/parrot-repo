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

    // The Agents page counts a team reply for each agent that answered in it, so the agents asked are read back as stored.
    [MongoFact]
    public async Task ATeamRepliesAgentsAreKeptAndATurnWithoutThemReadsAsNone()
    {
        await WithStore(async store =>
        {
            AgentActivityRecord team = Turn("owner-a", "turn_1", T0);
            await store.InsertAsync(new AgentActivityRecord
            {
                OwnerUserId = team.OwnerUserId, Type = team.Type, EventKey = team.EventKey, TurnId = team.TurnId, Status = team.Status,
                OccurredAt = team.OccurredAt, Payload = team.Payload, AgentId = "agt_super",
                Contributors = [new TurnContributor("agt_tobi", "answered"), new TurnContributor("agt_kora", "timed_out")],
            });
            await store.InsertAsync(Turn("owner-a", "turn_2", T0.AddMinutes(1)));

            IReadOnlyList<AgentActivityRecord> turns = await store.ListAsync("owner-a", [AgentActivityTypes.TurnCompleted], T0.AddHours(-1), T0.AddHours(1));

            Assert.Equal([new TurnContributor("agt_tobi", "answered"), new TurnContributor("agt_kora", "timed_out")], turns[0].Contributors);
            Assert.Null(turns[1].Contributors);
        });
    }

    // The decision feeds the day's reply-time figure: recorded once, and only on the workspace's own approval.
    [MongoFact]
    public async Task MarkApprovalDecidedAsync_RecordsTheWorkspacesDecisionOnceAndOnlyOnce()
    {
        await WithStore(async store =>
        {
            await store.InsertAsync(Approval("owner-a", "apr_1", T0));

            Assert.Equal(0, await store.MarkApprovalDecidedAsync("owner-b", "apr_1", "approved", "mallory", null, T0.AddMinutes(1)));
            Assert.Equal(1, await store.MarkApprovalDecidedAsync("owner-a", "apr_1", "approved", "user-ngozi", "refund in full", T0.AddMinutes(2)));
            Assert.Equal(0, await store.MarkApprovalDecidedAsync("owner-a", "apr_1", "denied", "user-ngozi", null, T0.AddMinutes(3)));

            var decided = Assert.Single(await store.ListRecentAsync("owner-a", AgentActivityTypes.ApprovalRequested, 5));
            Assert.Equal("approved", decided.Resolution);
            Assert.Equal("user-ngozi", decided.ResolvedBy);
            Assert.Equal(T0.AddMinutes(2), decided.ResolvedAt);
            Assert.Equal("refund in full", decided.Instruction);
        });
    }
}
