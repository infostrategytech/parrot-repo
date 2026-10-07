using System.Text.RegularExpressions;
using Parrot.Web.Services;

namespace Parrot.Web.Components.Pages.Dashboard.TeachAi;

public enum TeachAiStep { Welcome, Topics, Summary }
public enum TeachAiSpeaker { Agent, Person }

/// <summary>
/// Where the conversation is. Review: checking everything that was found, at
/// once. Changing, Correcting: waiting to hear what to change. Which: asking
/// what an answer was about. Checking: one topic read back (from the step
/// bar). Asking: a gap. FollowUp: a short question about the answer just
/// given. Checkpoint: once, part way, "here's what I have so far". Extras:
/// anything else customers should know.
/// </summary>
public enum TeachAiStage { Review, Changing, Which, Checking, Correcting, Asking, FollowUp, Checkpoint, Extras, Done }

/// <summary>An item on a card: found and not yet checked, confirmed, found but unsure, or still to ask.</summary>
public enum TeachAiItemState { Found, Confirmed, Unsure, Open }

public enum TeachAiChoiceKind { Yes, Change, Skip, No, Done, Topic }

/// <summary>A suggested reply, offered as a button. Choosing it sends its label, as if said.</summary>
public sealed record TeachAiChoice(string Label, TeachAiChoiceKind Kind);

/// <summary>A voice note: its audio (a URL in this browser only, or null), its length and its waveform.</summary>
public sealed record TeachAiVoiceNote(string? Url, int Seconds, IReadOnlyList<double>? Peaks = null);

/// <summary>What the AI understood from the person's words, as it was saved: a topic and a sentence.</summary>
public sealed record TeachAiUnderstood(KnowledgeTopic Topic, string Sentence);

/// <summary>An answer from approved knowledge, and the topic it is based on (null when there was nothing to go on).</summary>
public sealed record TeachAiAnswer(string Text, KnowledgeTopic? BasedOn);

/// <summary>
/// An item on a line's card: what it is about, what it says, and where it
/// came from. Anchor is where it is mentioned aloud, when the voice names it
/// rather than reads it ("I've got your prices…"); the item shows as it is said.
/// </summary>
public sealed record TeachAiItem(KnowledgeTopic Topic, string Text, string? Source)
{
    public TeachAiItemState State { get; init; } = TeachAiItemState.Found;
    public string? Anchor { get; init; }
    public string Spoken => Anchor ?? Text;
}

/// <summary>
/// One line of the conversation. Text is what is said aloud. On screen a line
/// can open with words (Screen), show found items on a card (Items) and end
/// with its question (Then), so the items are read once, on the card. A voice
/// note keeps its length; a person's line keeps what it taught (Learned).
/// </summary>
public sealed record TeachAiLine(TeachAiSpeaker Speaker, string Text, KnowledgeTopic? Topic = null)
{
    /// <summary>On the person's lines: the voice note it was said in (Text is what was heard).</summary>
    public TeachAiVoiceNote? Note { get; init; }
    /// <summary>On the person's lines: what the AI understood from them, a sentence per topic, shown under the message.</summary>
    public IReadOnlyList<TeachAiUnderstood> Understood { get; init; } = [];
    public string? Screen { get; init; }
    public IReadOnlyList<TeachAiItem> Items { get; init; } = [];
    public string? Then { get; init; }
    /// <summary>Pictures and documents sent with the line.</summary>
    public IReadOnlyList<TeachAiFile> Files { get; init; } = [];
    /// <summary>On the person's lines: topics confirmed as they were found (no new words to check).</summary>
    public IReadOnlyList<KnowledgeTopic> Learned { get; init; } = [];
    public string Display => Screen ?? Text;

    /// <summary>Where item <paramref name="i"/> starts in what is said, so it shows as the voice reaches it.</summary>
    public int ItemAt(int i)
    {
        var from = 0;
        for (var n = 0; n <= i; n++)
        {
            var at = Text.IndexOf(Items[n].Spoken, from, StringComparison.Ordinal);
            if (at < 0) return Text.Length;
            if (n == i) return at;
            from = at + Items[n].Spoken.Length;
        }
        return Text.Length;
    }

    /// <summary>Where the closing question starts in what is said.</summary>
    public int ThenAt => Then is null ? Text.Length : Math.Max(0, Text.LastIndexOf(Then, StringComparison.Ordinal));

    /// <summary>Where the opening words end in what is said.</summary>
    public int ScreenEnd => Items.Count > 0 ? ItemAt(0) : Then is not null ? ThenAt : Text.Length;
}

public enum TeachAiFileKind { Image, Pdf, Document, Sheet, Other }

/// <summary>A picture or document the person attached. Preview is a small data URL, for pictures.</summary>
public sealed record TeachAiFile(string Name, long Size, TeachAiFileKind Kind, string? Preview = null);

/// <summary>
/// The conversation that builds a business's base knowledge, as a person
/// would have it: it says what it is for, reads back everything it found at
/// once and asks what is wrong, then fills the gaps with open questions and a
/// short follow-up where an answer leaves an obvious question. One answer can
/// cover several topics ("we also sell bags, and we only deliver on the
/// mainland"); each goes where it belongs and is not asked again. Pictures and
/// documents are welcome at any point.
///
/// Voice and text share it. In the demo it understands answers with simple
/// rules about the demo business; the real version needs an AI service behind
/// it (see knowledge-backend-requests), reporting the same card changes.
/// </summary>
public sealed partial class TeachAiConversation(string business, IReadOnlyList<KnowledgeCard> draft)
{
    private static readonly string[] Thanks = ["Great.", "Got it.", "Perfect, noted.", "Lovely.", "Thanks, that helps."];
    private enum FollowUpKind { IncludesDelivery, PayOnDelivery, ReturnDays, HandoverNumber }

    private int _thanks;
    private bool _reviewed;
    private bool _gapsStarted;
    private bool _askedExtras;
    private bool _checkpointShown;
    private bool _correctingFromCheckpoint;
    private bool _correctingFromReview;
    private string? _pending;
    private FollowUpKind _followUp;
    private readonly HashSet<KnowledgeTopic> _followedUp = [];
    private readonly List<KnowledgeTopic> _learned = [];
    private readonly List<TeachAiUnderstood> _understood = [];
    private List<KnowledgeTopic> _reviewTopics = [];

    public string Business { get; } = business;
    public List<KnowledgeCard> Cards { get; } = draft.ToList();
    public List<TeachAiLine> Lines { get; } = [];
    public TeachAiStep Step { get; private set; } = TeachAiStep.Welcome;
    public TeachAiStage Stage { get; private set; } = TeachAiStage.Review;
    public int Index { get; private set; }

    /// <summary>The six topics every agent needs; Notes is extra.</summary>
    public IEnumerable<KnowledgeCard> Core => Cards.Where(c => c.Topic != KnowledgeTopic.Notes);

    /// <summary>The topic being talked about, when it is one topic.</summary>
    public KnowledgeCard? Current => Step == TeachAiStep.Topics
        && Stage is TeachAiStage.Asking or TeachAiStage.FollowUp or TeachAiStage.Checking or TeachAiStage.Correcting
        ? Cards[Index] : null;

