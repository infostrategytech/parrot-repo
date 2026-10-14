using Parrot.SharedComponents.DesignSystem;
using Parrot.Web.Features.Dashboard.AiAgents;

namespace Parrot.Web.Components.Pages.Dashboard.AiAgents;

/// <summary>
/// The jobs an agent can be created for (designer, 13 October
/// 2026): what a small business hires an assistant to do on its chats. Each
/// maps to one of the roles the settings already know, for its sensible
/// defaults, and suggests what to try it with. One job per agent; an owner
/// can have several agents.
/// </summary>
public sealed record AgentUseCase(
    string Id,
    string Name,
    string Does,
    IconName Icon,
    AgentRoleTemplate Role,
    IReadOnlyList<string> TryWith)
{
    public const string Custom = "custom";

    public static readonly IReadOnlyList<AgentUseCase> All =
    [
        new("support", "Customer support", "Answers questions about what you sell, prices, delivery and opening hours.",
            IconName.RiCustomerService2Fill, AgentRoleTemplate.Support,
            ["How much is delivery?", "What time do you open?", "I'm not happy with my order"]),
        new("sales", "Sales & orders", "Helps customers choose, takes their order and shares how to pay.",
            IconName.RiShoppingBag3Fill, AgentRoleTemplate.Sales,
            ["I'd like to order 2 kaftans", "Can I get a discount?", "How do I pay?"]),
        new("tracking", "Order tracking", "Tells customers where their order is and when it will arrive.",
            IconName.RiTruckFill, AgentRoleTemplate.Orders,
            ["Where is my order?", "My order is late", "Can I change my delivery address?"]),
        new("bookings", "Bookings & appointments", "Books customers in at a time that suits them, and reminds them.",
            IconName.RiCalendarCheckFill, AgentRoleTemplate.Custom,
            ["Can I book for Saturday?", "I need to cancel my appointment", "What times are free tomorrow?"]),
        new("followups", "Follow-ups", "Reminds customers who asked about something but didn't buy.",
            IconName.RiRefreshFill, AgentRoleTemplate.Sales,
            ["I'm still thinking about it", "Is it still available?", "Can you do a better price?"]),
        new(Custom, "Something else", "Describe the job in your own words.",
            IconName.RiEditFill, AgentRoleTemplate.Custom,
            ["Hi, can you help me?", "How much is it?", "I want to speak to someone"]),
    ];

    public static AgentUseCase? Of(string? id) => All.FirstOrDefault(useCase => useCase.Id == id);

    /// <summary>The job as owners read it: its name, or the owner's own for Something else.</summary>
    public static string NameOf(AgentSettings settings) =>
        settings.UseCase != Custom && Of(settings.UseCase) is { } useCase ? useCase.Name
        : settings.Role != AgentRoleTemplate.Custom && settings.UseCase is null ? AgentRole.Of(settings.Role).Name
        : string.IsNullOrWhiteSpace(settings.CustomRole) ? "Your own job" : settings.CustomRole.Trim();
}

