using System.Text.Json;
using Parrot.Web.Components.Pages.Dashboard.Conversations;
using Parrot.Web.Features.Dashboard.TaskRequests;
using Parrot.Web.Services;

namespace Test.Parrot.Api.Tests.Web.Customers;

public class CustomerDetailsTests
{
    private static readonly Guid Connection = Guid.Parse("46d225a1-4f8d-4376-b5de-9c0ca4e438aa");
    private static CustomerListItemDto Row(params CustomerIdentityDto[] identities) => new(
        Guid.Parse("2ace67f0-ce96-4b73-b25a-67acac9647ab"), "Example customer", "Customer", [], [.. identities], null, "whatsapp", DateTime.UnixEpoch);

    [Fact]
    public void SameConversationHasTheSameAvatarInBothEntryPoints()
    {
        var identity = new CustomerIdentityDto(Guid.NewGuid(), Connection, "whatsapp", "2348011111111", null, DateTime.UnixEpoch);
        var conversation = new ConversationItem { Id = $"{Connection}:{identity.Sender}", ConnectionId = Connection, Sender = identity.Sender };
        Assert.Equal(CustomerView.From(conversation).Seed, CustomerView.SeedFor(Row(identity)));
    }

    [Fact]
    public void ACustomerWithoutMessagesKeepsTheirStableCustomerSeed() => Assert.Equal(Row().Id.ToString(), CustomerView.SeedFor(Row()));

    [Fact]
    public void LastCustomerMessageNeverUsesOutgoingActivityOrAnInternalNote()
    {
        var incoming = DateTime.UtcNow.AddMinutes(-10);
        var conversation = new ConversationItem
        {
            UpdatedAt = incoming.AddMinutes(8),
            Messages =
            [
                new() { Kind = MessageKind.Customer, ReceivedAt = incoming },
                new() { Kind = MessageKind.Human, IsOutgoing = true, ReceivedAt = incoming.AddMinutes(5) },
                new() { Kind = MessageKind.Note, ReceivedAt = incoming.AddMinutes(8), Text = "Internal only" },
            ],
        };
        Assert.Equal(incoming, CustomerView.From(conversation).LastMessageAt);
    }

    [Fact]
    public void KnownIncomingTimeFromTheContractWinsOverPartialLoadedHistory()
    {
        var incoming = DateTime.UtcNow;
        var view = CustomerView.From(new ConversationItem { LastCustomerMessageAt = incoming, UpdatedAt = incoming.AddMinutes(1) });
        Assert.Equal(incoming, view.LastMessageAt);
    }

    [Fact]
    public void UnknownIncomingTimeIsNotInvented() => Assert.Null(CustomerView.From(new ConversationItem { UpdatedAt = DateTime.UtcNow }).LastMessageAt);

    [Fact]
    public void TaskCreationRequiresAnExistingPrimaryConversation()
    {
        var manual = new CustomerView("manual", "Example", "EX", [new("whatsapp", "+2348011111111", Guid.Empty, "2348011111111")]);
        Assert.False(manual.CanCreateTask);
        Assert.True((manual with { Channels = [new("whatsapp", "+2348011111111", Connection, "2348011111111")] }).CanCreateTask);
    }

    [Fact]
    public void TaskLinkRetainsOnlyTheExistingConversationScopeAndEscapesLabels()
    {
        var id = $"{Connection}:sender+123";
        var url = CustomerTaskScope.Url("Ada & Sons", [id, id]);
        var query = System.Web.HttpUtility.ParseQueryString(new Uri("https://example.test" + url).Query);
        Assert.Equal("Ada & Sons", query["customer"]);
        Assert.Equal(new[] { id }, query.GetValues("conversation"));
        Assert.Equal("/dashboard/tasks", CustomerTaskScope.Url("Example", []));
    }

    [Fact]
    public void OptionalLocationIsReadOnlyWhenTheProfileContractSuppliesIt()
    {
        var profile = JsonSerializer.Deserialize<CustomerDetailDto>("{\"id\":\"2ace67f0-ce96-4b73-b25a-67acac9647ab\",\"name\":\"Example\",\"location\":\"Lagos\"}", new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("Lagos", profile!.Location);
        Assert.Null(JsonSerializer.Deserialize<CustomerDetailDto>("{\"name\":\"Example\"}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Location);
    }

    [Fact]
    public void CrossChannelScopeRemainsDeferred()
    {
        var conversation = new ConversationItem
        {
            ConnectionId = Connection, Sender = "2348011111111", Channel = "whatsapp",
            Customer = new InboxCustomerDto { Identities = [new("whatsapp", "2348011111111", true), new("instagram", "example", true)] },
        };
        var view = CustomerView.From(conversation);
        Assert.Single(view.Channels);
        Assert.Equal("whatsapp", view.Primary!.Platform);
    }
}