    public int Done => Core.Count(c => c.Status is KnowledgeStatus.Confirmed or KnowledgeStatus.Skipped);

    /// <summary>Topics it has something confirmed for: "5 of 6 covered".</summary>
    public int Covered => Core.Count(c => c.Status == KnowledgeStatus.Confirmed && c.HasContent);

    /// <summary>The replies to offer as buttons, for what was just asked.</summary>
    public IReadOnlyList<TeachAiChoice> Choices => Step != TeachAiStep.Topics ? [] : Stage switch
    {
        TeachAiStage.Review => [new("All correct", TeachAiChoiceKind.Yes), new("Change something", TeachAiChoiceKind.Change)],
        TeachAiStage.Checking => [new("Looks right", TeachAiChoiceKind.Yes), new("Change it", TeachAiChoiceKind.Change)],
        TeachAiStage.Asking => [new(SkipLabel, TeachAiChoiceKind.Skip)],
        TeachAiStage.FollowUp => _followUp is FollowUpKind.ReturnDays or FollowUpKind.HandoverNumber
            ? [new(SkipLabel, TeachAiChoiceKind.Skip)]
            : [new("Yes", TeachAiChoiceKind.Yes), new("No", TeachAiChoiceKind.No)],
        TeachAiStage.Which => [.. _reviewTopics.Select(t => new TeachAiChoice(Name(t), TeachAiChoiceKind.Topic)), new(SomethingElse, TeachAiChoiceKind.Topic)],
        TeachAiStage.Checkpoint => [new("Carry on", TeachAiChoiceKind.Yes), new("Fix something", TeachAiChoiceKind.Change)],
        TeachAiStage.Extras => [new("That's everything", TeachAiChoiceKind.Done)],
        _ => [],
    };

    /// <summary>What to do now, in a few words, for the voice bar.</summary>
    public string Hint => Step != TeachAiStep.Topics ? "" : Stage switch
    {
        TeachAiStage.Review => "Say “all correct”, or tell me what to change.",
        TeachAiStage.Changing or TeachAiStage.Correcting => "Tell me what to change, in your own words.",
        TeachAiStage.Which => "Say which one it's about.",
        TeachAiStage.Checking => "Say “yes”, or tell me what to change.",
        TeachAiStage.FollowUp when _followUp is FollowUpKind.IncludesDelivery or FollowUpKind.PayOnDelivery => "Say yes or no, or tell me more.",
        TeachAiStage.Asking or TeachAiStage.FollowUp => "Answer in your own words, or say “skip”.",
        TeachAiStage.Checkpoint => "Say “carry on”, or tell me what to fix.",
        TeachAiStage.Extras => "Tell me anything else, or say “that's everything”.",
        _ => "",
    };

    private const string SkipLabel = "Skip for now";
    private const string SomethingElse = "Something else";

    /// <summary>Says what this is for, then reads back everything that was found and asks what is wrong.</summary>
    public TeachAiLine Start()
    {
        Step = TeachAiStep.Topics;
        // Approved before ("Teach more", from an agent): nothing to check or
        // ask again, so straight to what is new.
        if (Core.All(c => c.Status is KnowledgeStatus.Confirmed or KnowledgeStatus.Skipped))
        {
            _reviewed = true;
            _askedExtras = true;
            Stage = TeachAiStage.Extras;
            return Say($"Welcome back! What else should your AI know about {Business}? Tell me anything new, or send a file.");
        }
        var hello = $"Hi! I'll learn about {Business} so your AI can answer customers the way you would.";
        var found = Unchecked();
        _reviewTopics = found.Select(c => c.Topic).ToList();
        if (found.Count == 0) return NextGap(hello + " I'll ask a few quick questions.");
        Stage = TeachAiStage.Review;
        return Card($"{hello} Here's what I found on your {Channels(found)}.", found, "Is anything wrong or out of date?");
    }

    /// <summary>What the person said, typed or attached; returns what the agent says next.</summary>
    public TeachAiLine Reply(string text, TeachAiVoiceNote? note = null, IReadOnlyList<TeachAiFile>? files = null)
    {
        var said = text.Trim();
        files ??= [];
        var person = new TeachAiLine(TeachAiSpeaker.Person, said, Current?.Topic) { Files = files, Note = note };
        var at = Lines.Count;
        _before[at] = Now();
        Lines.Add(person);
        _learned.Clear();
        _understood.Clear();
        var reply = Step == TeachAiStep.Topics ? Respond(said, files) : Say("You can change anything here before you approve it.");
        if (_learned.Count > 0 || _understood.Count > 0)
        {
            var understood = _understood.Select(u => u.Topic).ToHashSet();
            Lines[at] = person with
            {
                Learned = _learned.Distinct().Where(t => !understood.Contains(t)).ToList(),
                Understood = _understood.ToList(),
            };
        }
        return reply;
    }

    // ── Editing a message, as in other AI chats ─────────────────────────────

    // Where the conversation stood before each of the person's messages, so
    // the latest one can be edited and answered again.
    private sealed record Moment(
        List<KnowledgeCard> Cards, TeachAiStep Step, TeachAiStage Stage, int Index, int Thanks,
        bool Reviewed, bool GapsStarted, bool AskedExtras, bool CheckpointShown,
        bool CorrectingFromCheckpoint, bool CorrectingFromReview, string? Pending,
        FollowUpKind FollowUp, List<KnowledgeTopic> FollowedUp, List<KnowledgeTopic> ReviewTopics);

    private readonly Dictionary<int, Moment> _before = [];

    private Moment Now() => new(
        [.. Cards], Step, Stage, Index, _thanks, _reviewed, _gapsStarted, _askedExtras, _checkpointShown,
        _correctingFromCheckpoint, _correctingFromReview, _pending, _followUp, [.. _followedUp], [.. _reviewTopics]);

    private void Restore(Moment moment)
    {
        Cards.Clear();
        Cards.AddRange(moment.Cards);
        Step = moment.Step;
        Stage = moment.Stage;
        Index = moment.Index;
        _thanks = moment.Thanks;
        _reviewed = moment.Reviewed;
        _gapsStarted = moment.GapsStarted;
        _askedExtras = moment.AskedExtras;
        _checkpointShown = moment.CheckpointShown;
        _correctingFromCheckpoint = moment.CorrectingFromCheckpoint;
        _correctingFromReview = moment.CorrectingFromReview;
        _pending = moment.Pending;
        _followUp = moment.FollowUp;
        _followedUp.Clear();
        _followedUp.UnionWith(moment.FollowedUp);
        _reviewTopics = [.. moment.ReviewTopics];
    }

    /// <summary>Whether this is the person's latest message: nothing they said came after it.</summary>
    public bool IsLatest(int line) => !Lines.Skip(line + 1).Any(l => l.Speaker == TeachAiSpeaker.Person);

    /// <summary>
    /// Whether a message of the person's can be edited: the latest always (it
    /// is answered again); an earlier one when it taught something in words.
    /// </summary>
    public bool CanEdit(int line) =>
        line >= 0 && line < Lines.Count && Lines[line].Speaker == TeachAiSpeaker.Person && Step == TeachAiStep.Topics
        && (IsLatest(line) ? _before.ContainsKey(line) : Lines[line].Understood.Count > 0);

