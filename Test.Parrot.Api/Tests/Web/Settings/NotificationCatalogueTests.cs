using Parrot.Web.Features.Notifications;
using Parrot.Web.Features.Settings;
using Parrot.Web.Features.Dashboard.TaskRequests;
using Xunit;

namespace Test.Parrot.Api.Tests.Web.Settings;

public class NotificationCatalogueTests
{
    private static AppNotification Question(string id = "one", string agent = "agent-one") => new(id, AlertGroup.NeedsYou, NotificationKind.Action, NotificationMark.Agent, "legacy subject", "Inbox", DateTimeOffset.UtcNow, true, "/dashboard/inbox/controlled", Type: NotificationType.AgentQuestion, AgentId: agent, AgentName: "Ada", WorkspaceId: "workspace", RecipientId: "person");
    [Fact] public void EveryTypeHasOneUniqueKey() { Assert.Equal(Enum.GetValues<NotificationType>().Length - 1, NotificationCatalogue.All.Count); Assert.Equal(NotificationCatalogue.All.Count, NotificationCatalogue.All.Select(item => item.Key).Distinct().Count()); }
    [Fact] public void QuestionsUseBusinessNeutralSingularAndPlural() { Assert.Equal("Ada has a question for you.", NotificationCatalogue.Title(Question())); Assert.Equal("Ada has 3 questions that need your answers.", NotificationCatalogue.Title(Question(), 3)); }
    [Fact] public void ApprovalsAreNotCalledQuestions() { Assert.Equal("Ada needs your approval.", NotificationCatalogue.Title(Question() with { Type = NotificationType.AgentApproval })); Assert.Contains("actions", NotificationCatalogue.Title(Question() with { Type = NotificationType.AgentApproval }, 2)); }
    [Fact] public void MissingAgentNameHasSafeCopy() => Assert.Equal("Your agent has a question for you.", NotificationCatalogue.Title(Question() with { AgentName = null }));
    [Fact] public void KnownOutcomesUseTheFixedHeadline() => Assert.Equal("A payment was received.", NotificationCatalogue.Title(Question() with { Type = NotificationType.PaymentReceived, Text = "Kemi paid ₦20,000." }));
    [Fact] public void HandoversUseNeutralCopyAndStillOpenTheAssignedTask() { var definition = NotificationCatalogue.Find(NotificationType.HumanHandoverTask)!; Assert.Equal("Open task", definition.ActionLabel); Assert.Equal("A customer has been assigned to you.", definition.Title); }
    [Fact] public void GroupsCountUniquePendingRecordsNotRetries() { var source = Question(); var group = Assert.Single(NotificationProjection.Build([source, source, Question("two"), Question("three")])); Assert.Equal(3, group.Items.Count); Assert.Contains("3 questions", group.Title); }
    [Fact] public void QuestionsAndApprovalsNeverGroupTogether() => Assert.Equal(2, NotificationProjection.Build([Question(), Question("approval") with { Type = NotificationType.AgentApproval }]).Count);
    [Fact] public void AgentsWithSameNamesKeepSeparateGroups() => Assert.Equal(2, NotificationProjection.Build([Question(), Question("two", "agent-two")]).Count);
    [Fact] public void WorkspacesAndRecipientsNeverGroupTogether() { Assert.Equal(2, NotificationProjection.Build([Question(), Question("two") with { WorkspaceId = "other" }]).Count); Assert.Equal(2, NotificationProjection.Build([Question(), Question("two") with { RecipientId = "other" }]).Count); }
    [Fact] public void MissingIdentityNeverGroupsByDisplayName() => Assert.Equal(2, NotificationProjection.Build([Question() with { AgentId = null }, Question("two") with { AgentId = null }]).Count);
    [Fact] public void ResolvedQuestionsLeavePendingGroups() { var cards = NotificationProjection.Build([Question(), Question("two") with { ResolvedAt = DateTimeOffset.UtcNow, Resolution = NotificationResolution.Answered }]); Assert.Equal(2, cards.Count); Assert.Single(cards.Where(card => card.NeedsYou)); Assert.Contains(cards, card => card.Title.Contains("Answered")); }
    [Fact] public void ReadingAGroupDoesNotResolveItsChildren() { var card = Assert.Single(NotificationProjection.Build([Question() with { Unread = false }, Question("two") with { Unread = false }])); Assert.False(card.Unread); Assert.True(card.NeedsYou); }
    [Fact] public void NewChildMakesTheGroupUnread() => Assert.True(Assert.Single(NotificationProjection.Build([Question() with { Unread = false }, Question("two")])).Unread);
    [Theory] [InlineData(NotificationResolution.Declined, "Declined")] [InlineData(NotificationResolution.Cancelled, "Cancelled")] [InlineData(NotificationResolution.WaitingOnCustomer, "Waiting on customer")]
    public void ResolutionDoesNotAlwaysSayDone(NotificationResolution state, string text) => Assert.Equal(text, NotificationCatalogue.ResolutionLabel(Question() with { Resolution = state }));
    [Theory] [InlineData("https://example.com", false)] [InlineData("//example.com", false)] [InlineData("/\\example.com", false)] [InlineData("/dashboard/tasks/T-1", true)] [InlineData("/dashboard/inbox/one?message=two", true)]
    public void DestinationsMustStayInsideTheApp(string href, bool expected) => Assert.Equal(expected, NotificationCatalogue.IsInternalDestination(href));
    [Fact] public void OwnerFallbackOnlyWhenNoEligibleTeammateExists() { var owner = new TaskPerson("owner", "Owner"); Assert.Same(owner, HandoverAssignmentRules.Choose(owner, [], null)); Assert.Same(owner, HandoverAssignmentRules.Choose(owner, [new(new("inactive", "Inactive"), false, true), new(new("not-allowed", "Not allowed"), true, false)], null)); }
    [Fact] public void EligibleTeammateReceivesTheHandover() { var owner = new TaskPerson("owner", "Owner"); var teammate = new TaskPerson("teammate", "Teammate"); Assert.Same(teammate, HandoverAssignmentRules.Choose(owner, [new(teammate, true, true)], null)); }
    [Fact] public void LargerTeamNeedsDeliberateRouting() { var owner = new TaskPerson("owner", "Owner"); HandoverCandidate[] team = [new(new("one", "One"), true, true), new(new("two", "Two"), true, true)]; Assert.Throws<InvalidOperationException>(() => HandoverAssignmentRules.Choose(owner, team, null)); Assert.Equal("two", HandoverAssignmentRules.Choose(owner, team, "two").Id); Assert.Throws<InvalidOperationException>(() => HandoverAssignmentRules.Choose(owner, team, "inactive")); }
    [Fact] public async Task ProductionDoesNotPretendToCreateHandoverTasks() { ICustomerHandoverTasks handovers = new UnavailableCustomerHandoverTasks(); Assert.False(handovers.Available); await Assert.ThrowsAsync<NotSupportedException>(() => handovers.CreateAsync(new("event", new("Customer", "instagram", ConversationId: "conversation"), "agent", "Ada", "Needs a teammate"))); }
}
