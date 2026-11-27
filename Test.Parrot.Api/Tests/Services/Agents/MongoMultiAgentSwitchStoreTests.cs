using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Parrot.Infrastructure.Agents;
using Parrot.Infrastructure.Messaging;

namespace Test.Parrot.Api.Tests.Services.Agents;

// Runs only against a disposable MongoDB (PARROT_TEST_MONGO); each test uses a database of its own and drops it.
public sealed class MongoMultiAgentSwitchStoreTests
{
    private static async Task WithStore(Func<MongoMultiAgentSwitchStore, Task> test)
    {
        MongoClient client = new(Environment.GetEnvironmentVariable("PARROT_TEST_MONGO"));
        string database = "parrot_test_" + Guid.NewGuid().ToString("N");
        try
        {
            await test(new MongoMultiAgentSwitchStore(client, Options.Create(new MongoDbSettings { Database = database, Collection = "messages" })));
        }
        finally
        {
            await client.DropDatabaseAsync(database);
        }
    }

    [MongoAgentActivityStoreTests.MongoFact]
    public async Task AWorkspaceIsOffUntilSwitchedOnAndOneWorkspacesSwitchIsNeverAnothers()
    {
        await WithStore(async store =>
        {
            Assert.False(await store.IsOnAsync("workspace-a", default));

            await store.SetAsync("workspace-a", true, "user-ngozi", default);
            Assert.True(await store.IsOnAsync("workspace-a", default));
            Assert.False(await store.IsOnAsync("workspace-b", default));

            await store.SetAsync("workspace-a", false, "user-ngozi", default);
            Assert.False(await store.IsOnAsync("workspace-a", default));
        });
    }
}
