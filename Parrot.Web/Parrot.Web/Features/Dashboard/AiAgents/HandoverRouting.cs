using Parrot.Web.Components.Pages.Dashboard.AiAgents;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Features.Dashboard.Team;
using Parrot.Web.Features.Dashboard.TaskRequests;
using Parrot.Web.Services;

namespace Parrot.Web.Features.Dashboard.AiAgents;

/// <summary>
/// Who an agent brings in (designer, 10 November 2026), now that a business can have a team:
/// <list type="bullet">
///   <item>Team: anyone on the team who can reply takes it in the Inbox; tasks it raises wait, unassigned, for anyone.</item>
///   <item>Role: shared evenly between everyone with that role (say Customer service).</item>
///   <item>People: shared evenly between the people chosen.</item>
/// </list>
/// Working alone, it is always the owner. Handovers and the tasks the agent raises for a person follow the same choice.
/// </summary>
public static class HandoverModes
{
    public const string Team = "team";
    public const string Role = "role";
    public const string People = "people";
}

/// <summary>Someone a handover can go to here: their role in this workspace and the channels they reply on (none: all).</summary>
public sealed record HandoverPerson(string Id, string Name, bool IsOwner, string? RoleId, string? RoleName, bool CanReply, IReadOnlyList<string> Channels)
{
    /// <summary>Replies on at least one of the agent's channels (with no channels of their own, on every channel).</summary>
    public bool Covers(IReadOnlyCollection<string> agentChannels) =>
        Channels.Count == 0 || agentChannels.Count == 0 || Channels.Any(c => agentChannels.Contains(c, StringComparer.OrdinalIgnoreCase));
}

public sealed record HandoverRole(string Id, string Name);

/// <summary>The people of this workspace, as the agent's settings see them.</summary>
public sealed record HandoverTeam(IReadOnlyList<HandoverPerson> People, bool Known = true)
{
    public static HandoverTeam Alone(string ownerId) => new([new HandoverPerson(ownerId, "You", true, null, null, true, [])]);

    /// <summary>The team can't be read here (someone other than the owner, or it failed): the saved words stand.</summary>
    public static HandoverTeam Unknown { get; } = new([], false);

    /// <summary>Working alone: nobody but the owner, so there is nothing to choose.</summary>
    public bool Solo => Known && People.All(person => person.IsOwner);

    /// <summary>Who can take this agent's customers: they can reply, on one of its channels.</summary>
    public IReadOnlyList<HandoverPerson> Eligible(IReadOnlyCollection<string> agentChannels) =>
        [.. People.Where(person => person.CanReply && person.Covers(agentChannels))];

    /// <summary>The roles at least one eligible teammate holds (the owner's isn't a role to share).</summary>
    public IReadOnlyList<HandoverRole> Roles(IReadOnlyCollection<string> agentChannels) =>
        [.. Eligible(agentChannels).Where(p => !p.IsOwner && p.RoleId is not null).GroupBy(p => p.RoleId!).Select(g => new HandoverRole(g.Key, g.First().RoleName ?? g.Key)).OrderBy(r => r.Name)];

    /// <summary>
    /// The team as the API gives it to the owner (members, their grants here, and what each role allows). Anyone else,
    /// or when it can't be read, gets an unknown team: the owner's choice still shows, as its words, never "you".
    /// </summary>
    public static async Task<HandoverTeam> LoadAsync(TeamClient team, AuthState auth)
    {
        var me = auth.CurrentUser;
        if (me is null || !me.IsOwner) return Unknown;
        try
        {
            var snapshot = await team.GetTeamAsync();
            var catalog = await team.GetCatalogAsync();
            var replies = catalog.Roles.Where(r => r.Permissions.Contains("conversations.reply")).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            var here = me.CurrentWorkspaceId;
            List<HandoverPerson> people = [];
            foreach (var member in snapshot.Members)
            {
                if (member.IsOwner) { people.Add(new(member.UserId, member.FullName ?? member.Email, true, null, null, true, [])); continue; }
                // Their access here: this workspace's own grant, else their all-workspaces one; none means not here.
                var grant = member.Grants.FirstOrDefault(g => g.WorkspaceId == here) ?? member.Grants.FirstOrDefault(g => g.WorkspaceId is null);
                if (grant is null) continue;
                people.Add(new(member.UserId, member.FullName ?? member.Email, false, grant.RoleId, TeamWords.RoleName(grant), replies.Contains(grant.RoleId), grant.Channels));
            }
            return people.Count == 0 ? Alone(me.UserId) : new(people);
        }
        catch (Exception exception) when (exception is HttpRequestException or ApiException or InvalidOperationException or TaskCanceledException)
        {
            return Unknown;
        }
    }
}