    /// <summary>
    /// The latest message, edited: back to where things stood before it was
    /// sent, with it and everything after it gone. Send the new words with
    /// Reply, and they are answered afresh. Returns the message as it was (for
    /// its voice note and files).
    /// </summary>
    public TeachAiLine Rewind(int line)
    {
        var sent = Lines[line];
        Restore(_before[line]);
        Lines.RemoveRange(line, Lines.Count - line);
        foreach (var later in _before.Keys.Where(k => k >= line).ToList()) _before.Remove(later);
        return sent;
    }

    /// <summary>
    /// An earlier message, edited: what it taught is replaced, in place, by
    /// what the new words say; everything after it stays as it was.
    /// </summary>
    public void Revise(int line, string text)
    {
        var old = Lines[line];
        var said = text.Trim();
        if (said.Length == 0 || said == old.Text) return;
        var (changes, _, _) = Plan(said, old.Topic ?? old.Understood.FirstOrDefault()?.Topic);
        var understood = new List<TeachAiUnderstood>();
        foreach (var topic in changes.GroupBy(c => c.Topic))
        {
            var sentence = string.Join(" ", topic.Select(c => Clean(c.Body)));
            var was = old.Understood.Where(u => u.Topic == topic.Key).ToList();
            var at = Cards.FindIndex(c => c.Topic == topic.Key);
            var card = Cards[at];
            if (was.Count > 0 && card.Text.Contains(was[0].Sentence, StringComparison.Ordinal))
            {
                var updated = card.Text.Replace(was[0].Sentence, sentence, StringComparison.Ordinal);
                foreach (var extra in was.Skip(1)) updated = Without(updated, extra.Sentence);
                Cards[at] = card with { Text = updated, Status = KnowledgeStatus.Confirmed };
            }
            else
            {
                Apply(topic.Key, string.Join(" ", topic.Select(c => c.Body)), replace: topic.Any(c => c.Replace));
            }
            understood.Add(new(topic.Key, sentence));
        }
        // What it taught before and no longer does.
        foreach (var gone in old.Understood.Where(u => changes.All(c => c.Topic != u.Topic)))
        {
            var at = Cards.FindIndex(c => c.Topic == gone.Topic);
            var card = Cards[at];
            var left = Without(card.Text, gone.Sentence);
            Cards[at] = card with { Text = left, Status = left.Length > 0 || card.Files is { Count: > 0 } ? card.Status : KnowledgeStatus.Missing };
        }
        Lines[line] = old with { Text = said, Understood = understood };
        _learned.Clear();
        _understood.Clear();
    }

    private static string Without(string text, string sentence) =>
        Spaces().Replace(text.Replace(sentence, "", StringComparison.Ordinal), " ").Trim();

    private TeachAiLine Respond(string said, IReadOnlyList<TeachAiFile> files)
    {
        if (files.Count > 0) return Received(said, files);
        var kind = Kind(said);
        switch (Stage)
        {
            case TeachAiStage.Review or TeachAiStage.Changing:
                if (kind is Said.Yes or Said.Done or Said.Skip) return ConfirmFound(Ack());
                if (kind is Said.No)
                {
                    Stage = TeachAiStage.Changing;
                    return Say("Sure. What should I change?");
                }
                return Corrected(said);

            case TeachAiStage.Which:
            {
                var topic = said.Equals(SomethingElse, StringComparison.OrdinalIgnoreCase) ? KnowledgeTopic.Notes
                    : _reviewTopics.Cast<KnowledgeTopic?>().FirstOrDefault(t => Name(t!.Value).Equals(said, StringComparison.OrdinalIgnoreCase))
                    ?? TopicOf(said) ?? KnowledgeTopic.Notes;
                Apply(topic, _pending ?? said, replace: false);
                _pending = null;
                Stage = TeachAiStage.Review;
                return Unchecked().Count > 0
                    ? Say($"Thanks, I've added that to {About(topic)}. Is everything else right?")
                    : ConfirmFound($"Thanks, I've added that to {About(topic)}.");
            }

            case TeachAiStage.Checking:
                if (kind is Said.Yes or Said.Done) return Accept(Ack());
                if (kind is Said.Skip) return Skip();
                if (kind is Said.No)
                {
                    Stage = TeachAiStage.Correcting;
                    return Say("Sure. What should it say?", Cards[Index].Topic);
                }
                Learn(said, Cards[Index].Topic, replaceFallback: true);
                return Next(Ack());

            case TeachAiStage.Correcting:
                if (kind is Said.Yes or Said.Done) return Accept(Ack());
                if (kind is Said.Skip) return Skip();
                if (kind is Said.No) return Say("What should it say instead?", Cards[Index].Topic);
                Learn(said, Cards[Index].Topic, replaceFallback: true);
                if (_correctingFromCheckpoint)
                {
                    _correctingFromCheckpoint = false;
                    Stage = TeachAiStage.Checkpoint;
                    return Say($"Thanks, I've updated {About(Cards[Index].Topic)}. Anything else to fix?");
                }
                if (_correctingFromReview)
                {
                    _correctingFromReview = false;
                    Stage = TeachAiStage.Review;
                    return Say($"Thanks, I've updated {About(Cards[Index].Topic)}. Is everything else right?");
                }
                return Next(Ack());

            case TeachAiStage.Asking:
            {
                var topic = Cards[Index].Topic;
                if (kind is Said.Skip or Said.Done) return Skip();
                if (kind is Said.Yes or Said.No) return Say("Could you tell me in a few words? Or say skip and we'll come back to it.", topic);
                var (learned, _, _) = Learn(said, topic);
                if (learned.Count == 0) return Say($"Sorry, I didn't catch that. {Question(topic)}", topic);
                // It was about something else: noted there, and the question still stands.
                if (Cards[Index].Status != KnowledgeStatus.Confirmed)
                    return Say($"Thanks, I've noted that under {List(learned.Select(About))}. {Question(topic)}", topic);
                return FollowUpOrNext(Ack());
            }

            case TeachAiStage.FollowUp:
            {
                if (kind is Said.Skip or Said.Done) return NextGap("No problem.");
                var card = Cards[Index];
                var addition = (_followUp, kind) switch
                {
                    (FollowUpKind.IncludesDelivery, Said.Yes) => "Prices include delivery.",
                    (FollowUpKind.IncludesDelivery, Said.No) => "Delivery is charged separately.",
                    (FollowUpKind.PayOnDelivery, Said.Yes) => "Customers can pay on delivery.",
                    (FollowUpKind.PayOnDelivery, Said.No) => "Customers pay before delivery.",
                    (FollowUpKind.ReturnDays, _) when Digits().IsMatch(said) => $"Customers have {Body(said).TrimEnd('.')} to ask for a return.",
                    (FollowUpKind.HandoverNumber, _) when Phone().Match(said) is { Success: true } number => $"Reach them on {number.Value}.",
                    _ => Clean(Body(said)),
                };
                if (addition.Length > 1) Apply(card.Topic, addition, replace: false);
                return NextGap(Ack());
            }

            case TeachAiStage.Checkpoint:
            {
                if (kind is Said.Yes or Said.Done or Said.Skip) return NextGap(Ack());
                if (kind is Said.No) return Say("Sure. What should I change?");
                var (fixedTopics, pointer, _) = Learn(said, null);
                if (pointer is { } wrong && !fixedTopics.Contains(wrong))
                {
                    Index = Cards.FindIndex(c => c.Topic == wrong);
                    Stage = TeachAiStage.Correcting;
                    _correctingFromCheckpoint = true;
                    return Say($"Sure. What should it say about {About(wrong)}?", wrong);
                }
                return Say(fixedTopics.Count == 0
                    ? "Sorry, I didn't catch what to change. Could you say it another way?"
                    : $"Got it, I've updated {List(fixedTopics.Select(About))}. Anything else to fix?");
            }

            case TeachAiStage.Extras:
                if (kind is Said.Done or Said.No or Said.Skip) return Finish("Great.");
                if (kind is Said.Yes) return Say("Sure, go ahead.");
                var (extra, _, _) = Learn(said, KnowledgeTopic.Notes);
                var placed = extra.Where(t => t != KnowledgeTopic.Notes).ToList();
                return Say(placed.Count > 0 ? $"Got it, I've added that to {List(placed.Select(About))}. Anything else?" : "Got it. Anything else?");
        }
        return Say("You can change anything here before you approve it.");
    }

