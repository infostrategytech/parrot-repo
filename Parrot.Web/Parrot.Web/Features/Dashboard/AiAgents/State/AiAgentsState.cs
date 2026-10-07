using System.Text.Json;
using Parrot.Web.Components.Pages.Dashboard.AiAgents;

namespace Parrot.Web.Features.Dashboard.AiAgents.State
{
    /// <summary>
    /// The agents the screens show, read once per session from
    /// <see cref="IAgentStore"/> and kept in step with every change saved
    /// through it. A change shows only once the store has accepted it, so a
    /// failed save never looks saved. The calls throw on failure; each screen
    /// says what failed and offers to try again (<see cref="IsFailure"/>).
    /// </summary>
    public class AiAgentsState(IAgentStore store)
    {
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
                Agents = (await store.ListAsync()).ToList();
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
        }

        public AiAgent? GetAgent(string id) => Agents.FirstOrDefault(a => a.Id == id);

        public async Task<AiAgent> CreateAsync(AiAgent agent)
        {
            if (!agent.Channels.Any()) agent.Channels = AgentBuilderCopy.DefaultChannels([]);
            var saved = await store.CreateAsync(agent);
            Agents.Add(saved);
            Changed?.Invoke();
            return saved;
        }

        /// <summary>Saves a changed copy (see <see cref="Copy"/>); the list shows it once saved.</summary>
        public async Task SaveAsync(AiAgent changed)
        {
            await store.SaveAsync(changed);
            var index = Agents.FindIndex(a => a.Id == changed.Id);
            if (index >= 0) Agents[index] = changed;
            Changed?.Invoke();
        }

        public async Task SetLiveAsync(AiAgent agent, bool on)
        {
            await store.SetLiveAsync(agent.Id, on);
            agent.IsDraft = false;
            agent.IsActive = on;
            Changed?.Invoke();
        }

        public async Task DeleteAsync(AiAgent agent)
        {
            await store.DeleteAsync(agent.Id);
            Agents.RemoveAll(a => a.Id == agent.Id);
            Changed?.Invoke();
        }

        /// <summary>A copy to change before saving, so the shown agent stays as it was until the save succeeds.</summary>
        public static AiAgent Copy(AiAgent agent) =>
            JsonSerializer.Deserialize<AiAgent>(JsonSerializer.Serialize(agent))!;
    }
}
