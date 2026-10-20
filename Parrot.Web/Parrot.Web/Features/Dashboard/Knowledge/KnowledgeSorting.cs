using Parrot.Web.Services;
using System.Text.RegularExpressions;

namespace Parrot.Web.Features.Dashboard.Knowledge;

/// <summary>
/// Standing in for the AI that will do it (knowledge-backend-requests 9 and 15): what the owner writes, split into
/// short facts, each sorted into a topic and a heading by word rules. Used by the demo and the preview; the knowledge
/// service replaces it.
/// </summary>
public static class KnowledgeSorting
{
    private static readonly Regex Care = new(@"\b(wash|iron|dry|clean|care|store|fade)\b", RegexOptions.IgnoreCase);

    private static readonly (KnowledgeTopic Topic, Regex Words)[] Rules =
    [
        (KnowledgeTopic.Payment, new(@"\b(pay|paid|payment|transfer|card|cash|pos|bank|deposit|opay|moniepoint|paystack)\b", RegexOptions.IgnoreCase)),
        (KnowledgeTopic.Returns, new(@"\b(return|returns|refund|refunds|exchange|exchanges|swap|cancel|cancellation|cancellations)\b", RegexOptions.IgnoreCase)),
        (KnowledgeTopic.Delivery, new(@"\b(deliver|delivers|delivery|shipping|ship|ships|dispatch|pick ?up|courier|logistics|waybill)\b", RegexOptions.IgnoreCase)),
        (KnowledgeTopic.Hours, new(@"\b(open|opens|close|closed|closes|hours|monday|tuesday|wednesday|thursday|friday|saturday|sunday|weekend|holiday|holidays|address|located|location|street|branch|call us|phone|email)\b", RegexOptions.IgnoreCase)),
        (KnowledgeTopic.Prices, new(@"(₦|\bnaira\b|\bprice|\bprices\b|\bdiscount|\b\d+% off\b|\bcost\b|\bcharge\b|\bfee\b)", RegexOptions.IgnoreCase)),
        (KnowledgeTopic.Products, new(@"\b(we (sell|make|offer|stock|have|take)|our (products|items|range)|sizes?|colours?|materials?)\b", RegexOptions.IgnoreCase)),
    ];

    public static KnowledgeTopic Sort(string text) =>
        Rules.FirstOrDefault(rule => rule.Words.IsMatch(text)) is { Words: not null } match ? match.Topic : KnowledgeTopic.Notes;

    /// <summary>A fact, sorted (a topic given wins); care instructions get a heading of their own, as the AI's own headings will.</summary>
    public static KnowledgeFact New(string sentence, KnowledgeTopic? topic)
    {
        var sorted = topic ?? Sort(sentence);
        var label = sorted == KnowledgeTopic.Notes && Care.IsMatch(sentence) ? "Product care" : KnowledgeTopics.Label(sorted);
        return new(Guid.NewGuid().ToString("N"), sorted, sentence, FactState.Known, null, DateTimeOffset.UtcNow, label);
    }

    /// <summary>One fact a sentence, as a memory list keeps them short.</summary>
    public static IEnumerable<string> Sentences(string text) =>
        Regex.Split(text.Trim(), @"(?<=[.!?])\s+|\n+")
            .Select(Tidy)
            .Where(sentence => sentence.Length > 1)
            .Take(10);

    public static string Tidy(string text)
    {
        var sentence = Regex.Replace(text.Trim(), @"\s+", " ");
        if (sentence.Length > 1200) sentence = sentence[..1200];
        if (sentence.Length > 0 && char.IsLower(sentence[0])) sentence = char.ToUpperInvariant(sentence[0]) + sentence[1..];
        if (sentence.Length > 0 && !".!?".Contains(sentence[^1])) sentence += ".";
        return sentence;
    }
}
