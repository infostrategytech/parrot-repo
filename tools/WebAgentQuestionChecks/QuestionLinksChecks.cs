using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Parrot.Web.Components.Pages.Dashboard.AiAgents;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Features.Dashboard.AiAgents;

static class QuestionLinksChecks
{
    public static async Task<int> RunAsync()
    {
        var checks=0;void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
        var kora=new AiAgent{Id="agent-kora",Name="Kora",IsActive=false};
        var duplicate=new AiAgent{Id="agent-other",Name="Kora"};
        var question=new WaitingQuestion("Kora","Customer","A question") {AgentId=kora.Id,ConversationId="connection:customer",MessageId="question & 1"};
        Check(InboxQuestionLinks.BelongsTo(question,kora,[kora]),"A paused agent's pending questions remain associated.");
        Check(!InboxQuestionLinks.BelongsTo(question,duplicate,[kora,duplicate]),"Stable agent identity wins over matching names.");
        var legacy=new WaitingQuestion("Kora","Customer","A question");
        Check(InboxQuestionLinks.BelongsTo(legacy,kora,[kora]),"Older API responses retain unique-name compatibility.");
        Check(!InboxQuestionLinks.BelongsTo(legacy,kora,[kora,duplicate]),"Ambiguous legacy names do not duplicate attribution.");
        var link=InboxQuestionLinks.Review([question],kora.Id);
        Check(link.Contains("connection%3Acustomer")&&link.Contains("message=question%20%26%201"),"A single question deep-links with encoded identities.");
        Check(link.Contains("questions=true")&&link.Contains("agent=agent-kora")&&link.Contains("view=needs-you"),"The deep link retains its question and agent filters.");
        Check(InboxQuestionLinks.Review([question,question],kora.Id)==InboxQuestionLinks.Queue(kora.Id),"Several questions open the agent's question queue, not a guessed first conversation.");
        Check(InboxQuestionLinks.Review([legacy])==InboxQuestionLinks.Queue(),"Missing conversation identities fall back to the Inbox queue.");
        var auth=new AuthState();auth.SetUser(new(){UserId="controlled-owner",Token="controlled",ExpiresAt=DateTime.UtcNow.AddHours(1)});
        var handler=new WaitingHandler();var feed=new ApiAiOperationsFeed(new HttpClient(handler){BaseAddress=new("http://controlled.test/")},auth,NullLogger<ApiAiOperationsFeed>.Instance);
        await feed.RefreshAsync();Check(feed.Waiting([]).Single().AgentId is null,"Legacy waiting JSON still loads without identities.");
        handler.Identified=true;await feed.RefreshAsync();var mapped=feed.Waiting([]).Single();
        Check(mapped.AgentId=="agent-kora"&&mapped.ConversationId=="connection:customer"&&mapped.MessageId=="question-1","Additive waiting identities map without backend DTO changes.");
        Check(handler.Paths.All(path=>path=="/api/ai-operations/waiting"),"The existing waiting endpoint is preserved.");
        return checks;
    }
    sealed class WaitingHandler:HttpMessageHandler
    {
        public bool Identified;public List<string> Paths=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            object[] payload=Identified?[new{AgentName="Kora",Customer="Customer",Question="A question",AgentId="agent-kora",ConversationId="connection:customer",MessageId="question-1"}]:[new{AgentName="Kora",Customer="Customer",Question="A question"}];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(payload)});
        }
    }
}
