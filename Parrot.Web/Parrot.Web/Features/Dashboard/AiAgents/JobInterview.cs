using Parrot.SharedComponents.DesignSystem;
using Parrot.Web.Components.Pages.Dashboard.AiAgents;

namespace Parrot.Web.Features.Dashboard.AiAgents;

/// <summary>
/// One thing the agent wants to know about its job, asked the way it would
/// ask: the question for a form, the same question to say aloud, and whether
/// its job description can do without it.
/// </summary>
public sealed record JobAsk(FormQuestion Form, string Spoken, bool Must)
{
    public string Id => Form.Id;
}

/// <summary>
/// What was understood about the job so far: every question it could ask, in
/// order of importance; the answers it heard (Known); its best guesses from
/// what the business has already said, to confirm rather than ask; and the
/// topics the conversation goes through, in order.
/// </summary>
public sealed record JobReading(
    IReadOnlyList<JobAsk> Asks,
    IReadOnlyDictionary<string, FormAnswer> Known,
    IReadOnlyDictionary<string, FormAnswer> Guessed,
    IReadOnlyList<JobSection>? Sections = null);

/// <summary>
/// A topic of the conversation (designer and team, 15 October 2026): its name
/// as the owner sees it ("Refunds"), and the questions it covers, which they
/// never see as a list. A topic whose questions the business has answered
/// already is left out.
/// </summary>
public sealed record JobSection(string Id, string Title, IReadOnlyList<string> AskIds);

/// <summary>What the job is understood from: the job, the business, what the owner said, and the answers given so far.</summary>
public sealed record JobInterviewRequest(
    string UseCase,
    string? CustomJob,
    string? Industry,
    string Business,
    string Said,
    IReadOnlyDictionary<string, FormAnswer> Answers);

/// <summary>
/// What the answers come to: sentences for the job description, settings
/// (when to bring the owner in, discounts, tone), facts for the business's
/// knowledge, one line saying what else was noted (or null), and the
/// sentences of what was said that an answer changed (an answer given
/// differently from what was said takes their place).
/// </summary>
public sealed record JobOutcome(
    IReadOnlyList<string> Sentences,
    Action<AgentSettings> Apply,
    IReadOnlyList<string> Facts,
    string? Noted,
    IReadOnlyList<string>? Replaced = null);

/// <summary>
/// Getting to know an agent's job before its job description is written
/// (designer and team, 15 October 2026). Each job carries what an agent doing
/// it needs to know; the owner never sees that list. What they say is read
/// against it; what is missing is asked as the agent's own follow-ups (aloud,
/// or as a short form for someone typing), and its best guesses are asked to
/// confirm. An AI's work: the demo stands in with rules
/// (agents-backend-requests).
/// </summary>
public interface IJobInterview
{
    bool Available { get; }

    Task<JobReading> ReadAsync(JobInterviewRequest request, CancellationToken cancellationToken = default);

    /// <summary>An answer to one question, heard aloud: the choices it names, or its words.</summary>
    Task<FormAnswer> HearAsync(JobAsk ask, string said, CancellationToken cancellationToken = default);

    /// <summary>"Decide for me": a sensible answer to each question given.</summary>
    Task<IReadOnlyDictionary<string, FormAnswer>> DecideAsync(JobInterviewRequest request, IReadOnlyList<string> questions, CancellationToken cancellationToken = default);

    Task<JobOutcome> ConcludeAsync(JobInterviewRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// A question something just added to the job description opens, or none
    /// ("Small discounts are fine": how much?). The request's Said is the job
    /// description; its answer is concluded like any other.
    /// </summary>
    Task<JobAsk?> GapAsync(JobInterviewRequest request, IReadOnlyList<string> added, CancellationToken cancellationToken = default);
}

/// <summary>Real mode: nothing to ask with yet, so the job description is written straight from what was said, as before.</summary>
public sealed class UnavailableJobInterview : IJobInterview
{
    public bool Available => false;

    public Task<JobReading> ReadAsync(JobInterviewRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new JobReading([], new Dictionary<string, FormAnswer>(), new Dictionary<string, FormAnswer>()));

    public Task<FormAnswer> HearAsync(JobAsk ask, string said, CancellationToken cancellationToken = default) =>
        Task.FromResult(new FormAnswer([], said));

    public Task<IReadOnlyDictionary<string, FormAnswer>> DecideAsync(JobInterviewRequest request, IReadOnlyList<string> questions, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, FormAnswer>>(new Dictionary<string, FormAnswer>());

    public Task<JobOutcome> ConcludeAsync(JobInterviewRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new JobOutcome([], _ => { }, [], null));

    public Task<JobAsk?> GapAsync(JobInterviewRequest request, IReadOnlyList<string> added, CancellationToken cancellationToken = default) =>
        Task.FromResult<JobAsk?>(null);
}
