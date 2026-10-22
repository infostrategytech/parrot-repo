using Parrot.Domain.Entities;
using Parrot.Application.Teams;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Parrot.Api.Controllers;
using Parrot.Application.Messaging;
using Parrot.Domain.Enums;
using Parrot.Domain.Exceptions;

namespace Test.Parrot.Api.Tests.Services.Messaging;

public class MessageHistoryControllerTests
{
    private const string Owner = "owner-1";
    private const string Ada = "whatsapp:2348012345678:2349011112222";

    private readonly Mock<IMessageHistory> _history = new();

    private MessageHistoryController Controller(string? userId = Owner)
    {
        ClaimsIdentity identity = userId is null
            ? new ClaimsIdentity()
            : new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test");
        return new MessageHistoryController(_history.Object)
        {
            ControllerContext = new ControllerContext
            {
                // As WorkspaceAccessFilter leaves it: the owner, who reaches every channel.
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(identity),
                    Items = { [typeof(WorkspaceAccess)] = WorkspaceAccess.ForOwner(new Workspace { Id = userId ?? "none", OwnerId = userId ?? "none" }) },
                },
            },
        };
    }

    private void Returns(MessageHistoryPage page) =>
        _history.Setup(h => h.GetAfterAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);

    [Fact]
    public async Task ReadsTheSignedInUsersHistory_AfterTheCursor()
    {
        Returns(new MessageHistoryPage { Messages = [] });

        await Controller().Get(Ada, after: 7, limit: 20);

        _history.Verify(h => h.GetAfterAsync(Owner, Ada, 7, 20, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartsFromTheBeginning_WithTheDefaultPageSize()
    {
        Returns(new MessageHistoryPage { Messages = [] });

        await Controller().Get(Ada);

        _history.Verify(h => h.GetAfterAsync(Owner, Ada, 0, MessageHistoryController.DefaultPageSize, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("  ", 0)]
    [InlineData(Ada, -1)]
    public async Task RejectsAMissingKeyOrANegativeCursor(string? key, long after)
    {
        await Assert.ThrowsAsync<ValidationException>(() => Controller().Get(key, after));

        _history.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RejectsARequestWithNoUser()
    {
        await Assert.ThrowsAsync<UnauthorizedException>(() => Controller(userId: null).Get(Ada));

        _history.VerifyNoOtherCalls();
    }

    // The client merges recovered and pushed messages, so both must read the same.
    [Fact]
    public async Task WritesTheLivePushShape_PlatformAsTextAndCamelCase()
    {
        Returns(new MessageHistoryPage
        {
            Messages =
            [
                new StoredMessage
                {
                    MessageId = "whatsapp:wamid.1", ConversationKey = Ada, Sequence = 3,
                    Platform = MessagingPlatform.WhatsApp, OwnerUserId = Owner, Outgoing = false,
                    Sender = "2348012345678", Receiver = "2349011112222", Type = "text", Text = "Hi <b>",
                    PersistedAt = new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc),
                },
            ],
            NextCursor = 3,
        });

        JsonResult result = Assert.IsType<JsonResult>(await Controller().Get(Ada));
        JsonSerializerOptions options = Assert.IsType<JsonSerializerOptions>(result.SerializerSettings);
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value, options));

        JsonElement message = json.RootElement.GetProperty("messages")[0];
        Assert.Equal("whatsapp", message.GetProperty("platform").GetString());
        Assert.Equal("whatsapp:wamid.1", message.GetProperty("messageId").GetString());
        Assert.Equal(3, message.GetProperty("sequence").GetInt64());
        Assert.Equal(3, json.RootElement.GetProperty("nextCursor").GetInt64());

        // Customer text goes to browsers, so it keeps the HTML-safe escaping.
        Assert.DoesNotContain("<b>", JsonSerializer.Serialize(result.Value, options));
    }
}
