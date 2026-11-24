using Parrot.Application.Integrations;
using Parrot.Grpc.Contracts.Messaging.V1;
using Parrot.Web.Components;
using Parrot.Web.Features.Auth.ForgotPassword.Services;
using Parrot.Web.Features.Dashboard.AiAgents.State;
using Parrot.Web.Features.Dashboard.Channels.Services;
using Parrot.Web.Features.Dashboard.Channels.State;
using Parrot.Web.Features.Dashboard.Contacts.State;
using Parrot.Web.Components.Pages.Dashboard.BusinessSetup.Services;
using Parrot.Web.Components.Pages.Dashboard.Conversations;
using Parrot.Web.Features.Dashboard.QaInsights.State;
using Parrot.Web.Features.Dashboard.Workspace;
using Parrot.Web.Features.Profile;
using Parrot.Web.Features.Dashboard.Team.State;
using Parrot.Web.Features.Auth.ForgotPassword.State;
using Parrot.Web.Features.Auth.Login.Services;
using Parrot.Web.Features.Auth.Login.State;
using Parrot.Web.Features.Auth.Register.Services;
using Parrot.Web.Features.Auth.Register.State;
using Parrot.Web.Features.Auth.ResetPassword.Services;
using Parrot.Web.Features.Auth.ResetPassword.State;
using Parrot.Web.Features.Auth.State;
using Parrot.Web.Features.Auth.VerifyEmail.Services;
using Parrot.Web.Features.Auth.VerifyEmail.State;
using Parrot.Web.Services;
using Parrot.Web.State;
using Parrot.SharedComponents.Services;

var builder = WebApplication.CreateBuilder(args);

