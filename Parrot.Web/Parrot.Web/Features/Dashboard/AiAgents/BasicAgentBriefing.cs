using System.Text.RegularExpressions;

namespace Parrot.Web.Features.Dashboard.AiAgents;

/// <summary>
/// Writing an agent's job description without the AI, until the agents API
/// has a service that does it (a backend request): what the
/// owner said becomes the job description in their own words, tidied and in
/// order (what it does, what it never does, when it calls them); the job's
/// own sentences fill only what they didn't cover; the kind of business adds
/// the rule an agent there must keep. With nothing said, a ready-made job
/// description for the job. Test replies need the AI (request 16), so there
/// are none here: CanTry is false and Test as a customer stays out of sight.
/// </summary>
public class BasicAgentBriefing : IAgentBriefing
{
    public virtual bool Available => true;

    public virtual bool CanTry => false;

    public virtual Task<string?> SuggestIndustryAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    public virtual Task<AgentBrief> DraftAsync(AgentDraftRequest request, CancellationToken cancellationToken = default) => Task.FromResult(Draft(request));

    public virtual Task<AgentBriefUpdate> UnderstandAsync(string said, AgentBrief sofar, CancellationToken cancellationToken = default) => Task.FromResult(Understand(said, sofar));

    public virtual Task<AgentTryReply> TryAsync(AgentTryRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    // ── Writing it ──────────────────────────────────────────────────────────

    /// <summary>The ready-made job description for a job, in the business's name.</summary>
    protected static List<string> ReadyMade(string useCase, string? customJob, string business) => useCase switch
    {
        "support" => [$"Answer customers' questions about {business}: what we sell, prices, delivery and opening hours.",
            "Keep replies short and friendly, and if you're not sure of an answer, say you'll check with the team.",
            "Never make up prices or details.",
            "Bring me in when a customer is upset or asks for a refund."],
        "sales" => [$"Help customers choose what to buy from {business} and take their order.",
            "Confirm the item, size, quantity and delivery address, then share how to pay.",
            "Never confirm an order before payment is received.",
            "Never offer a discount without asking me.",
            "Bring me in when a big order comes in."],
        "tracking" => ["Tell customers where their order is and when it will arrive.",
            "Ask for the order number, or the phone number they ordered with, then share the latest status and the day it should arrive.",
            "Never promise a delivery time you can't see in the order.",
            "Bring me in when an order is late or lost."],
        "bookings" => ["Book customers in for an appointment at a time that suits them.",
            "Ask for their name, the service they want and a time that suits them, and send a reminder the day before.",
            "Never book outside our opening hours.",
            "Bring me in when someone wants to cancel on the same day."],
        "followups" => ["Follow up with customers who asked about something but didn't buy.",
            "Send one friendly reminder a day after they asked, and share what's in stock and how to order.",
            "Never send more than one reminder.",
            "Bring me in when a customer wants to negotiate the price."],
        _ => [customJob is { Length: > 0 } job ? $"Help {business}'s customers with {Lower(job)}." : $"Help {business}'s customers.",
            "Keep replies short and friendly.",
            "Never make up an answer.",
            "Bring me in when you can't help."],
    };

    /// <summary>What a kind of business adds: the rule an agent there must keep.</summary>
    protected static string? IndustryRule(string? industry) => industry switch
    {
        "fashion" => "Never promise a delivery date you can't confirm.",
        "beauty" => "Never promise results from a treatment or product.",
        "food" => "Never say a dish is free of an allergen unless you're sure.",
        "groceries" => "Never confirm an item is in stock without checking.",
        "health" => "Never give medical advice or a diagnosis.",
        "electronics" => "Never promise a warranty you don't offer.",
        "home" => "Never confirm measurements you haven't checked.",
        "property" => "Never confirm a price or a viewing without checking with me.",
        "education" => "Never promise a result or a certificate you don't give.",
        "travel" => "Never confirm a booking until it's paid for.",
        "logistics" => "Never promise a delivery time you can't see.",
        "finance" => "Never ask for a PIN, password or OTP.",
        "services" => "Never give legal or financial advice.",
        _ => null,
    };

    /// <summary>
    /// The job description: what the owner said, in their words; the job's
    /// own sentences only where they said nothing (its job first, what it
    /// does, when to call them); then the kind of business's rule.
    /// </summary>
    protected static AgentBrief Draft(AgentDraftRequest request)
    {
        var business = request.Business.Trim().Length > 0 ? request.Business.Trim() : "the business";
        var sentences = ReadyMade(request.UseCase, request.CustomJob, business);
        var said = Sentences(request.Said ?? string.Empty).Select(Clean).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (said.Count > 0)
        {
            var template = sentences;
            sentences = JobWords.IsMatch(said[0]) ? [said[0]] : [template[0], said[0]];
            foreach (var sentence in said.Skip(1)) Place(sentences, sentence);
            if (!sentences.Skip(1).Any(sentence => KindOf(sentence) == BriefKind.Does)) Place(sentences, template.First(sentence => KindOf(sentence) == BriefKind.Does && sentence != template[0]));
            if (!sentences.Any(sentence => KindOf(sentence) == BriefKind.CallMe)) Place(sentences, template.Last());
        }
        var careful = new List<string>();
        if (IndustryRule(request.Industry) is { } rule) careful.Add(rule);
        if (request.Industry == "health") careful.Add("Bring me in at once if someone describes an emergency.");
        foreach (var sentence in careful.Where(sentence => !sentences.Contains(sentence, StringComparer.OrdinalIgnoreCase))) Place(sentences, sentence);
        return new() { Summary = string.Join(" ", sentences) };
    }

    /// <summary>What the owner said since, worked in next to what it belongs with; only what is new.</summary>
    protected static AgentBriefUpdate Understand(string said, AgentBrief sofar)
    {
        var sentences = sofar.Sentences().ToList();
        var added = new List<string>();
        foreach (var sentence in Sentences(said))
        {
            var clean = Clean(sentence);
            if (sentences.Contains(clean, StringComparer.OrdinalIgnoreCase)) continue;
            Place(sentences, clean);
            added.Add(clean);
        }
        return new(new() { Summary = string.Join(" ", sentences) }, added);
    }

    // ── Sentences ───────────────────────────────────────────────────────────

    /// <summary>What a sentence of a job description is: something it does, never does, or when it calls the owner.</summary>
    protected enum BriefKind { Does, Never, CallMe }

    private static readonly Regex CallMeWords = new(@"\b(bring me in|bring (in )?(the )?(team|manager|owner)|call me|tell me|let me know|alert me|get me|contact me|hand (it |them )?over|pass (it|them|the customer|customers) (on )?to|escalate)\b", RegexOptions.IgnoreCase);
    private static readonly Regex NeverWords = new(@"(^|\b)(never|don't|do not|dont|not allowed|avoid|no need to)\b", RegexOptions.IgnoreCase);
    // A sentence that says what the job is ("Help customers…", "Answer…").
    private static readonly Regex JobWords = new(@"^(your job is|help|answer|take|book|tell|follow|sell|handle|reply|look after|deal with)\b", RegexOptions.IgnoreCase);
    // Openings that say nothing in a job description ("You should…", "Please…").
    private static readonly Regex Opening = new(@"^(so,?\s+|ok(ay)?,?\s+|and\s+|also,?\s+|please,?\s+|you should\s+|i want you to\s+|it should\s+|your job is to\s+|you('ll| will| are going to)\s+)+", RegexOptions.IgnoreCase);

    protected static BriefKind KindOf(string sentence) =>
        CallMeWords.IsMatch(sentence) ? BriefKind.CallMe : NeverWords.IsMatch(sentence) ? BriefKind.Never : BriefKind.Does;

    // A new sentence goes after the last one like it, so the job description
    // keeps its order: what it does, what it never does, when it calls you.
    protected static void Place(List<string> sentences, string sentence)
    {
        if (sentences.Count == 0) { sentences.Add(sentence); return; }
        var kind = KindOf(sentence);
        var at = 1;
        for (var index = 1; index < sentences.Count; index++)
            if (KindOf(sentences[index]) <= kind) at = index + 1;
        sentences.Insert(at, sentence);
    }

    protected static IEnumerable<string> Sentences(string text) =>
        Regex.Split(text.Trim(), @"(?<=[.!?])\s+|\n+").Select(sentence => sentence.Trim()).Where(sentence => sentence.Length > 2).Take(20);

    protected static string Clean(string sentence)
    {
        var text = Opening.Replace(Regex.Replace(sentence.Trim(), @"\s+", " "), string.Empty).Trim();
        if (text.Length == 0) text = sentence.Trim();
        if (char.IsLower(text[0])) text = char.ToUpperInvariant(text[0]) + text[1..];
        if (!".!?".Contains(text[^1])) text += ".";
        return text.Length > 240 ? text[..240] : text;
    }

    protected static string Lower(string text) => text.Length > 1 && char.IsUpper(text[0]) && !char.IsUpper(text[1]) ? char.ToLowerInvariant(text[0]) + text[1..] : text;
}