    // A correction while checking what was found: put each part where it
    // belongs; ask what it should say if they only said what is wrong, and
    // which topic it is about if that is not clear.
    private TeachAiLine Corrected(string said)
    {
        var (learned, pointer, unplaced) = Learn(said, null);
        if (unplaced is not null)
        {
            _pending = unplaced;
            Stage = TeachAiStage.Which;
            return Say(learned.Count > 0
                ? $"Got it, I've updated {List(learned.Select(About))}. And the rest, which is that about?"
                : "Got it. Which is that about?");
        }
        if (pointer is { } wrong && !learned.Contains(wrong))
        {
            Index = Cards.FindIndex(c => c.Topic == wrong);
            Stage = TeachAiStage.Correcting;
            _correctingFromReview = true;
            return Say($"Sure. What should it say about {About(wrong)}?", wrong);
        }
        if (learned.Count == 0)
        {
            Stage = TeachAiStage.Changing;
            return Say("Sorry, I didn't catch what to change. Could you say it another way?");
        }
        Stage = TeachAiStage.Review;
        return Unchecked().Count > 0
            ? Say($"Got it, I've updated {List(learned.Select(About))}. Is everything else right?")
            : ConfirmFound($"Got it, I've updated {List(learned.Select(About))}.");
    }

    // Files are an answer in themselves (a price list, a returns policy):
    // they go with what is being talked about, or what their name says they are.
    private TeachAiLine Received(string said, IReadOnlyList<TeachAiFile> files)
    {
        var topic = Current?.Topic
            ?? FileTopic(string.Join(" ", files.Select(f => f.Name)))
            ?? (Kind(said) == Said.Other ? TopicOf(said) : null)
            ?? KnowledgeTopic.Notes;
        var at = Cards.FindIndex(c => c.Topic == topic);
        var card = Cards[at];
        var words = Kind(said) == Said.Other ? Clean(Body(said)) : "";
        Cards[at] = card with
        {
            Text = words.Length > 0 ? (card.Text.Length > 0 && card.Status != KnowledgeStatus.Skipped ? $"{card.Text} {words}" : words) : card.Text,
            Status = KnowledgeStatus.Confirmed,
            Source = card.Text.Length == 0 || card.Status == KnowledgeStatus.Skipped ? "you" : words.Length > 0 ? AndYou(card.Source) : card.Source,
            Files = [.. card.Files ?? [], .. files.Select(f => f.Name)],
        };
        _learned.Add(topic);
        if (words.Length > 0) _understood.Add(new(topic, words));
        var them = files.Count == 1 ? "it" : "them";
        var got = files.Count == 1 ? $"Thanks, I've got {files[0].Name}." : $"Thanks, I've got those {files.Count} files.";
        var use = topic == KnowledgeTopic.Notes
            ? $"{got} I'll keep {them} to help answer customers."
            : $"{got} I'll use {them} when customers ask about {About(topic)}.";
        return Stage switch
        {
            TeachAiStage.Review or TeachAiStage.Changing or TeachAiStage.Which => Unchecked().Count > 0
                ? Say($"{use} Is everything else I found right?")
                : ConfirmFound(use),
            TeachAiStage.Extras => Say($"{use} Anything else?"),
            _ => Next(use),
        };
    }

    /// <summary>Goes back to a topic (the step bar): reads it back, or asks for it.</summary>
    public TeachAiLine Revisit(KnowledgeTopic topic)
    {
        Step = TeachAiStep.Topics;
        Index = Cards.FindIndex(c => c.Topic == topic);
        _pending = null;
        var card = Cards[Index];
        if (card.Text.Length > 0 && card.Status != KnowledgeStatus.Skipped)
        {
            Stage = TeachAiStage.Checking;
            return Card($"Here's what I have for {About(topic)}.", [card], "Is it still right?", topic);
        }
        if (card.Status == KnowledgeStatus.Skipped) Cards[Index] = card with { Status = KnowledgeStatus.Missing };
        Stage = TeachAiStage.Asking;
        return Say(Question(topic), topic);
    }

    /// <summary>Changes a topic's text directly (editing it in the summary).</summary>
    public void Edit(KnowledgeTopic topic, string text)
    {
        var at = Cards.FindIndex(c => c.Topic == topic);
        var value = Naira(text.Trim());
        var hasFiles = Cards[at].Files is { Count: > 0 };
        Cards[at] = Cards[at] with { Text = value, Status = value.Length > 0 || hasFiles ? KnowledgeStatus.Confirmed : KnowledgeStatus.Skipped, Source = "you" };
    }

    /// <summary>
    /// How an AI answers a customer from approved knowledge only, for an agent's
    /// live preview, which opens in its own tone and ends with its role's own
    /// question (or nothing, for short replies).
    /// </summary>
    public static TeachAiAnswer AnswerFrom(IReadOnlyList<KnowledgeCard> cards, string question, string closing = "Is there anything else I can help with?", string opener = "Hi!")
    {
        var topic = TopicOf(question);
        var card = topic is null ? null : cards.FirstOrDefault(c => c.Topic == topic);
        if (card is null || card.Status != KnowledgeStatus.Confirmed || !card.HasContent)
            return new("Good question. I don't have that yet, so I'll check with the team and get back to you shortly.", null);
        // Someone asking for a person is handed over, not read the contact details.
        if (card.Topic == KnowledgeTopic.Handover)
            return new("Of course. I'll get someone from the team to help you right away.", card.Topic);
        // Only a file so far: the AI will read it once the API takes uploads.
        if (card.Text.Length == 0)
            return new($"{opener} Let me check our {card.Title.ToLowerInvariant()} and get back to you in a moment.", card.Topic);
        return new(string.Join(' ', new[] { opener, card.Text, closing }.Where(part => part.Length > 0)), card.Topic);
    }

