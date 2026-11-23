using System.Text.Json;
using Parrot.Web.Components.Pages.Dashboard.Conversations;
using Parrot.Web.Features.Dashboard.TaskRequests;
using Parrot.Web.Services;

internal static class InboxAttentionChecks
{
    public static int Run()
    {
        var checks=0;
        void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
        const string me="person-me",other="person-other";
        var now=DateTime.UtcNow;
        var item=new ConversationItem{Id="chat",Status=ConversationStatus.Open,AiState=AiState.Paused,Assignee=new(me,"Me"),UnreadCount=5,Priority=ConversationPriority.Urgent};
        InboxNeed Need(string person=me,IEnumerable<TaskQuestion>? questions=null)=>InboxAttentionRules.Reason(item,person,questions??[]);
        Check(Need()==InboxNeed.None,"Assigned, unread, urgent or paused alone is not a conversation-input obligation.");
        item.Assignee=null;Check(Need()==InboxNeed.None,"Unassigned conversations are not broadcast to every teammate.");
        item.NeedsAnswer=true;Check(Need()==InboxNeed.None,"Unknown recipient is not guessed from a legacy flag.");
        Check(InboxAttentionRules.Reason(item,me,[],answersAgents:true)==InboxNeed.AgentAnswer,"Until chats can be assigned, an AI question waits for whoever can answer it.");
        item.Assignee=new(me,"Me");Check(Need()==InboxNeed.AgentAnswer,"Legacy explicitly assigned AI questions remain readable.");
        item.Attention=new(other);Check(Need()==InboxNeed.None,"Canonical recipient takes precedence over a stale assignee.");
        item.Attention=new(me);Check(Need()==InboxNeed.AgentAnswer&&Need(other)==InboxNeed.None,"AI questions belong only to their recipient.");
        item.NeedsAnswer=false;item.PendingApproval=new("approval",null,null);
        Check(Need()==InboxNeed.Approval&&Need(other)==InboxNeed.None,"Approvals follow explicit responsibility.");
        item.PendingApproval=null;item.Attention=new(me,true,"handoff-task");
        Check(Need()==InboxNeed.CustomerReply,"A routed handoff waits for a customer-facing response.");
        item.AiState=AiState.Active;Check(Need()==InboxNeed.None,"Returning to AI removes human reply need even before a stale snapshot catches up.");
        item.AiState=AiState.Handoff;item.Status=ConversationStatus.Resolved;Check(Need()==InboxNeed.None,"Resolved chats do not remain a reply queue.");
        item.Status=ConversationStatus.Open;item.Attention=new(me,false,"handoff-task");Check(Need()==InboxNeed.None,"An open linked handoff task does not keep an answered conversation in Needs you.");
        item.Attention=new(other,false);
        var asked=new TaskQuestion("q","task","chat","A conversation question",new(other,"Other"),new(me,"Me"),1,now);
        Check(Need(questions:[asked])==InboxNeed.TeammateAnswer,"An explicit teammate question can need you without owning the parent task.");
        Check(Need(other,[asked])==InboxNeed.None,"A requester is not waiting on their own unanswered question.");
        var returned=asked with{Answer="Approved information"};
        Check(Need(other,[returned])==InboxNeed.ReplyReady&&Need(questions:[returned])==InboxNeed.None,"A returned answer belongs to its requester.");
        Check(Need(other,[returned with{Closed=true}])==InboxNeed.None,"Used/closed teammate answers leave the input queue.");
        Check(Need(questions:[asked with{ConversationId="different"}])==InboxNeed.None,"Question context cannot leak to another chat.");
        Check(Need("")==InboxNeed.None,"No authenticated identity means no guessed personal assignment.");
        item.Attention=new(me,true);item.LastCustomerMessageAt=now;
        InboxMessageDto Message(string id,string kind,string status,DateTime? at=null)=>new(id,"Controlled content",kind=="human",status,at??now.AddSeconds(1)){Kind=kind};
        foreach(var status in new[]{"failed","unknown","pending"}){ConversationsState.SettleReplyNeed(item,Message(status,"human",status));Check(item.Attention.ReplyNeeded,$"{status} send must keep the reply obligation.");}
        ConversationsState.SettleReplyNeed(item,Message("note","note","sent"));Check(item.Attention.ReplyNeeded,"An internal note is not a customer reply.");
        ConversationsState.SettleReplyNeed(item,Message("old","human","sent",now.AddMinutes(-1)));Check(item.Attention.ReplyNeeded,"An old reply cannot settle newer inbound messages.");
        ConversationsState.Merge(item,[Message("historical","human","sent")]);Check(item.Attention.ReplyNeeded,"A history read cannot override a newer authoritative handoff snapshot.");
        var task=new TaskRequest{Id="still-open",Kind=TaskKind.Order,Title="Confirm a payment",RaisedBy="Agent",RaisedAt=now,Urgent=true};
        ConversationsState.SettleReplyNeed(item,Message("confirmed","human","sent"));
        Check(!item.Attention.ReplyNeeded&&Need()==InboxNeed.None,"A confirmed customer-facing reply clears only conversation input.");
        Check(task.State==TaskState.Open&&task.Urgent,"Replying does not complete or change the urgent operational task.");
        var state=new ConversationsState();var connection=Guid.NewGuid();
        var legacy=JsonSerializer.Deserialize<InboxConversationDto>($$"""{"connectionId":"{{connection}}","sender":"customer","name":"Name","preview":"Text","updatedAt":"{{now:O}}"}""",new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        state.Apply([legacy]);Check(state.Conversations.Single().Attention is null,"Older API JSON remains compatible without invented attention state.");
        state.Apply([legacy with{Attention=new(me,true,"task")}]);Check(state.Conversations.Single().Attention?.ReplyNeeded==true,"Canonical attention survives the normal summary boundary.");
        state.Apply([legacy]);Check(state.Conversations.Single().Attention is null,"An absent authoritative snapshot cannot leave stale attention behind.");
        return checks;
    }
}
