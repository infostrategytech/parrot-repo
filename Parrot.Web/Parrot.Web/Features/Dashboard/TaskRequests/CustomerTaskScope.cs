namespace Parrot.Web.Features.Dashboard.TaskRequests;

/// <summary>Links the existing task list to the same conversation set shown by Customer details.</summary>
public static class CustomerTaskScope
{
    public static string Url(string name, IEnumerable<string> conversationIds)
    {
        var ids = conversationIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        if (ids.Count == 0) return "/dashboard/tasks";
        return "/dashboard/tasks?customer=" + Uri.EscapeDataString(name) + "&" +
            string.Join("&", ids.Select(id => "conversation=" + Uri.EscapeDataString(id)));
    }
}
