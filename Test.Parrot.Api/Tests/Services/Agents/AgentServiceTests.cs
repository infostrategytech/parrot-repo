using Microsoft.Extensions.Options;
using Moq;
using Parrot.Application.Agents;
using Parrot.Application.DTOs.Agents;
using Parrot.Application.Integrations;
using Parrot.Application.Logging;
using Parrot.Application.Messaging;
using Parrot.Domain.Entities;
using Parrot.Domain.Exceptions;

namespace Test.Parrot.Api.Tests.Services.Agents;

public class AgentServiceTests
{
    private const string Owner = "owner-1";

    private readonly Mock<IAgentRepository> _repository = new();
    private readonly Mock<IMessageBus> _bus = new();
    private readonly List<AgentConfigChangedEvent> _announced = [];
    private readonly AgentService _sut;

    public AgentServiceTests()
    {
        _bus.Setup(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<AgentConfigChangedEvent>(), It.IsAny<CancellationToken>()))
            .Callback<string, string?, AgentConfigChangedEvent, CancellationToken>((_, _, evt, _) => _announced.Add(evt))
            .Returns(Task.CompletedTask);
        _sut = new AgentService(_repository.Object, _bus.Object, Options.Create(new KafkaSettings()), new Mock<IAppLogger<AgentService>>().Object);
    }

    private static Agent Existing(string owner = Owner, bool draft = false, bool active = true, DateTime? deletedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = owner,
        Name = "Nova",
        Description = "Answers customer questions.",
        MascotUrl = "/assets/images/avatars/avatar-1.png",
        Channels = ["whatsapp"],
        SettingsJson = AgentSettingsJson.Write(new AgentSettingsDto { Tone = "Upbeat" }),
        IsDraft = draft,
        IsActive = active,
        Version = 3,
        DeletedAt = deletedAt,
    };

    private static AgentInfo Info(string name = "Atlas") => new()
    {
        Name = name,
        Description = "Helps customers buy.",
        MascotUrl = "/assets/images/avatars/avatar-2.png",
        SupportsHumanTakeover = true,
        Channels = ["WhatsApp", "instagram ", "whatsapp"],
        Settings = new AgentSettingsDto { Role = "Sales", HandOverBigOrders = true, BigOrderNaira = 80000 },
    };

    private void Holds(Agent agent) => _repository.Setup(r => r.GetByIdAsync(agent.Id)).ReturnsAsync(agent);

    [Fact]
    public async Task CreateAsync_AsDraft_IsSavedOffAndAnnouncedNotLive()
    {
        AgentInfo request = Info();
        request.IsDraft = true;
        Agent? added = null;
        _repository.Setup(r => r.AddAsync(It.IsAny<Agent>())).Callback<Agent>(a => added = a).Returns(Task.CompletedTask);

        AgentResponse response = await _sut.CreateAsync(Owner, request);

        Assert.NotNull(added);
        Assert.True(added!.IsDraft);
        Assert.False(added.IsActive);
        Assert.Equal(["whatsapp", "instagram"], added.Channels);
        Assert.True(response.IsDraft);
        Assert.Equal("Sales", response.Settings.Role);
        Assert.Equal(1, response.Version);
        AgentConfigChangedEvent announced = Assert.Single(_announced);
        Assert.Equal(Owner, announced.TenantId);
        Assert.False(announced.Payload.IsLive);
        Assert.Equal(added.Id.ToString(), announced.Payload.AgentId);
    }

    [Fact]
    public async Task CreateAsync_WithNoName_ThrowsValidationAndSavesNothing()
    {
        await Assert.ThrowsAsync<ValidationException>(() => _sut.CreateAsync(Owner, Info(name: "  ")));

        _repository.Verify(r => r.AddAsync(It.IsAny<Agent>()), Times.Never);
        Assert.Empty(_announced);
    }

    [Fact]
    public async Task UpdateAsync_WhenOwned_SavesTheChangesBumpsTheVersionAndAnnounces()
    {
        Agent agent = Existing();
        Holds(agent);

        AgentResponse response = await _sut.UpdateAsync(Owner, agent.Id, Info());

        _repository.Verify(r => r.UpdateAsync(agent), Times.Once);
        Assert.Equal("Atlas", agent.Name);
        Assert.Equal(["whatsapp", "instagram"], agent.Channels);
        Assert.Equal(4, agent.Version);
        Assert.NotNull(agent.UpdatedAt);
        Assert.Equal(80000, response.Settings.BigOrderNaira);
        // Saving settings never switches an agent.
        Assert.True(agent.IsActive);
        Assert.False(agent.IsDraft);
        AgentConfigChangedEvent announced = Assert.Single(_announced);
        Assert.Equal(4, announced.Payload.Version);
        Assert.Equal(["whatsapp", "instagram"], announced.Payload.Channels);
        Assert.True(announced.Payload.IsLive);
        Assert.Equal(AgentConfigChangedEvent.EventType, announced.Type);
        _bus.Verify(b => b.PublishAsync("agent.events", Owner, It.IsAny<AgentConfigChangedEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateAsync_OnADraft_KeepsItADraft()
    {
        Agent agent = Existing(draft: true, active: false);
        Holds(agent);

        await _sut.UpdateAsync(Owner, agent.Id, Info());

        Assert.True(agent.IsDraft);
        Assert.False(agent.IsActive);
        Assert.False(Assert.Single(_announced).Payload.IsLive);
    }

    [Fact]
    public async Task UpdateAsync_WhenAnotherOwners_ThrowsForbiddenAndChangesNothing()
    {
        Agent agent = Existing(owner: "someone-else");
        Holds(agent);

        await Assert.ThrowsAsync<ForbiddenException>(() => _sut.UpdateAsync(Owner, agent.Id, Info()));

        _repository.Verify(r => r.UpdateAsync(It.IsAny<Agent>()), Times.Never);
        Assert.Equal("Nova", agent.Name);
        Assert.Empty(_announced);
    }

    [Fact]
    public async Task UpdateAsync_WhenUnknown_ThrowsNotFound()
    {
        _repository.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync((Agent?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.UpdateAsync(Owner, Guid.NewGuid(), Info()));
    }

    [Fact]
    public async Task UpdateAsync_WhenDeleted_ThrowsNotFound()
    {
        Agent agent = Existing(deletedAt: DateTime.UtcNow);
        Holds(agent);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.UpdateAsync(Owner, agent.Id, Info()));
    }

    [Fact]
    public async Task UpdateAsync_WithAChannelThatIsNotAKey_ThrowsValidation()
    {
        Agent agent = Existing();
        Holds(agent);
        AgentInfo request = Info();
        request.Channels = ["whats app"];

        await Assert.ThrowsAsync<ValidationException>(() => _sut.UpdateAsync(Owner, agent.Id, request));

        _repository.Verify(r => r.UpdateAsync(It.IsAny<Agent>()), Times.Never);
    }

    [Fact]
    public async Task SetLiveAsync_OnADraft_TurnsItOnAndEndsTheDraft()
    {
        Agent agent = Existing(draft: true, active: false);
        Holds(agent);

        AgentResponse response = await _sut.SetLiveAsync(Owner, agent.Id, on: true);

        Assert.True(agent.IsActive);
        Assert.False(agent.IsDraft);
        Assert.Equal(4, agent.Version);
        Assert.False(response.IsDraft);
        Assert.True(Assert.Single(_announced).Payload.IsLive);
        _repository.Verify(r => r.UpdateAsync(agent), Times.Once);
    }

    [Fact]
    public async Task SetLiveAsync_Off_AnnouncesItNoLongerAnswers()
    {
        Agent agent = Existing();
        Holds(agent);

        await _sut.SetLiveAsync(Owner, agent.Id, on: false);

        Assert.False(agent.IsActive);
        Assert.False(Assert.Single(_announced).Payload.IsLive);
    }

    [Fact]
    public async Task DeleteAsync_MarksTheRowDeletedAndAnnouncesIt()
    {
        Agent agent = Existing();
        Holds(agent);

        await _sut.DeleteAsync(Owner, agent.Id);

        Assert.NotNull(agent.DeletedAt);
        Assert.False(agent.IsActive);
        _repository.Verify(r => r.UpdateAsync(agent), Times.Once);
        AgentConfigChangedEvent announced = Assert.Single(_announced);
        Assert.True(announced.Payload.Deleted);
        Assert.False(announced.Payload.IsLive);
    }

    [Fact]
    public async Task DeleteAsync_WhenAnotherOwners_ThrowsForbidden()
    {
        Agent agent = Existing(owner: "someone-else");
        Holds(agent);

        await Assert.ThrowsAsync<ForbiddenException>(() => _sut.DeleteAsync(Owner, agent.Id));

        Assert.Null(agent.DeletedAt);
    }

    [Fact]
    public async Task ResolveForChannelAsync_WhenALiveAgentAnswers_ReturnsItWithItsSettings()
    {
        Agent agent = Existing();
        _repository.Setup(r => r.GetLiveForChannelAsync(Owner, "whatsapp")).ReturnsAsync(agent);

        AgentResponse? resolved = await _sut.ResolveForChannelAsync(Owner, " WhatsApp ");

        Assert.NotNull(resolved);
        Assert.Equal(agent.Id, resolved!.Id);
        Assert.Equal("Upbeat", resolved.Settings.Tone);
        Assert.Equal(["whatsapp"], resolved.Channels);
    }

    [Fact]
    public async Task ResolveForChannelAsync_WhenNoneAnswers_ReturnsNull()
    {
        _repository.Setup(r => r.GetLiveForChannelAsync(Owner, "facebook")).ReturnsAsync((Agent?)null);

        Assert.Null(await _sut.ResolveForChannelAsync(Owner, "facebook"));
    }

    [Fact]
    public async Task ResolveForChannelAsync_WithABlankChannel_ReturnsNullWithoutReading()
    {
        Assert.Null(await _sut.ResolveForChannelAsync(Owner, "  "));

        _repository.Verify(r => r.GetLiveForChannelAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // The row is the record; the announcement only shortens the orchestrator's cache life.
    [Fact]
    public async Task UpdateAsync_WhenTheBusIsDown_StillSaves()
    {
        Agent agent = Existing();
        Holds(agent);
        _bus.Setup(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<AgentConfigChangedEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Kafka is down"));

        AgentResponse response = await _sut.UpdateAsync(Owner, agent.Id, Info());

        Assert.Equal("Atlas", response.Name);
        _repository.Verify(r => r.UpdateAsync(agent), Times.Once);
    }

    [Fact]
    public async Task GetAllAsync_ReadsTheSettingsFromTheColumn()
    {
        Agent broken = Existing();
        broken.SettingsJson = "not json";
        _repository.Setup(r => r.GetByWorkspaceAsync(Owner)).ReturnsAsync([Existing(), broken]);

        IReadOnlyList<AgentResponse> agents = await _sut.GetAllAsync(Owner);

        Assert.Equal("Upbeat", agents[0].Settings.Tone);
        // A column that is not the shape reads as the defaults, not as a failed page.
        Assert.Equal("Warm", agents[1].Settings.Tone);
    }
}