    /// <summary>A question a customer might ask about a topic: a tap instead of typing.</summary>
    public static string SampleQuestion(KnowledgeTopic topic) => topic switch
    {
        KnowledgeTopic.Products => "What do you sell?",
        KnowledgeTopic.Prices => "How much does it cost?",
        KnowledgeTopic.Delivery => "Do you deliver to Abuja?",
        KnowledgeTopic.Payment => "Can I pay on delivery?",
        KnowledgeTopic.Hours => "Are you open on Sunday?",
        KnowledgeTopic.Returns => "Can I return something?",
        KnowledgeTopic.Handover => "Can I speak to a person?",
        _ => "Is there anything new?",
    };

    // ── Moving on ───────────────────────────────────────────────────────────

    private List<KnowledgeCard> Unchecked() =>
        Core.Where(c => c.Status == KnowledgeStatus.Found && c.Text.Length > 0).ToList();

    // Everything found still unchecked is right: kept, as found.
    private TeachAiLine ConfirmFound(string lead)
    {
        foreach (var card in Unchecked())
        {
            Cards[Cards.FindIndex(c => c.Topic == card.Topic)] = card with { Status = KnowledgeStatus.Confirmed };
            _learned.Add(card.Topic);
        }
        _reviewed = true;
        return NextGap(lead);
    }

    private TeachAiLine Accept(string lead)
    {
        var card = Cards[Index];
        Cards[Index] = card with { Status = card.HasContent ? KnowledgeStatus.Confirmed : KnowledgeStatus.Skipped };
        if (card.HasContent) _learned.Add(card.Topic);
        return Next(lead);
    }

    private TeachAiLine Skip()
    {
        Cards[Index] = Cards[Index] with { Status = KnowledgeStatus.Skipped };
        return NextGap("No problem, we can add it later.");
    }

    private TeachAiLine Next(string lead) => NextGap(lead);

    private TeachAiLine FollowUpOrNext(string lead)
    {
        var card = Cards[Index];
        if (_followedUp.Add(card.Topic) && FollowUpFor(card) is { } kind)
        {
            _followUp = kind;
            Stage = TeachAiStage.FollowUp;
            return Say($"{lead} {FollowUpQuestion(kind)}", card.Topic);
        }
        return NextGap(lead);
    }

    // A short question where an answer leaves an obvious one open.
    private static FollowUpKind? FollowUpFor(KnowledgeCard card) => card.Topic switch
    {
        KnowledgeTopic.Prices when !Mentions(card.Text, "deliver") => FollowUpKind.IncludesDelivery,
        KnowledgeTopic.Payment when !Mentions(card.Text, "deliver") => FollowUpKind.PayOnDelivery,
        KnowledgeTopic.Returns when !Digits().IsMatch(card.Text) && !Mentions(card.Text, "day") && !Mentions(card.Text, "week") => FollowUpKind.ReturnDays,
        // Ask for the person, then how to reach them.
        KnowledgeTopic.Handover when !Phone().IsMatch(card.Text) => FollowUpKind.HandoverNumber,
        _ => null,
    };

    private static string FollowUpQuestion(FollowUpKind kind) => kind switch
    {
        FollowUpKind.IncludesDelivery => "Does that include delivery?",
        FollowUpKind.PayOnDelivery => "Can customers pay on delivery?",
        FollowUpKind.HandoverNumber => "What's the best number to reach them on?",
        _ => "How many days do customers have to ask for a return?",
    };

    private static bool Mentions(string text, string word) => text.Contains(word, StringComparison.OrdinalIgnoreCase);

    // What next: anything found still to check, then each gap, then one open
    // question for anything else, then the summary.
    private TeachAiLine NextGap(string lead)
    {
        var found = Unchecked();
        if (found.Count > 0)
        {
            Stage = TeachAiStage.Review;
            _reviewTopics = found.Select(c => c.Topic).ToList();
            return Card($"{lead} Here's the rest of what I found.", found, "Is it right?");
        }
        if (!_checkpointShown && _gapsStarted && Covered >= 4 && Core.Any(c => c.Status == KnowledgeStatus.Missing))
        {
            _checkpointShown = true;
            Stage = TeachAiStage.Checkpoint;
            return Checkpoint(lead);
        }
        if (Core.FirstOrDefault(c => c.Status == KnowledgeStatus.Missing) is { } gap)
        {
            Stage = TeachAiStage.Asking;
            Index = Cards.FindIndex(c => c.Topic == gap.Topic);
            var intro = !_gapsStarted && _reviewed ? "Now a few things I couldn't find. " : "";
            _gapsStarted = true;
            return Say($"{lead} {intro}{Question(gap.Topic)}".Trim(), gap.Topic);
        }
        if (!_askedExtras)
        {
            _askedExtras = true;
            Stage = TeachAiStage.Extras;
            return Say($"{lead} Last thing: is there anything customers often ask that we haven't covered?");
        }
        return Finish(lead);
    }

    // A pause part way, as the Experience Specification's checkpoint: what it
    // has (confirmed), what it thinks it has (found, not checked) and what is
    // still to ask, under the owner's own headings. Aloud it names the topics
    // rather than reading every answer again; each shows as it is named.
    private TeachAiLine Checkpoint(string lead)
    {
        var known = Core.Where(c => c.Status == KnowledgeStatus.Confirmed && c.HasContent).ToList();
        var unsure = Core.Where(c => c.Status == KnowledgeStatus.Found && c.Text.Length > 0).ToList();
        var open = Core.Where(c => c.Status is KnowledgeStatus.Missing or KnowledgeStatus.Skipped).ToList();
        var screen = $"{lead} Quick check: here's what I have so far.".Trim();
        var middle = $"I've got {List(known.Select(c => About(c.Topic)))}."
            + (unsure.Count > 0 ? $" I think I have {List(unsure.Select(c => About(c.Topic)))}, if that's still right." : "")
            + (open.Count > 0 ? $" Still to cover: {List(open.Select(c => About(c.Topic)))}." : "");
        const string question = "Anything to fix before we carry on?";
        List<TeachAiItem> items =
        [
            .. known.Select(c => new TeachAiItem(c.Topic, c.Text, c.Source) { State = TeachAiItemState.Confirmed, Anchor = About(c.Topic) }),
            .. unsure.Select(c => new TeachAiItem(c.Topic, c.Text, c.Source) { State = TeachAiItemState.Unsure, Anchor = About(c.Topic) }),
            .. open.Select(c => new TeachAiItem(c.Topic, "", null) { State = TeachAiItemState.Open, Anchor = About(c.Topic) }),
        ];
        return Say(new TeachAiLine(TeachAiSpeaker.Agent, $"{screen} {middle} {question}") { Screen = screen, Items = items, Then = question });
    }

    private TeachAiLine Finish(string lead)
    {
        Step = TeachAiStep.Summary;
        Stage = TeachAiStage.Done;
        return Say($"{lead} That's everything I need. Here's what your AI will know about {Business}. Have a look, then approve it.");
    }