// Static assets are precompressed by MapStaticAssets; the rendered page HTML
// was going out raw at ~18 KB, past one TCP window, which costs an extra round
// trip. Only text/html is compressed here. The pages carry no secrets or
// tokens (sign-in state lives in the circuit, not the markup), so this does
// not open a BREACH-style oracle.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
    options.MimeTypes = ["text/html"];
});
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(o =>
    o.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(o =>
    o.Level = System.IO.Compression.CompressionLevel.Fastest);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

string apiBaseUrl = builder.Configuration["ApiSettings:BaseUrl"]!;
string grpcBaseUrl = builder.Configuration["GrpcSettings:BaseUrl"]!;

builder.Services.AddScoped<AuthFormState>();
builder.Services.AddHttpClient<ILoginService, LoginService>(c => c.BaseAddress = new Uri(apiBaseUrl));
builder.Services.AddHttpClient<IRegisterService, RegisterService>(c => c.BaseAddress = new Uri(apiBaseUrl));
builder.Services.AddHttpClient<IForgotPasswordService, ForgotPasswordService>(c => c.BaseAddress = new Uri(apiBaseUrl));
builder.Services.AddHttpClient<IResetPasswordService, ResetPasswordService>(c => c.BaseAddress = new Uri(apiBaseUrl));
builder.Services.AddHttpClient<IVerifyEmailService, VerifyEmailService>(c => c.BaseAddress = new Uri(apiBaseUrl));
// Authenticated clients share the current circuit's AuthState for session-expiry notifications.
// The activity handler feeds the hairline progress bar in the dashboard shell.
builder.Services.AddScoped<ActivityState>();
builder.Services.AddScoped<FeedbackService>();
builder.Services.AddScoped<HttpClient>(sp => new HttpClient(new ActivityHandler(sp.GetRequiredService<ActivityState>())
{
    InnerHandler = new SessionExpiryHandler(sp.GetRequiredService<AuthState>())
    {
        InnerHandler = new HttpClientHandler(),
    },
}) { BaseAddress = new Uri(apiBaseUrl) });
builder.Services.AddScoped<IIntegrationsService, IntegrationsService>();
// What the real API can connect today; the Channels screens offer only that.
builder.Services.AddSingleton<IChannelCapabilities, LiveChannelCapabilities>();
// Whether any channel is connected, for pages that only need that (the dashboard).
builder.Services.AddScoped<ChannelStatusLoader>();
builder.Services.AddScoped<IBusinessSetupService, BusinessSetupService>();
// The account's profile, for the welcome card and the settings page.
builder.Services.AddScoped<ProfileClient>();
// The owner's workspaces, for the switcher, the welcome card and the settings page.
builder.Services.AddScoped<WorkspaceClient>();
builder.Services.AddScoped<IWorkspaceSetupService, WorkspaceSetupService>();
builder.Services.AddScoped<WorkspacePrompt>();
// The owner's team, and invite links opened before signing in.
builder.Services.AddScoped<Parrot.Web.Features.Dashboard.Team.TeamClient>();
builder.Services.AddScoped<Parrot.Web.Features.Dashboard.Team.PendingInvite>();
// How checkout links behave (Settings > Payments), and the checkout page customers open without signing in.
builder.Services.AddScoped<Parrot.Web.Features.Settings.PaymentSettingsClient>();
// The workspace's wallet: what customers have paid it, and every movement in and out (Settings > Wallet).
builder.Services.AddScoped<Parrot.Web.Features.Settings.WalletClient>();
builder.Services.AddScoped<Parrot.Web.Features.Checkout.CheckoutClient>();

builder.Services.AddGrpcClient<MessageVerificationService.MessageVerificationServiceClient>(o =>
{
    o.Address = new Uri(grpcBaseUrl);
});
builder.Services.AddScoped<IMessageVerificationGrpcClient, MessageVerificationGrpcClient>();

builder.Services.AddScoped<TokenStorageService>();
builder.Services.AddScoped<AuthState>();
// One messaging-hub connection per circuit, shared by every page that wants pushes.
builder.Services.AddScoped<Parrot.Web.Services.Realtime.RealtimeClient>();
builder.Services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, Parrot.Web.Services.Realtime.RealtimeCircuitHandler>();
builder.Services.AddScoped<LoginState>();
builder.Services.AddScoped<RegisterState>();
builder.Services.AddScoped<ForgotPasswordState>();
builder.Services.AddScoped<ResetPasswordState>();
builder.Services.AddScoped<VerifyEmailState>();
builder.Services.AddScoped<AppState>();

// The Aurora shell: rail state, the typed face of aurora-interop.js, and the single toast slot.
builder.Services.AddScoped<AppShellState>();
builder.Services.AddScoped<AuroraInterop>();
builder.Services.AddScoped<ToastService>();

builder.Services.AddScoped<ConversationClient>();
builder.Services.AddScoped<CustomerClient>();
builder.Services.AddScoped<ICustomerLinks, CustomerLinkClient>();
// No photo-reading service yet; photos are not offered in the import modal until one exists.
builder.Services.AddSingleton<Parrot.Web.Features.Dashboard.Customers.ICustomerListReader, Parrot.Web.Features.Dashboard.Customers.UnavailableCustomerListReader>();
builder.Services.AddScoped<InboxUnreadState>();
// The API has none of the extra inbox actions yet, so the inbox hides them.
builder.Services.AddSingleton<IInboxFeatures, UnavailableInboxFeatures>();
// Business knowledge: no API for it yet, so the screens hide what they cannot show. With voice
// orientation on, "Teach your AI" is a voice conversation with the AI instead (it keeps what it learns).
if (builder.Configuration.GetValue<bool>("OmniAi:OrientationVoice"))
    builder.Services.AddSingleton<IBusinessKnowledge, OrientationBusinessKnowledge>();
else
    builder.Services.AddSingleton<IBusinessKnowledge, UnavailableBusinessKnowledge>();
// The AI service (Omni-AI), called as the signed-in person: it reads the user, workspace and
// permissions from the session token. Streams (orientation answers, its forms) get their own
// client with no timeout; each is cancelled by the page reading it. A free ngrok tunnel answers
// a request it takes for a browser with its warning page, unless told to skip it.
Uri? omniBaseUrl = Uri.TryCreate(builder.Configuration["OmniAi:BaseUrl"], UriKind.Absolute, out var omniUri) ? omniUri : null;
builder.Services.AddScoped(sp => new OmniAiClient(
    new HttpClient(new ActivityHandler(sp.GetRequiredService<ActivityState>())
    {
        InnerHandler = new ThirdPartyCallLogger("Omni-AI", sp.GetRequiredService<ILogger<ThirdPartyCallLogger>>()) { InnerHandler = new HttpClientHandler() },
    })
    {
        BaseAddress = omniBaseUrl,
        DefaultRequestHeaders = { { "ngrok-skip-browser-warning", "1" } },
    },
    sp.GetRequiredService<AuthState>()));
builder.Services.AddScoped(sp => new OmniAiStreams(new HttpClient(
    new ThirdPartyCallLogger("Omni-AI", sp.GetRequiredService<ILogger<ThirdPartyCallLogger>>()) { InnerHandler = new HttpClientHandler() })
{
    BaseAddress = omniBaseUrl,
    Timeout = Timeout.InfiniteTimeSpan,
    DefaultRequestHeaders = { { "ngrok-skip-browser-warning", "1" } },
}));
builder.Services.AddScoped<OrientationApi>();
builder.Services.AddScoped<Parrot.SharedComponents.Services.AiVoiceInterop>();
// "Teach your AI" before the first agent, and the browser's voice for it.
builder.Services.AddScoped<Parrot.Web.Components.Pages.Dashboard.TeachAi.TeachAiState>();
// "Tell us about your business", asked once before the first agent (skipped while there is no knowledge API).
builder.Services.AddScoped<Parrot.Web.Components.Pages.Dashboard.TeachAi.BusinessIntroState>();
builder.Services.AddScoped<Parrot.Web.Components.Pages.Dashboard.AiAgents.AgentGate>();
builder.Services.AddScoped<Parrot.SharedComponents.Services.VoiceInterop>();
// With the AI orchestrator connected (OmniAi:Enabled), agents live in the
// API, the AI Operations figures come from the activity it reports, and a
// turned-on agent really answers. Without it, agents are kept for the session
// only and saved as drafts, and the figures stay hidden.
if (builder.Configuration.GetValue<bool>("OmniAi:Enabled"))
{
    // OmniAi:AgentSetup: agents and their channels kept by the AI service itself (/api/v1/agents),
    // instead of Parrot's api/agent. Needs OmniAi:BaseUrl and the service on the same signing key.
    if (builder.Configuration.GetValue<bool>("OmniAi:AgentSetup"))
        builder.Services.AddScoped<Parrot.Web.Features.Dashboard.AiAgents.IAgentStore, Parrot.Web.Features.Dashboard.AiAgents.OmniAgentStore>();
    else
        builder.Services.AddScoped<Parrot.Web.Features.Dashboard.AiAgents.IAgentStore, Parrot.Web.Features.Dashboard.AiAgents.ApiAgentStore>();
    builder.Services.AddScoped<Parrot.Web.Features.Dashboard.AiAgents.IAiOperationsFeed, Parrot.Web.Features.Dashboard.AiAgents.ApiAiOperationsFeed>();
    builder.Services.AddSingleton<Parrot.Web.Features.Dashboard.AiAgents.IAgentCapabilities, Parrot.Web.Features.Dashboard.AiAgents.OmniAiAgentCapabilities>();
}
else
{
    builder.Services.AddScoped<Parrot.Web.Features.Dashboard.AiAgents.IAgentStore, Parrot.Web.Features.Dashboard.AiAgents.SessionAgentStore>();
    builder.Services.AddSingleton<Parrot.Web.Features.Dashboard.AiAgents.IAiOperationsFeed, Parrot.Web.Features.Dashboard.AiAgents.UnavailableAiOperationsFeed>();
    builder.Services.AddSingleton<Parrot.Web.Features.Dashboard.AiAgents.IAgentCapabilities, Parrot.Web.Features.Dashboard.AiAgents.LiveAgentCapabilities>();
}
builder.Services.AddScoped<AiAgentsState>();
// "Tell Tolu its job" left halfway, per agent, for the session (picked up at the next topic).
builder.Services.AddScoped<Parrot.Web.Features.Dashboard.AiAgents.JobConversationDrafts>();
// Writing an agent's job description: until the agents API writes them, the
// basic writer keeps the owner's words as they said them, and a test as a
// customer stays hidden (it needs the agent to answer).
builder.Services.AddSingleton<Parrot.Web.Features.Dashboard.AiAgents.IAgentBriefing, Parrot.Web.Features.Dashboard.AiAgents.BasicAgentBriefing>();
// Getting to know the job (its topics, questions and approvals) needs the AI: until the agents API
// can read, hear and conclude, the job description is written from the owner's first answer.
builder.Services.AddSingleton<Parrot.Web.Features.Dashboard.AiAgents.IJobInterview, Parrot.Web.Features.Dashboard.AiAgents.UnavailableJobInterview>();
// The business's knowledge as facts (what an agent uses): no API for it yet.
builder.Services.AddSingleton<Parrot.Web.Features.Dashboard.Knowledge.IKnowledgeMemory, Parrot.Web.Features.Dashboard.Knowledge.UnavailableKnowledgeMemory>();
// The kind of business, once for the business; and which agents have been tested (both for the session).
builder.Services.AddScoped<Parrot.Web.Features.Dashboard.AiAgents.BusinessKind>();
builder.Services.AddScoped<Parrot.Web.Features.Dashboard.AiAgents.AgentReadiness>();
// Knowledge before its store exists: the page, empty, kept in the browser, when "Knowledge:Preview" is on.
builder.Services.AddScoped<Parrot.Web.Features.Dashboard.Knowledge.KnowledgePreview>();
// The Knowledge page from the AI service's business knowledge, read-only, when "OmniAi:Knowledge" is on.
builder.Services.AddScoped<Parrot.Web.Features.Dashboard.Knowledge.OmniKnowledgeMemory>();
// Products & Services: kept by the catalog service (CatalogApi:BaseUrl), called as the signed-in person.
// Without one, items last for the session.
if (Uri.TryCreate(builder.Configuration["CatalogApi:BaseUrl"], UriKind.Absolute, out var catalogUri))
    builder.Services.AddScoped<Parrot.Web.Features.Dashboard.Catalog.ICatalogStore>(sp => new Parrot.Web.Features.Dashboard.Catalog.ApiCatalogStore(
        new HttpClient(new ActivityHandler(sp.GetRequiredService<ActivityState>())
        {
            InnerHandler = new SessionExpiryHandler(sp.GetRequiredService<AuthState>()) { InnerHandler = new HttpClientHandler() },
        }) { BaseAddress = catalogUri },
        sp.GetRequiredService<AuthState>(),
        sp.GetRequiredService<ILogger<Parrot.Web.Features.Dashboard.Catalog.ApiCatalogStore>>()));
else
    builder.Services.AddScoped<Parrot.Web.Features.Dashboard.Catalog.ICatalogStore, Parrot.Web.Features.Dashboard.Catalog.SessionCatalogStore>();
// The backend does not expose owned social content or extraction yet: posts and post links are not offered.
builder.Services.AddScoped<Parrot.Web.Features.Dashboard.Catalog.IOfferingDiscovery, Parrot.Web.Features.Dashboard.Catalog.UnavailableOfferingDiscovery>();
builder.Services.AddScoped<Parrot.Web.Features.Dashboard.Catalog.IOfferingDraftReader, Parrot.Web.Features.Dashboard.Catalog.UnavailableOfferingDraftReader>();
// No service reads a photo of a price list yet, so "Snap a photo" is not offered.
builder.Services.AddSingleton<Parrot.Web.Features.Dashboard.Catalog.IPriceListReader, Parrot.Web.Features.Dashboard.Catalog.UnavailablePriceListReader>();
builder.Services.AddScoped<ChannelsState>();
builder.Services.AddScoped<ContactsState>();
builder.Services.AddScoped<ConversationsState>();
builder.Services.AddScoped<QaInsightsState>();
// Tasks, as the API keeps them for the session's workspace.
builder.Services.AddScoped<Parrot.Web.Features.Dashboard.TaskRequests.ITaskStore, Parrot.Web.Features.Dashboard.TaskRequests.ApiTaskStore>();
// The person's own time zone, read from their browser, so "Today" is their today.
builder.Services.AddScoped<Parrot.Web.Services.UserClock>();
// Tools: the screens are ready, the API isn't yet, so these say so rather than pretend (integrations backend requests).
builder.Services.AddSingleton<Parrot.Web.Features.Dashboard.Tools.IWorkspaceTools, Parrot.Web.Features.Dashboard.Tools.UnavailableWorkspaceTools>();
builder.Services.AddSingleton<Parrot.Web.Features.Dashboard.Tools.IToolActionRequests, Parrot.Web.Features.Dashboard.Tools.UnavailableToolActionRequests>();
builder.Services.AddSingleton<Parrot.Web.Features.Settings.IBusinessDetailsStore, Parrot.Web.Features.Settings.UnavailableBusinessDetails>();
builder.Services.AddScoped<TeamState>();
// Settings, sign-in and the team. Where the API has nothing yet, these say so rather than pretend
// (settings-, profile- and team-backend-requests).
builder.Services.AddScoped<Parrot.Web.Components.Pages.Dashboard.Settings.SettingsReturn>();
builder.Services.AddScoped<Parrot.Web.Features.Settings.WorkspaceCurrency>();
builder.Services.AddScoped<Parrot.Web.Features.Auth.ResetPassword.State.PasswordResetHandoff>();
builder.Services.AddScoped<Parrot.Web.Features.Auth.ResetPassword.State.PasswordResetSignOut>();
builder.Services.AddScoped<Parrot.Web.Features.Auth.ResetPassword.Services.IPasswordResetPreview, Parrot.Web.Features.Auth.ResetPassword.Services.UnavailablePasswordResetPreview>();
// The Contact page's form: the backend can't receive website messages yet, so the page offers the email instead.
builder.Services.AddScoped<Parrot.Web.Services.IContactMessages, Parrot.Web.Services.UnavailableContactMessages>();
// The sign-in waiting for its two-factor code, and the browser's "don't ask again on this device".
builder.Services.AddScoped<Parrot.Web.Features.Auth.TwoFactor.TwoFactorSignInState>();
builder.Services.AddScoped<Parrot.Web.Features.Auth.TwoFactor.RememberedDeviceStorage>();
builder.Services.AddSingleton<Parrot.Web.Features.Auth.TwoFactor.ITwoFactorSignIn, Parrot.Web.Features.Auth.TwoFactor.UnavailableTwoFactorSignIn>();
// Pop-ups and browser notifications while yára is open; Settings asks it for a test.
builder.Services.AddScoped<Parrot.Web.Features.Notifications.NotificationPopups>();
builder.Services.AddSingleton<Parrot.Web.Features.Notifications.INotificationFeed, Parrot.Web.Features.Notifications.UnavailableNotificationFeed>();
builder.Services.AddSingleton<Parrot.Web.Features.Settings.INotificationSettingsStore, Parrot.Web.Features.Settings.UnavailableNotificationSettings>();
builder.Services.AddSingleton<Parrot.Web.Features.Settings.IActivityLog, Parrot.Web.Features.Settings.UnavailableActivityLog>();
builder.Services.AddSingleton<Parrot.Web.Features.Settings.IAccountSecurity, Parrot.Web.Features.Settings.UnavailableAccountSecurity>();
builder.Services.AddSingleton<Parrot.Web.Features.Profile.IEmailChange, Parrot.Web.Features.Profile.UnavailableEmailChange>();
builder.Services.AddSingleton<Parrot.Web.Features.Profile.IProfilePhoto, Parrot.Web.Features.Profile.UnavailableProfilePhoto>();
// A person brought in by an agent: the API creates and assigns that task (tasks- and agents-backend-requests).
builder.Services.AddSingleton<Parrot.Web.Features.Dashboard.TaskRequests.ICustomerHandoverTasks, Parrot.Web.Features.Dashboard.TaskRequests.UnavailableCustomerHandoverTasks>();
// Teammate questions have no real endpoint yet; never register local fixtures in this branch.
builder.Services.AddSingleton<Parrot.Web.Features.Dashboard.TaskRequests.ITaskQuestions, Parrot.Web.Features.Dashboard.TaskRequests.UnavailableTaskQuestions>();
builder.Services.AddScoped<Parrot.Web.Features.Dashboard.TaskRequests.TaskReplyDraft>();

WebApplication app = builder.Build();

// Where the Knowledge page reads from, said once: a page showing "Coming in phase 6" is a switch, not a bug.
app.Logger.LogInformation("knowledge.page Source={Source} OmniBaseUrl={OmniBaseUrl}",
    Parrot.Web.Features.Dashboard.Knowledge.OmniKnowledgeMemory.IsOn(app.Configuration, "OmniAi:Knowledge") ? "omni (read-only)"
        : Parrot.Web.Features.Dashboard.Knowledge.OmniKnowledgeMemory.IsOn(app.Configuration, "Knowledge:Preview") ? "preview (this browser only)"
        : "placeholder (OmniAi:Knowledge is off)",
    omniBaseUrl?.ToString() ?? "(OmniAi:BaseUrl not set)");

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseResponseCompression();
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(Parrot.Web.Client._Imports).Assembly);

app.Run();