public static class HandoverRouting
{
    /// <summary>Who it brings in, to finish "To …" or "It passes customers to …". Working alone: you.</summary>
    public static string Phrase(AgentSettings settings, bool solo, HandoverTeam? team = null) =>
        solo ? "you, in the Inbox"
        // Chosen people, seen by the owner: "you" for themselves, though the AI's words carry their name.
        : settings.HandoverMode == HandoverModes.People && team is { Known: true }
            && Describe(HandoverModes.People, team, null, settings.HandoverPeople, onScreen: true) is { Length: > 0 } people ? people
        : settings.HandoverMode switch
        {
            HandoverModes.Role or HandoverModes.People when settings.HandoverTo.Trim().Length > 0 => settings.HandoverTo.Trim(),
            HandoverModes.Team => "anyone on your team",
            // Not chosen yet: an older agent's own words, else the default, anyone on the team.
            _ => settings.HandoverTo.Trim().Length > 0 ? settings.HandoverTo.Trim() : "anyone on your team",
        };

    /// <summary>
    /// The words kept with the agent for the AI and for anyone who can't read the team (see agents-backend-requests).
    /// The owner is named in them, because to the AI "you" is itself (designer, 12 November 2026); onScreen gives the
    /// owner's own view, where they are "you".
    /// </summary>
    public static string Describe(string mode, HandoverTeam team, string? roleId, IReadOnlyList<string> peopleIds, bool onScreen = false) => mode switch
    {
        HandoverModes.Role => team.People.FirstOrDefault(p => p.RoleId == roleId)?.RoleName is { } role ? $"{role}, shared evenly" : "",
        HandoverModes.People => Names(team.People.Where(p => peopleIds.Contains(p.Id)).Select(p => p.IsOwner && onScreen ? "you" : p.Name).ToList()) is { Length: > 0 } names
            ? peopleIds.Count > 1 ? $"{names}, shared evenly" : names : "",
        _ => "",
    };

    private static string Names(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    /// <summary>
    /// Who gets a handover, or a task the agent raises for a person. Alone: the owner. Anyone on the team: no one in
    /// particular (it waits for whoever is free). A role or people: shared evenly, to whoever of them has the fewest
    /// open tasks, the earliest in the list on a tie; on the conversation's channel when it's known. If none of them can
    /// take it (they left, or lost access), it waits for anyone rather than going nowhere.
    /// </summary>
    public static TaskPerson? Assign(AgentSettings settings, HandoverTeam team, IReadOnlyCollection<string> agentChannels,
        IReadOnlyDictionary<string, int> openTasks, string? channel = null)
    {
        var owner = team.People.FirstOrDefault(p => p.IsOwner);
        if (team.Solo) return owner is null ? null : new TaskPerson(owner.Id, owner.Name);
        var eligible = team.Eligible(channel is null ? agentChannels : [channel]);
        IEnumerable<HandoverPerson> chosen = settings.HandoverMode switch
        {
            HandoverModes.Role => eligible.Where(p => !p.IsOwner && p.RoleId == settings.HandoverRoleId),
            HandoverModes.People => eligible.Where(p => settings.HandoverPeople.Contains(p.Id)),
            _ => [],
        };
        var pick = chosen.Select((person, order) => (person, order))
            .OrderBy(x => openTasks.GetValueOrDefault(x.person.Id))
            .ThenBy(x => x.order)
            .Select(x => x.person)
            .FirstOrDefault();
        return pick is null ? null : new TaskPerson(pick.Id, pick.Name);
    }
}
