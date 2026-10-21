using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Parrot.Application.Agents;
using Parrot.Application.DTOs.Agents;
using Parrot.Api.Monitoring;
using Parrot.Api.Security;

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

    [HttpPost]
    public async Task<IActionResult> CreateAgent([FromBody] AgentInfo agentInfo)
    {
        string userId = Owner();
        AgentResponse agent = await _agentService.CreateAsync(userId, agentInfo);
        return CreatedAtAction(nameof(GetAgent), new { id = agent.Id }, agent);
    }

    [HttpGet]
    public async Task<IActionResult> GetAgents()
    {
        string userId = Owner();
        IReadOnlyList<AgentResponse> agents = await _agentService.GetAllAsync(userId);
        return Ok(agents);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetAgent(Guid id)
    {
        string userId = Owner();
        AgentResponse agent = await _agentService.GetByIdAsync(userId, id);
        return Ok(agent);
    }

    /// <summary>Saves everything about the agent except whether it is on; that is the live switch below.</summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateAgent(Guid id, [FromBody] AgentInfo agentInfo)
    {
        string userId = Owner();
        AgentResponse agent = await _agentService.UpdateAsync(userId, id, agentInfo);
        return Ok(agent);
    }

    /// <summary>Turns the agent on or off. A draft turned on starts answering on its channels.</summary>
    [HttpPatch("{id:guid}/live")]
    public async Task<IActionResult> SetLive(Guid id, [FromBody] AgentLiveRequest request)
    {
        string userId = Owner();
        AgentResponse agent = await _agentService.SetLiveAsync(userId, id, request.On);
        return Ok(agent);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteAgent(Guid id)
    {
        string userId = Owner();
        await _agentService.DeleteAsync(userId, id);
        return NoContent();
    }

    private string Owner() =>
        User.WorkspaceId()
            ?? throw new InvalidOperationException("User ID claim not found.");
}