    // Open questions, in plain words, one at a time, inviting a file where
    // people have one. Anything else said in passing is still kept.
    private static string Question(KnowledgeTopic topic) => topic switch
    {
        KnowledgeTopic.Products => "What do you sell? A sentence or two is fine, or send me your catalogue.",
        KnowledgeTopic.Prices => "How do you price things? A range is fine, or send me a photo of your price list.",
        KnowledgeTopic.Delivery => "How do customers get their orders? Delivery, pickup, or both?",
        KnowledgeTopic.Payment => "How do customers pay you? Transfer, card, cash?",
        KnowledgeTopic.Hours => "When are you open, and where are you based?",
        KnowledgeTopic.Returns => "What happens if a customer wants to return or change something?",
        KnowledgeTopic.Handover => "When a customer needs something I can't help with, who should I pass them to? A name and a phone number is perfect.",
        _ => "Is there anything customers often ask that we haven't covered?",
    };

    private string Ack() => Thanks[_thanks++ % Thanks.Length];

    // ── Lines ───────────────────────────────────────────────────────────────

    private TeachAiLine Say(string text, KnowledgeTopic? topic = null) => Say(new TeachAiLine(TeachAiSpeaker.Agent, text, topic));

    private TeachAiLine Say(TeachAiLine line)
    {
        Lines.Add(line);
        return line;
    }

    // Opening words, the found items on a card, then the question. Aloud, the
    // items are said the way a person would ("You sell…, you offer…, and
    // you're open…"), not read out as a list; each shows as it is said.
    private TeachAiLine Card(string screen, IReadOnlyList<KnowledgeCard> cards, string then, KnowledgeTopic? topic = null)
    {
        var items = cards.Select(c => new TeachAiItem(c.Topic, c.Text, c.Source)).ToList();
        var spoken = string.Join(" ", items.Select((item, i) => Spoken(item, last: i > 0 && i == items.Count - 1)));
        return Say(new TeachAiLine(TeachAiSpeaker.Agent, $"{screen} {spoken} {then}", topic) { Screen = screen, Items = items, Then = then });
    }

    // One found item, aloud: "You sell handmade adire fabrics…". What the owner
    // said in their own words is read back as theirs.
    private static string Spoken(TeachAiItem item, bool last)
    {
        var lead = item.Source is null or "you" ? "You told me" : item.Topic switch
        {
            KnowledgeTopic.Products => "You sell",
            KnowledgeTopic.Prices => "Your prices are",
            KnowledgeTopic.Delivery => "You offer",
            KnowledgeTopic.Payment => "Customers can pay by",
            KnowledgeTopic.Hours => "You're open",
            KnowledgeTopic.Returns => "For returns,",
            KnowledgeTopic.Handover => "When I can't help, I'll pass customers to",
            _ => "Also,",
        };
        if (last) lead = "And " + char.ToLowerInvariant(lead[0]) + lead[1..];
        return $"{lead} {Sentence(item.Text)}";
    }

    /// <summary>
    /// The agent was interrupted <paramref name="said"/> characters into a line:
    /// what shows is cut to about as much as it said, ending "…". A line with
    /// found items keeps its card and its question whatever was said: they are
    /// what the person answers, so the voice stops but the content stays.
    /// </summary>
    public void Cut(TeachAiLine line, int said)
    {
        var at = Lines.LastIndexOf(line);
        if (at < 0 || said >= line.Text.Length) return;
        var opening = line.Display;
        if (line.Items.Count > 0)
        {
            if (said < line.ScreenEnd) Lines[at] = line with { Screen = Shorten(opening, said / (double)Math.Max(1, line.ScreenEnd)) };
            return;
        }
        if (said < line.ScreenEnd)
        {
            Lines[at] = line with { Screen = Shorten(opening, said / (double)Math.Max(1, line.ScreenEnd)), Then = null };
            return;
        }
        if (line.Then is null || said < line.ThenAt)
        {
            Lines[at] = line with { Screen = opening, Then = null };
            return;
        }
        Lines[at] = line with { Screen = opening, Then = Shorten(line.Then, (said - line.ThenAt) / (double)Math.Max(1, line.Then.Length)) };
    }

