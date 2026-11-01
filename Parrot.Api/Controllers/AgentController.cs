using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Api.Monitoring;
using Parrot.Api.Security;
using Parrot.Application.Agents;
using Parrot.Application.DTOs.Agents;
using Parrot.Application.Teams;
using Parrot.Domain.Exceptions;

namespace Parrot.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[Monitor(Area = "agents")]
public class AgentController : ControllerBase
{
    private readonly IAgentService _agentService;

    public AgentController(IAgentService agentService)
    {
        _agentService = agentService;
    }

    // Reading the agents (the two GETs): anyone who builds them, works the
    // conversations they answer, or reads their figures. Channel and catalog
    // managers have no use for them.

    [HttpPost]
    [RequirePermission(WorkspacePermissions.AgentsCreate)]
    public async Task<IActionResult> CreateAgent([FromBody] AgentInfo agentInfo)
    {
        RequireChannels(agentInfo.Channels);
        string userId = Owner();
        AgentResponse agent = await _agentService.CreateAsync(userId, agentInfo);
        return CreatedAtAction(nameof(GetAgent), new { id = agent.Id }, agent);
    }

    [HttpGet]
    [RequireAnyPermission(WorkspacePermissions.AgentsCreate, WorkspacePermissions.AgentsEdit,
        WorkspacePermissions.PlaybooksCreate, WorkspacePermissions.PlaybooksEdit,
        WorkspacePermissions.PlaybooksDelete, WorkspacePermissions.PlaybooksPublish,
        WorkspacePermissions.ConversationsView, WorkspacePermissions.ConversationsTakeOver,
        WorkspacePermissions.AnalyticsDashboard)]
    public async Task<IActionResult> GetAgents()
    {
        string userId = Owner();
        IReadOnlyList<AgentResponse> agents = await _agentService.GetAllAsync(userId);
        return Ok(agents.Where(Reaches).ToList());
    }

    [HttpGet("{id:guid}")]
    [RequireAnyPermission(WorkspacePermissions.AgentsCreate, WorkspacePermissions.AgentsEdit,
        WorkspacePermissions.PlaybooksCreate, WorkspacePermissions.PlaybooksEdit,
        WorkspacePermissions.PlaybooksDelete, WorkspacePermissions.PlaybooksPublish,
        WorkspacePermissions.ConversationsView, WorkspacePermissions.ConversationsTakeOver,
        WorkspacePermissions.AnalyticsDashboard)]
    public async Task<IActionResult> GetAgent(Guid id) => Ok(await ReachableAgentAsync(id));

    /// <summary>Saves everything about the agent except whether it is on; that is the live switch below.</summary>
    [HttpPut("{id:guid}")]
    [RequirePermission(WorkspacePermissions.AgentsEdit)]
    public async Task<IActionResult> UpdateAgent(Guid id, [FromBody] AgentInfo agentInfo)
    {
        await ReachableAgentAsync(id);
        RequireChannels(agentInfo.Channels);
        string userId = Owner();
        AgentResponse agent = await _agentService.UpdateAsync(userId, id, agentInfo);
        return Ok(agent);
    }

    /// <summary>Turns the agent on or off. A draft turned on starts answering on its channels.</summary>
    [HttpPatch("{id:guid}/live")]
    [RequirePermission(WorkspacePermissions.PlaybooksPublish)]
    public async Task<IActionResult> SetLive(Guid id, [FromBody] AgentLiveRequest request)
    {
        await ReachableAgentAsync(id);
        string userId = Owner();
        AgentResponse agent = await _agentService.SetLiveAsync(userId, id, request.On);
        return Ok(agent);
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(WorkspacePermissions.PlaybooksDelete)]
    public async Task<IActionResult> DeleteAgent(Guid id)
    {
        await ReachableAgentAsync(id);
        string userId = Owner();
        await _agentService.DeleteAsync(userId, id);
        return NoContent();
    }

    // A team member limited to some channels sees the agents that answer on one
    // of them; one with no channels is everyone else's concern.
    private bool Reaches(AgentResponse agent)
    {
        IReadOnlySet<string> channels = HttpContext.WorkspaceAccess().Channels;
        return channels.Count == 0 || agent.Channels.Any(channels.Contains);
    }

    private async Task<AgentResponse> ReachableAgentAsync(Guid id)
    {
        AgentResponse agent = await _agentService.GetByIdAsync(Owner(), id);
        return Reaches(agent) ? agent : throw new NotFoundException("Agent not found.");
    }

    // Nor can they put an agent on a channel their access does not include.
    private void RequireChannels(IEnumerable<string>? requested)
    {
        IReadOnlySet<string> channels = HttpContext.WorkspaceAccess().Channels;
        if (channels.Count > 0 && (requested ?? []).Any(c => !channels.Contains(c)))
        {
            throw new ForbiddenException("Your access in this workspace does not include all of those channels.");
        }
    }

    private string Owner() =>
        User.WorkspaceId()
            ?? throw new InvalidOperationException("User ID claim not found.");
}
