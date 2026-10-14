using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Moq;
using Parrot.SharedComponents.Services;
using Parrot.Web.Components.Pages.Dashboard.Conversations;

namespace Test.Parrot.Api.Tests.Services;

// Renders the real composer: once the channel's reply window has closed there is no reply box, only the way on.
public class ComposerWindowRenderTests
{
    private static async Task<string> RenderAsync(ConversationItem item, ComposerMode mode = ComposerMode.Reply)
    {
        ServiceCollection services = new();
        services.AddSingleton(new AuroraInterop(Mock.Of<IJSRuntime>()));
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using HtmlRenderer renderer = new(provider, NullLoggerFactory.Instance);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.RenderComponentAsync<ConversationComposer>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { ["Conversation"] = item, ["Mode"] = mode, ["CanAddNotes"] = true }));
            return root.ToHtmlString();
        });
    }

    private static ConversationItem Chat(string channel, string sender, TimeSpan sinceCustomerWrote, int windowHours = 24) => new()
    {
        Id = "c", ConnectionId = Guid.NewGuid(), Sender = sender, CustomerName = "Kunle", Channel = channel, AccountId = "100000000000001",
        LastCustomerMessageAt = DateTime.UtcNow - sinceCustomerWrote, ReplyWindowHours = windowHours,
    };

    [Fact]
    public async Task AClosedWhatsAppWindowHasNoReplyBoxAndLinksToTheCustomersChat()
    {
        string html = await RenderAsync(Chat("whatsapp", "2348012345004", TimeSpan.FromHours(30)));

        Assert.Contains("Reply window closed", html);
        Assert.Contains("Continue this conversation in Kunle&#x27;s WhatsApp DM.", html);
        Assert.Contains("href=\"https://wa.me/2348012345004\"", html);
        Assert.DoesNotContain("<textarea", html);
        Assert.DoesNotContain("Send", html);
    }

    [Fact]
    public async Task AClosedMessengerWindowLinksToThePagesInbox()
    {
        string html = await RenderAsync(Chat("facebook", "psid", TimeSpan.FromHours(25)));

        Assert.Contains("href=\"https://business.facebook.com/latest/inbox/messenger?asset_id=100000000000001\"", html);
        Assert.DoesNotContain("<textarea", html);
    }

    [Fact]
    public async Task AnOpenWindowKeepsTheReplyBox()
    {
        string html = await RenderAsync(Chat("whatsapp", "2348012345004", TimeSpan.FromHours(2)));

        Assert.Contains("<textarea", html);
        Assert.DoesNotContain("Reply window closed", html);
    }

    [Fact]
    public async Task ATikTokWindowIs48Hours()
    {
        Assert.Contains("<textarea", await RenderAsync(Chat("tiktok", "open-id", TimeSpan.FromHours(30), windowHours: 48)));
        Assert.Contains("Reply window closed", await RenderAsync(Chat("tiktok", "open-id", TimeSpan.FromHours(50), windowHours: 48)));
    }

    [Fact]
    public async Task TheNoteBoxIsStillThereWhenTheWindowIsClosed()
    {
        string html = await RenderAsync(Chat("whatsapp", "2348012345004", TimeSpan.FromHours(30)), ComposerMode.Note);

        Assert.Contains("<textarea", html);
        Assert.DoesNotContain("Reply window closed", html);
        Assert.Contains("never sent to the customer", html);
    }
}
