using System.Text.Json;
using Parrot.Application.Agents;
using Parrot.Web.Components.Pages.Dashboard.AiAgents;
using Parrot.Web.Services.Realtime;

namespace Parrot.Web.Features.Dashboard.AiAgents.State
{
    /// <summary>
    /// The agents the screens show, read once per session from
    /// <see cref="IAgentStore"/> and kept in step with every change saved
    /// through it. A change shows only once the store has accepted it, so a
    /// failed save never looks saved. The calls throw on failure; each screen
    /// says what failed and offers to try again (<see cref="IsFailure"/>).
    ///
    /// It also keeps the operations feed fresh: read once the agents are, and
    /// again each time an agent reports something over the messaging hub.
    /// <see cref="Changed"/> fires for both, so a screen draws again for either.
    /// </summary>
    public class AiAgentsState : IDisposable
    {
        private readonly IAgentStore _store;
        private readonly IAiOperationsFeed _feed;
        private readonly RealtimeClient _realtime;

        public AiAgentsState(IAgentStore store, IAiOperationsFeed feed, RealtimeClient realtime)
        {
            _store = store;
            _feed = feed;
            _realtime = realtime;
            _feed.Changed += OnFeedChanged;
            _realtime.AgentActivityReceived += OnAgentActivityAsync;
        }

        public bool IsLoading { get; private set; }

        /// <summary>True once the agents have been read at least once.</summary>
        public bool IsLoaded { get; private set; }

        /// <summary>Set when the agents could not be read; the page offers to try again.</summary>
        public bool LoadFailed { get; private set; }

        public List<AiAgent> Agents { get; private set; } = new();

        public event Action? Changed;

        /// <summary>The failures a screen reports and offers to retry, as opposed to a bug.</summary>
        public static bool IsFailure(Exception exception) =>
            exception is HttpRequestException or TaskCanceledException or TimeoutException or JsonException;

        public async Task LoadAsync(bool again = false)
        {
            if ((IsLoaded && !again) || IsLoading) return;
            IsLoading = true;
            LoadFailed = false;
            Changed?.Invoke();
            try
            {
                Agents = (await _store.ListAsync()).ToList();
                IsLoaded = true;
            }
            catch (Exception exception) when (IsFailure(exception))
            {
                LoadFailed = true;
            }
            finally
            {
                IsLoading = false;
                Changed?.Invoke();
            }

            if (!IsLoaded) return;
            // The figures, and the pushes that keep them fresh. Neither throws.
            await _feed.RefreshAsync();
            await _realtime.StartAsync();
        }

        public AiAgent? GetAgent(string id) => Agents.FirstOrDefault(a => a.Id == id);

        public async Task<AiAgent> CreateAsync(AiAgent agent)
        {
            if (!agent.Channels.Any()) agent.Channels = AgentBuilderCopy.DefaultChannels([]);
            var saved = await _store.CreateAsync(agent);
            Agents.Add(saved);
            Changed?.Invoke();
            return saved;
        }

        /// <summary>Saves a changed copy (see <see cref="Copy"/>); the list shows it once saved.</summary>
        public async Task SaveAsync(AiAgent changed)
        {
            await _store.SaveAsync(changed);
            var index = Agents.FindIndex(a => a.Id == changed.Id);
            if (index >= 0) Agents[index] = changed;
            Changed?.Invoke();
        }

        public async Task SetLiveAsync(AiAgent agent, bool on)
        {
            await _store.SetLiveAsync(agent.Id, on);
            agent.IsDraft = false;
            agent.IsActive = on;
            Changed?.Invoke();
        }

        public async Task DeleteAsync(AiAgent agent)
        {
            await _store.DeleteAsync(agent.Id);
            Agents.RemoveAll(a => a.Id == agent.Id);
            Changed?.Invoke();
        }

        /// <summary>A copy to change before saving, so the shown agent stays as it was until the save succeeds.</summary>
        public static AiAgent Copy(AiAgent agent) =>
            JsonSerializer.Deserialize<AiAgent>(JsonSerializer.Serialize(agent))!;

        // An agent reported something: the figures are read again, and the feed raises Changed when they are.
        private Task OnAgentActivityAsync(AgentActivityEvent activity) => _feed.RefreshAsync();

        private void OnFeedChanged() => Changed?.Invoke();

        public void Dispose()
        {
            _feed.Changed -= OnFeedChanged;
            _realtime.AgentActivityReceived -= OnAgentActivityAsync;
        }
    }
}
