using Parrot.Web.Features.Dashboard.Knowledge;
using Parrot.Web.Services;

namespace Parrot.Web.Components.Pages.Dashboard.TeachAi;

/// <summary>
/// "Tell us about your business", before the first agent (designer and team,
/// 14 October 2026): one box for the business to say what it does, typed or
/// from a voice note, with links and files if it wants. Every way of creating
/// an agent goes through RequireAsync, which carries straight on once the
/// business has a base, or opens the dialog and carries on once it is saved.
/// Teach your AI (the guided conversation, with its voice orb) stays for
/// "Teach more" from an agent. In real mode the knowledge API does not exist
/// yet, so nothing is held back.
/// </summary>
public sealed class BusinessIntroState(IBusinessKnowledge knowledge, IKnowledgeMemory memory)
{
    private Func<Task>? _then;

    public bool IsOpen { get; private set; }

    /// <summary>Opened to add to what the business already said, not before a first agent.</summary>
    public bool Adding { get; private set; }

    public event Action? Changed;

    /// <summary>Whether business knowledge can be added to (the ways in are hidden when it can't).</summary>
    public bool CanAdd => knowledge.Available && knowledge.CanSaveIntro;

    public async Task RequireAsync(Func<Task> then)
    {
        if (!knowledge.Available || !knowledge.CanSaveIntro || await knowledge.HasApprovedAsync())
        {
            await then();
            return;
        }
        _then = then;
        Adding = false;
        IsOpen = true;
        Changed?.Invoke();
    }

    /// <summary>
    /// "Add to business knowledge" (AI Operations, an agent's page): the same
    /// dialog, to say more; every agent uses what is added. The one way to add
    /// business knowledge in Concept B (designer, 14 October 2026).
    /// </summary>
    public void AddMore()
    {
        if (!CanAdd) return;
        _then = null;
        Adding = true;
        IsOpen = true;
        Changed?.Invoke();
    }

    /// <summary>Closed without saving: whatever was asked for does not happen.</summary>
    public void Close()
    {
        IsOpen = false;
        _then = null;
        Changed?.Invoke();
    }

    /// <summary>Saved: kept as the business's knowledge, then on to what was asked for.</summary>
    public async Task SaveAsync(BusinessIntro intro)
    {
        await knowledge.SaveIntroAsync(intro);
        // The Knowledge page shows it too, as facts: one place for what every agent knows.
        if (memory.Available)
        {
            await memory.AddAsync(intro.Summary.Trim());
            foreach (var site in intro.Links.Where(IsWebsite))
            {
                try { await memory.ReadWebsiteAsync(site); }
                catch (Exception exception) when (exception is HttpRequestException or NotSupportedException) { }
            }
        }
        var then = _then;
        IsOpen = false;
        _then = null;
        Changed?.Invoke();
        if (then is not null) await then();
    }

    /// <summary>The social network a link is on (instagram, tiktok, facebook, x), or null for a website.</summary>
    public static string? NetworkOf(string link)
    {
        var host = HostOf(link);
        if (host is null) return null;
        if (host.EndsWith("instagram.com")) return "instagram";
        if (host.EndsWith("tiktok.com")) return "tiktok";
        if (host.EndsWith("facebook.com") || host.EndsWith("fb.com")) return "facebook";
        if (host is "x.com" or "twitter.com" || host.EndsWith(".x.com") || host.EndsWith(".twitter.com")) return "x";
        return null;
    }

    private static bool IsWebsite(string link) => HostOf(link) is not null && NetworkOf(link) is null;

    private static string? HostOf(string link)
    {
        var text = link.Trim();
        if (text.Length == 0) return null;
        if (!text.Contains("://")) text = "https://" + text;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Host.Contains('.') ? uri.Host.ToLowerInvariant() : null;
    }
}
