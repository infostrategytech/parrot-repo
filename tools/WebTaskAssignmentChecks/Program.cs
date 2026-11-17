using System.Reflection;
using Parrot.SharedComponents.Services;
using Parrot.Web.Components.Pages.Dashboard.TaskRequests;
using Parrot.Web.Features.Dashboard.TaskRequests;
using TaskPage=Parrot.Web.Components.Pages.Dashboard.TaskRequests.TaskRequests;

// Controlled contracts/components only. No demo records or live API.
var count=0;void Check(bool value,string why){if(!value)throw new Exception(why);count++;}
const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
void Set(object target,string name,object? value)=>target.GetType().GetProperty(name,flags)!.SetValue(target,value);
void Field(object target,string name,object? value)=>target.GetType().GetField(name,flags)!.SetValue(target,value);
T Read<T>(object target,string name)=>(T)target.GetType().GetField(name,flags)!.GetValue(target)!;
async Task Call(object target,string name,params object?[] args){
 var result=target.GetType().GetMethod(name,flags)!.Invoke(target,args);
 if(result is Task pending)await pending;
}
var me=new TaskPerson("viewer","Alex");var other=new TaskPerson("other","Dara");var third=new TaskPerson("third","Sam");
var store=new ControlledStore(me,other,third);var questions=new ControlledQuestions();using var toast=new ToastService();
questions.People=[new(other,"Support",true),new(other,"Repeated",true)];
var people=await TaskAssignmentRules.PeopleAsync(store,questions,store.Item.Id);
Check(people.Count==1&&people[0].Role=="Support"&&store.RosterReads==0,"Both entry points use the same deduplicated permission-aware list.");
questions.People=[];Check((await TaskAssignmentRules.PeopleAsync(store,questions,store.Item.Id)).Count==0&&store.RosterReads==0,"No eligible people must not fall back to an unrestricted roster.");
questions.Available=false;Check((await TaskAssignmentRules.PeopleAsync(store,questions,store.Item.Id)).Count==3,"Current real-mode fallback uses the existing Tasks team contract.");
object Bind(object component){Set(component,"Store",store);Set(component,"Toasts",toast);return component;}
// The table's menu saves at once, with Undo; it never opens a window (designer, 17 November 2026).
var page=(TaskPage)Bind(new TaskPage());
await Call(page,"AssignAsync",store.Item,other);
Check(store.Assignments==1&&store.Item.Assignee?.Id==other.Id&&toast.Current?.ActionLabel=="Undo","Table reassignment saves at once, with Undo.");
Check(page.GetType().GetField("_passingTaskId",flags) is null,"The table has no handover window to open.");
Check(await TaskAssignmentRules.TryUndoAsync(store,store.Item.Id,other,me)&&store.Item.Assignee?.Id==me.Id,"Table Undo restores the previous assignee.");
// The details: Take it for an unassigned task; Pass on for everything else.
var panel=(TaskPanel)Bind(new TaskPanel());
store.Item.Assignee=null;await Call(panel,"AssignAsync",store.Item,me);
Check(store.Item.Assignee?.Id==me.Id&&toast.Current?.ActionLabel=="Undo","Take it stays one click, with Undo.");
await Call(panel,"OpenPassOn");Check(Read<bool>(panel,"_passing"),"Pass on is the details' way to hand over.");
PassOnDialog Dialog(){
 var dialog=(PassOnDialog)Bind(new PassOnDialog());Set(dialog,"Questions",questions);Set(dialog,"Item",store.Item);
 Set(dialog,"Open",true);return dialog;
}
var pass=Dialog();await Call(pass,"OnParametersSetAsync");
Check(Read<string>(pass,"_person") is null&&Read<object>(pass,"_mode").ToString()=="Task","Pass on starts on passing the task, with the teammate chosen once, inside it.");
Field(pass,"_person",other.Id);Field(pass,"_text","Please confirm the delivery details.");await Call(pass,"SaveAsync");
Check(store.Item.Assignee?.Id==other.Id&&store.Notes.SequenceEqual(["Please confirm the delivery details."]),"Pass on saves the note and changes ownership through the existing contracts.");
Check(store.Item.State==TaskState.InProgress&&store.Item.Total==123m&&store.Item.Customer?.ConversationId=="controlled-chat","Reassignment preserves task state, money and conversation.");
Check(toast.Current?.ActionLabel=="Undo","The handover offers Undo.");
Check(await TaskAssignmentRules.TryUndoAsync(store,store.Item.Id,other,me)&&store.Item.Assignee?.Id==me.Id,"Undo restores the previous assignee.");
var before=store.Assignments;Check(!await TaskAssignmentRules.TryUndoAsync(store,store.Item.Id,other,third)&&store.Assignments==before,"A stale Undo does not overwrite newer ownership.");
var retry=Dialog();await Call(retry,"OnParametersSetAsync");Field(retry,"_person",other.Id);Field(retry,"_text","Already-saved note");store.FailAssignments=1;
await Call(retry,"SaveAsync");
Check(Read<string>(retry,"_error").Contains("note was saved")&&store.Item.Assignee?.Id==me.Id,"A partial note/assignment failure is reported honestly.");
await Call(retry,"SaveAsync");Check(store.Notes.Count(n=>n=="Already-saved note")==1&&store.Item.Assignee?.Id==other.Id,"Retrying assignment does not duplicate the saved note.");
store.Item.Assignee=me;var stale=Dialog();await Call(stale,"OnParametersSetAsync");Field(stale,"_person",other.Id);store.Item.Assignee=third;before=store.Assignments;
await Call(stale,"SaveAsync");Check(store.Assignments==before&&Read<string>(stale,"_error").Contains("assignee changed"),"Changed ownership is reviewed instead of overwritten.");
store.Item.Assignee=me;questions.Available=true;questions.People=[new(other,"Support",true),new(third,"Delivery",true)];
var removed=Dialog();await Call(removed,"OnParametersSetAsync");Field(removed,"_person",other.Id);questions.People=[new(third,"Delivery",true)];before=store.Assignments;
await Call(removed,"SaveAsync");Check(store.Assignments==before&&Read<string>(removed,"_error").Contains("no longer available"),"A removed/ineligible recipient is rechecked at submit.");
questions.People=[new(other,"Support",true)];var ask=Dialog();await Call(ask,"OnParametersSetAsync");
var modeType=typeof(PassOnDialog).GetNestedType("PassOnMode",BindingFlags.NonPublic)!;
await Call(ask,"ChooseMode",Enum.Parse(modeType,"Ask"));Field(ask,"_person",other.Id);Field(ask,"_text","Is this delivery time available?");before=store.Assignments;
await Call(ask,"SaveAsync");Check(questions.Asked==1&&store.Assignments==before&&store.Item.Assignee?.Id==me.Id,"Asking remains distinct and never transfers ownership.");
Console.WriteLine($"{count} controlled task-assignment checks passed.");

