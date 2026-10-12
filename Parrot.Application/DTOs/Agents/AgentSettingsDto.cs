namespace Parrot.Application.DTOs.Agents;

/// <summary>
/// What an agent does and how, as the owner set it on the AI Agents screens
/// and as the orchestrator reads it. The choices with a fixed set of values
/// (its job, tone, reply length, emoji, the knowledge topics left out) travel
/// as their names, so this one shape serves the wire, the settings column and
/// the orchestrator whatever enums the screens use.
/// </summary>
public class AgentSettingsDto
{
    /// <summary>The job it was set up for: Sales, Support, Orders or Custom.</summary>
    public string Role { get; set; } = "Support";

    /// <summary>Build my own: the job's name in the owner's words.</summary>
    public string CustomRole { get; set; } = string.Empty;

    /// <summary>Who it passes a customer to when it cannot help. Empty: the owner, in the Inbox.</summary>
    public string HandoverTo { get; set; } = string.Empty;

    public bool CanOfferDiscounts { get; set; }

    public bool CanApproveRefunds { get; set; }

    /// <summary>Warm, Professional or Upbeat.</summary>
    public string Tone { get; set; } = "Warm";

    /// <summary>Concise, Balanced or Detailed.</summary>
    public string Length { get; set; } = "Balanced";

    /// <summary>None, Light or Expressive.</summary>
    public string Emoji { get; set; } = "Light";

    /// <summary>Its first message. Empty: one written for its role.</summary>
    public string Greeting { get; set; } = string.Empty;

    /// <summary>Words and phrases it never says.</summary>
    public List<string> AvoidPhrases { get; set; } = [];

    public bool CanRecommend { get; set; } = true;

    public bool CanCheckOrders { get; set; }

    /// <summary>Asks for a name and phone number before handing over.</summary>
    public bool CanTakeDetails { get; set; } = true;

    public bool HandOverWhenUpset { get; set; } = true;

    public bool HandOverAfterMisses { get; set; } = true;

    public bool HandOverBigOrders { get; set; }

    /// <summary>The order value, in naira, above which a person closes the sale.</summary>
    public int BigOrderNaira { get; set; } = 50000;

    /// <summary>What only this agent should know.</summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>Topics of the shared business knowledge this agent leaves out, by name (Products, Prices, Delivery...).</summary>
    public List<string> LeftOut { get; set; } = [];
}
