namespace Parrot.Web.Components.Pages.Dashboard.Conversations;

/// <summary>
/// The agents answering on a conversation's number, named together as the screens say it: "Tolu", "Tobi and Kora",
/// "Nia and 2 others".
/// </summary>
public static class AgentTeam
{
    public static string Names(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "the AI agent",
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => $"{names[0]} and {names.Count - 1} others",
    };
}