sealed class ControlledQuestions:ITaskQuestions{
 public bool Available{get;set;}=true;public int Asked;public IReadOnlyList<QuestionPerson> People{get;set;}=[];
 public event Action? Changed{add{}remove{}}
 public Task<IReadOnlyList<TaskQuestion>> ListAsync(CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<TaskQuestion>>([]);
 public Task<IReadOnlyList<QuestionPerson>> PeopleAsync(string id,CancellationToken ct=default)=>Task.FromResult(People);
 public Task<TaskQuestion> ApplyAsync(QuestionCommand command,CancellationToken ct=default){Asked++;return Task.FromResult(new TaskQuestion("q",command.TaskId,"controlled-chat",command.Text!,new("viewer","Alex"),new(command.PersonId!,"Dara"),1,DateTime.UtcNow));}
}
sealed class ControlledStore(TaskPerson me,TaskPerson other,TaskPerson third):ITaskStore{
 public bool Available=>true;public TaskPerson Me=>me;public int Assignments,RosterReads,FailAssignments;public List<string> Notes=[];
 public TaskRequest Item{get;}=new(){Id="controlled-task",Kind=TaskKind.Order,Title="A controlled order",RaisedBy="Agent",RaisedAt=DateTime.UtcNow,Assignee=me,State=TaskState.InProgress,Total=123m,Customer=new("Customer","whatsapp",ConversationId:"controlled-chat")};
 public event Action? Changed{add{}remove{}}
 public Task<IReadOnlyList<TaskRequest>> ListAsync(CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<TaskRequest>>([Item]);
 public Task<IReadOnlyList<TaskPerson>> TeamAsync(CancellationToken ct=default){RosterReads++;return Task.FromResult<IReadOnlyList<TaskPerson>>([me,other,third]);}
 public Task AssignAsync(string id,TaskPerson? person,CancellationToken ct=default){if(FailAssignments-->0)throw new HttpRequestException("Controlled failure");Assignments++;Item.Assignee=person;return Task.CompletedTask;}
 public Task PostUpdateAsync(string id,string text,CancellationToken ct=default){Notes.Add(text);return Task.CompletedTask;}
 public Task<IReadOnlyList<TaskCustomer>> CustomersAsync(CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<TaskCustomer>>([]);
 public Task<IReadOnlyList<TaskItem>> ItemsAsync(CancellationToken ct=default)=>Task.FromResult<IReadOnlyList<TaskItem>>([]);
 public Task<TaskRequest> CreateAsync(TaskDraft draft,CancellationToken ct=default)=>throw new Exception("Unexpected create");
 public Task EditAsync(string id,TaskDraft draft,CancellationToken ct=default)=>throw new Exception("Unexpected edit");
 public Task SetStateAsync(string id,TaskState state,CancellationToken ct=default)=>throw new Exception("Unexpected state change");
 public Task SetUrgentAsync(string id,bool urgent,CancellationToken ct=default)=>throw new Exception("Unexpected priority change");
 public Task SetDueAsync(string id,DateTime? at,bool hasTime,CancellationToken ct=default)=>throw new Exception("Unexpected due-date change");
}