    private static string Shorten(string text, double share)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var keep = Math.Clamp((int)Math.Ceiling(words.Length * Math.Clamp(share, 0, 1)), 1, words.Length);
        return string.Join(' ', words.Take(keep)).TrimEnd(',', '.', ':', ';') + "…";
    }

    // ── Words about topics ──────────────────────────────────────────────────

    /// <summary>A topic as a label: on buttons, and read out before a found item.</summary>
    public static string Name(KnowledgeTopic topic) => topic switch
    {
        KnowledgeTopic.Products => "What you sell",
        KnowledgeTopic.Prices => "Prices",
        KnowledgeTopic.Delivery => "Delivery",
        KnowledgeTopic.Payment => "Payment",
        KnowledgeTopic.Hours => "Hours and location",
        KnowledgeTopic.Returns => "Returns",
        KnowledgeTopic.Handover => "Who I ask for help",
        _ => "Good to know",
    };

    // A topic in a sentence: "I've updated what you sell and delivery."
    private static string About(KnowledgeTopic topic) => topic switch
    {
        KnowledgeTopic.Products => "what you sell",
        KnowledgeTopic.Prices => "your prices",
        KnowledgeTopic.Delivery => "delivery",
        KnowledgeTopic.Payment => "payment",
        KnowledgeTopic.Hours => "your hours",
        KnowledgeTopic.Returns => "returns",
        KnowledgeTopic.Handover => "who I ask for help",
        _ => "other things to know",
    };

    private static string List(IEnumerable<string> items)
    {
        var all = items.Distinct().ToList();
        return all.Count switch
        {
            0 => "that",
            1 => all[0],
            _ => string.Join(", ", all.Take(all.Count - 1)) + " and " + all[^1],
        };
    }

    // "your WhatsApp Business profile" and "your Instagram bio" → "WhatsApp and Instagram".
    private static string Channels(IEnumerable<KnowledgeCard> cards) => List(cards.Select(c => c.Source switch
    {
        null => "workspace",
        var s when s.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase) => "WhatsApp",
        var s when s.Contains("Instagram", StringComparison.OrdinalIgnoreCase) => "Instagram",
        var s when s.Contains("TikTok", StringComparison.OrdinalIgnoreCase) => "TikTok",
        var s when s.Contains("Facebook", StringComparison.OrdinalIgnoreCase) => "Facebook",
        var s => s.StartsWith("your ", StringComparison.OrdinalIgnoreCase) ? s[5..] : s,
    }));

    // ── Understanding answers (the demo's rules) ────────────────────────────

    // Each part of an answer goes to the topic it is about, or the fallback
    // (what was asked). "Also" adds to what is there; "only", "not", "actually"
    // replace it. "The hours are wrong" on its own points at a topic to correct.
    private (List<KnowledgeTopic> Learned, KnowledgeTopic? Pointer, string? Unplaced) Learn(string said, KnowledgeTopic? fallback, bool replaceFallback = false)
    {
        var (changes, pointer, unplaced) = Plan(said, fallback, replaceFallback);
        foreach (var change in changes) Apply(change.Topic, change.Body, change.Replace);
        return (changes.Select(c => c.Topic).Distinct().ToList(), pointer, unplaced);
    }

    private sealed record Change(KnowledgeTopic Topic, string Body, bool Replace);

    // What an answer would change, part by part, before anything is changed.
    private (List<Change> Changes, KnowledgeTopic? Pointer, string? Unplaced) Plan(string said, KnowledgeTopic? fallback, bool replaceFallback = false)
    {
        var changes = new List<Change>();
        KnowledgeTopic? pointer = null;
        var unplaced = new List<string>();
        foreach (var clause in Clauses(said))
        {
            var topic = TopicOf(clause);
            var body = Body(clause);
            if (body.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 2 && Wrong().IsMatch(clause))
            {
                pointer ??= topic;
                continue;
            }
            if (body.Length == 0) continue;
            var target = topic ?? fallback;
            if (target is null)
            {
                unplaced.Add(Clean(body));
                continue;
            }
            changes.Add(new(target.Value, body, Replaces(clause) || (replaceFallback && target == fallback && !AddWords().IsMatch(clause))));
        }
        return (changes, pointer, unplaced.Count > 0 ? string.Join(" ", unplaced) : null);
    }

    private void Apply(KnowledgeTopic topic, string body, bool replace)
    {
        var at = Cards.FindIndex(c => c.Topic == topic);
        var card = Cards[at];
        var sentence = Clean(body);
        var keep = !replace && card.Text.Length > 0 && card.Status != KnowledgeStatus.Skipped;
        Cards[at] = card with
        {
            Text = keep ? $"{card.Text} {sentence}" : sentence,
            Status = KnowledgeStatus.Confirmed,
            Source = keep ? AndYou(card.Source) : "you",
        };
        _learned.Add(topic);
        _understood.Add(new(topic, sentence));
    }

    // What was found, with something the owner added: "your Instagram bio and you".
    private static string AndYou(string? source) =>
        source is null or "you" ? "you" : source.EndsWith(" and you", StringComparison.Ordinal) ? source : source + " and you";

    // An answer in parts: by sentence, then wherever "and", "but" or a comma
    // moves on to a different topic. "Kaftans, bags and shoes" stays whole.
    private static IEnumerable<string> Clauses(string said)
    {
        foreach (var sentence in SentenceBreak().Split(said))
        {
            var pieces = Joiner().Split(sentence);
            var parts = new List<(string Text, KnowledgeTopic? Topic)>();
            for (var i = 0; i < pieces.Length; i += 2)
            {
                var piece = pieces[i];
                var topic = TopicOf(piece);
                if (parts.Count > 0 && (topic is null || parts[^1].Topic is null || parts[^1].Topic == topic))
                    parts[^1] = (parts[^1].Text + pieces[i - 1] + piece, parts[^1].Topic ?? topic);
                else
                    parts.Add((piece, topic));
            }
            foreach (var (text, _) in parts)
            {
                var clean = text.Trim(' ', ',', '.', ';');
                if (clean.Length > 0) yield return clean;
            }
        }
    }

    /// <summary>What an answer is about, if it says: the most specific match wins.</summary>
    private static KnowledgeTopic? TopicOf(string text)
    {
        var t = text.ToLowerInvariant();
        if (ReturnsWords().IsMatch(t)) return KnowledgeTopic.Returns;
        if (HandoverWords().IsMatch(t) || Phone().IsMatch(t)) return KnowledgeTopic.Handover;
        if (PaymentWords().IsMatch(t)) return KnowledgeTopic.Payment;
        if (PriceWords().IsMatch(t)) return KnowledgeTopic.Prices;
        if (DeliveryWords().IsMatch(t)) return KnowledgeTopic.Delivery;
        if (HoursWords().IsMatch(t)) return KnowledgeTopic.Hours;
        if (ProductWords().IsMatch(t)) return KnowledgeTopic.Products;
        return null;
    }

    private static KnowledgeTopic? FileTopic(string names)
    {
        var n = names.ToLowerInvariant();
        if (FilePriceWords().IsMatch(n)) return KnowledgeTopic.Prices;
        if (FileProductWords().IsMatch(n)) return KnowledgeTopic.Products;
        if (FileReturnsWords().IsMatch(n)) return KnowledgeTopic.Returns;
        if (DeliveryWords().IsMatch(n)) return KnowledgeTopic.Delivery;
        return null;
    }

    private static bool Replaces(string clause) => !AddWords().IsMatch(clause) && ReplaceWords().IsMatch(clause);

    // What is left once "no", "actually", "the hours are wrong" and the like are taken off.
    private static string Body(string clause)
    {
        var text = clause.Trim();
        string before;
        do
        {
            before = text;
            text = LeadingFiller().Replace(text, "");
            text = WrongPhrase().Replace(text, "");
        } while (text != before);
        return text.Trim(' ', ',', '.', ';', ':');
    }

    private static string Sentence(string text) => text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?') ? text : text + ".";

    private static string Clean(string said)
    {
        var text = Naira(said.Trim());
        if (text.Length == 0) return text;
        text = char.ToUpperInvariant(text[0]) + text[1..];
        return Sentence(text);
    }

    // Amounts the way owners say and type them ("25k to 60k", "25,000 naira",
    // "N5000") written the one way: "₦25,000 to ₦60,000".
    private static string Naira(string text)
    {
        text = RangeK().Replace(text, m => $"{Amount(m.Groups[1].Value, 1000)}{m.Groups[2].Value}{Amount(m.Groups[3].Value, 1000)}");
        text = RangeNaira().Replace(text, m => $"{Amount(m.Groups[1].Value)}{m.Groups[2].Value}{Amount(m.Groups[3].Value)}");
        text = SingleK().Replace(text, m => Amount(m.Groups[1].Value, 1000));
        text = SingleNaira().Replace(text, m => Amount(m.Groups[1].Value));
        text = PrefixNaira().Replace(text, m => Amount(m.Groups[1].Value));
        return text;
    }

    private static string Amount(string digits, int times = 1) =>
        decimal.TryParse(digits.Replace(",", ""), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? "₦" + (value * times).ToString("#,0.##", System.Globalization.CultureInfo.InvariantCulture)
            : digits;

    private enum Said { Yes, No, Skip, Done, Other }

    /// <summary>A short answer on its own: yes, no, skip or done.</summary>
    public static bool IsQuickAnswer(string said) => Kind(said) != Said.Other;

    // Yes and no only when that is (nearly) all that was said: "Yes, and we
    // also ship to Ghana" is new information, not a yes.
    private static Said Kind(string said)
    {
        var s = Spaces().Replace(Punctuation().Replace(said.ToLowerInvariant(), " "), " ").Trim();
        if (s.Length == 0) return Said.Other;
        if (SkipWords().IsMatch(s)) return Said.Skip;
        if (DoneWords().IsMatch(s)) return Said.Done;
        if (YesWords().IsMatch(s)) return Said.Yes;
        if (NoWords().IsMatch(s)) return Said.No;
        var short_ = s.Split(' ').Length <= 4 && TopicOf(s) is null;
        if (short_ && YesStart().IsMatch(s)) return Said.Yes;
        if (short_ && NoStart().IsMatch(s)) return Said.No;
        return Said.Other;
    }

    [GeneratedRegex(@"^(?:(?:yes|yeah|yep|yup|correct|right|exactly|sure|ok|okay|perfect|great|good|fine|it is|spot on|all good|all correct|all right|everything'?s (?:right|correct|fine)|thanks|thank you|carry on|continue|go on|keep going|move on|let'?s (?:continue|go|carry on)|that'?s (?:right|correct|fine|good)|that is (?:right|correct)|looks (?:right|good|fine))\s*)+$")]
    private static partial Regex YesWords();

    [GeneratedRegex(@"^(?:no|nope|not quite|not really|wrong|incorrect|that'?s wrong|that is wrong|change something|change it|fix it|fix something|edit it|something'?s wrong|something is wrong|not all of it|some of it)$")]
    private static partial Regex NoWords();

    [GeneratedRegex(@"^(?:yes|yeah|yep|yup|sure|correct|it does|they do|we do|we can|they can)\b")]
    private static partial Regex YesStart();

    [GeneratedRegex(@"^(?:no|nope|not|it doesn'?t|they don'?t|we don'?t|we can'?t|they can'?t)\b")]
    private static partial Regex NoStart();

    [GeneratedRegex(@"^(?:skip|skip it|skip this|skip for now|later|not now|pass|next|come back to it|(?:i'?ll )?(?:do|add) (?:it|this|that) later)$")]
    private static partial Regex SkipWords();

    [GeneratedRegex(@"^(?:that'?s (?:everything|all|it)|that is (?:everything|all)|nothing(?: else)?|no(?:pe)?,? (?:that'?s (?:everything|all|it)|nothing(?: else)?)|i'?m (?:done|good)|all done|done|no thanks|no thank you)$")]
    private static partial Regex DoneWords();

    [GeneratedRegex(@"\b(?:return\w*|refund\w*|exchange\w*|swap|cancel\w*)\b")]
    private static partial Regex ReturnsWords();

    [GeneratedRegex(@"\b(?:hand (?:them |it )?(?:over|to)|pass (?:them |it |customers )?(?:on )?to|escalat\w*|take over|takes over|manager|supervisor|call me|contact me|reach me|my (?:sister|brother|wife|husband|staff|assistant|partner|manager)|person in charge|(?:talk|speak|chat) (?:to|with) (?:a |an )?(?:person|human|someone|somebody|manager|owner)|real person|customer (?:care|service))\b")]
    private static partial Regex HandoverWords();

    // A Nigerian phone number, as people write it: 0803 123 4567, +234 803 123 4567.
    [GeneratedRegex(@"(?:\+?234\s?|\b0)[789][01]\d(?:[\s-]?\d){7}\b")]
    private static partial Regex Phone();

    [GeneratedRegex(@"\b(?:pay|pays|paid|paying|payment\w*|transfer\w*|card|cards|cash|pos|bank|opay|moniepoint|paystack|flutterwave)\b")]
    private static partial Regex PaymentWords();

    [GeneratedRegex(@"₦|\bnaira\b|\bprice\w*|\bcost\w*|how much|\b\d+\s?k\b|\b\d{1,3}(?:,\d{3})+\b|\b\d{4,}\b|\bcheap\w*|\bexpensive\b|\bdiscount\w*")]
    private static partial Regex PriceWords();

    [GeneratedRegex(@"\b(?:deliver\w*|ship\w*|dispatch\w*|pick ?up|collect\w*|courier\w*|mainland|island|nationwide|waybill|send (?:it|orders))\b")]
    private static partial Regex DeliveryWords();

    [GeneratedRegex(@"\b(?:open\w*|clos\w*|hours?|monday|tuesday|wednesday|thursday|friday|saturday|sunday|weekends?|weekdays?|\d{1,2}\s?(?:am|pm)|located|address|based in|shop is|store is|office)\b")]
    private static partial Regex HoursWords();

    [GeneratedRegex(@"\b(?:sell\w*|make|made|stock\w*|offer\w*|kaftans?|fabrics?|adire|bags?|shoes?|dress\w*|products?|services?|clothes|clothing|accessor\w*|collection)\b")]
    private static partial Regex ProductWords();

    [GeneratedRegex(@"price|menu|rate|list|cost")]
    private static partial Regex FilePriceWords();

    [GeneratedRegex(@"catalog|catalogue|product|collection|lookbook")]
    private static partial Regex FileProductWords();

    [GeneratedRegex(@"return|refund|policy|terms")]
    private static partial Regex FileReturnsWords();

    [GeneratedRegex(@"\b(?:also|too|as well|additionally|plus)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AddWords();

    [GeneratedRegex(@"\b(?:only|no longer|not|don'?t|doesn'?t|isn'?t|aren'?t|actually|instead|now|changed?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReplaceWords();

    [GeneratedRegex(@"\b(?:wrong|not right|incorrect|out of date|outdated|old|not true)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Wrong();

    [GeneratedRegex(@"^(?:no|nope|yes|yeah|yep|ok|okay|actually|well|so|oh|and|but|also|um|uh)\b[,.\s]*", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingFiller();

    [GeneratedRegex(@"^(?:that'?s|that is|it'?s|it is|(?:the|our|my|your)?\s*(?:\w+\s){0,3}(?:is|are|was|were))\s+(?:wrong|not right|incorrect|out of date|outdated|old|not true)\b[,.;:\s]*", RegexOptions.IgnoreCase)]
    private static partial Regex WrongPhrase();

    [GeneratedRegex(@"(?<=[.!?;])\s+")]
    private static partial Regex SentenceBreak();

    // Kept in the split (the capture), so a part can be joined back as said.
    [GeneratedRegex(@"(,?\s+(?:and|but)\s+|,\s+)", RegexOptions.IgnoreCase)]
    private static partial Regex Joiner();

    [GeneratedRegex(@"\d")]
    private static partial Regex Digits();

    [GeneratedRegex(@"(?<![\w₦,.])(\d+(?:\.\d+)?)\s?k?(\s?(?:-|–|to)\s?)(\d+(?:\.\d+)?)\s?k\b", RegexOptions.IgnoreCase)]
    private static partial Regex RangeK();

    [GeneratedRegex(@"(?<![\w₦,.])(\d[\d,]*(?:\.\d+)?)(\s?(?:-|–|to)\s?)(\d[\d,]*(?:\.\d+)?)\s?(?:naira|ngn)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RangeNaira();

    [GeneratedRegex(@"(?<![\w₦,.])(\d+(?:\.\d+)?)\s?k\b", RegexOptions.IgnoreCase)]
    private static partial Regex SingleK();

    [GeneratedRegex(@"(?<![\w₦,.])(\d[\d,]*(?:\.\d+)?)\s?(?:naira|ngn)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SingleNaira();

    [GeneratedRegex(@"(?:₦|\bNGN\s?|\bN(?=\d))\s?(\d[\d,]*(?:\.\d+)?)")]
    private static partial Regex PrefixNaira();


    [GeneratedRegex(@"[.,!?;:]")]
    private static partial Regex Punctuation();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