/// <summary>
/// The kind of business, for small businesses that sell and serve on their
/// chats: it shapes the first draft of the job description, the questions to
/// try it with, and what an agent there must be careful about.
/// </summary>
public sealed record AgentIndustry(string Id, string Name, string Examples, string? Careful, IReadOnlyList<string> TryWith)
{
    public static readonly IReadOnlyList<AgentIndustry> All =
    [
        new("fashion", "Fashion & clothing", "Clothes, fabrics, shoes, bags", "Never promise a delivery date you can't confirm.",
            ["Do you have it in a size 12?", "Can I see other colours?"]),
        new("beauty", "Beauty & personal care", "Salons, spas, skincare, hair", "Never promise results from a treatment or product.",
            ["Is this good for oily skin?", "How long does braiding take?"]),
        new("food", "Food & restaurants", "Restaurants, catering, bakeries", "Never say a dish is free of an allergen unless you're sure.",
            ["Does it contain nuts?", "Do you cater for events?"]),
        new("groceries", "Groceries & supermarkets", "Groceries, drinks, household items", "Never confirm an item is in stock without checking.",
            ["Do you have Golden Penny semovita?", "Do you deliver on Sundays?"]),
        new("health", "Health & pharmacy", "Clinics, labs, pharmacies", "Never give medical advice or a diagnosis.",
            ["What are your test prices?", "Do I need to fast before a blood test?"]),
        new("electronics", "Phones & electronics", "Phones, gadgets, repairs", "Never promise a warranty you don't offer.",
            ["Is this phone UK used or new?", "Do you fix cracked screens?"]),
        new("home", "Home & furniture", "Furniture, decor, appliances", "Never confirm measurements you haven't checked.",
            ["Can you make it in a different size?", "Do you assemble on delivery?"]),
        new("property", "Real estate & property", "Rentals, sales, short lets", "Never confirm a price or a viewing without checking with you.",
            ["Is the 2-bedroom in Lekki still available?", "Can I view it this weekend?"]),
        new("education", "Education & training", "Schools, tutors, courses", "Never promise a result or a certificate you don't give.",
            ["When does the next class start?", "How much are the fees?"]),
        new("travel", "Travel, hotels & events", "Hotels, tours, event venues", "Never confirm a booking until it's paid for.",
            ["Is the hall free on 20 December?", "Do you have airport pickup?"]),
        new("logistics", "Logistics & delivery", "Couriers, dispatch, haulage", "Never promise a delivery time you can't see.",
            ["How much to send a parcel to Abuja?", "Where is my package?"]),
        new("finance", "Finance & payments", "Lending, savings, payments", "Never ask for a PIN, password or OTP.",
            ["How do I apply for a loan?", "My transfer hasn't arrived"]),
        new("services", "Professional services", "Lawyers, accountants, consultants", "Never give legal or financial advice.",
            ["How much is a consultation?", "Can you register my business?"]),
        new("other", "Something else", "Any other kind of business", null,
            ["What do you do?", "How can I reach you?"]),
    ];

    public static AgentIndustry? Of(string? id) => All.FirstOrDefault(industry => industry.Id == id);
}

/// <summary>
/// What creating an agent from a job description sets: its job and kind of
/// business, the defaults its job brings (as choosing a role did), and the
/// switches its job description implies, so creating it asks none.
/// Every one of them can be changed on the agent's page.
/// </summary>
public static class AgentBriefDefaults
{
    public static void Apply(AgentForm form, AgentUseCase useCase, string customJob, string? industry, string website, AgentBrief brief)
    {
        var settings = form.Settings;
        settings.Role = useCase.Role;
        form.HasRole = true;
        settings.UseDefaultsFor(useCase.Role);
        settings.UseCase = useCase.Id;
        settings.Industry = industry;
        settings.Website = website.Trim();
        settings.Brief = brief.Copy();
        settings.CustomRole = useCase.Id == AgentUseCase.Custom ? customJob.Trim()
            : useCase.Role == AgentRoleTemplate.Custom ? useCase.Name
            : string.Empty;
        if (useCase.Id == "tracking") settings.CanCheckOrders = true;
        if (useCase.Id is "sales" or "followups") settings.CanRecommend = true;
        form.Job = brief.Job.Trim();

        var sentences = brief.Sentences().Select(sentence => sentence.ToLowerInvariant()).ToList();
        var never = string.Join(" ", sentences.Where(sentence => sentence.Contains("never") || sentence.Contains("don't") || sentence.Contains("do not")));
        var callMe = string.Join(" ", sentences.Where(sentence => sentence.Contains("bring me in") || sentence.Contains("tell me") || sentence.Contains("call me") || sentence.Contains("let me know")));
        if (never.Contains("discount")) settings.CanOfferDiscounts = false;
        if (never.Contains("refund") || callMe.Contains("refund")) settings.CanApproveRefunds = false;
        if (callMe.Contains("upset") || callMe.Contains("angry") || callMe.Contains("complain")) settings.HandOverWhenUpset = true;
        if (callMe.Contains("big order") || callMe.Contains("large order")) settings.HandOverBigOrders = true;
    }
}
