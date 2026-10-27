using Parrot.Grpc.Contracts.Messaging.V1;
using Parrot.Web.Components;
using Parrot.Web.Features.Auth.ForgotPassword.Services;
using Parrot.Web.Features.Dashboard.AiAgents.State;
using Parrot.Web.Features.Dashboard.Catalog.State;
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
builder.Services.AddScoped<CatalogState>();
builder.Services.AddScoped<ChannelsState>();
builder.Services.AddScoped<ContactsState>();
builder.Services.AddScoped<ConversationsState>();
builder.Services.AddScoped<QaInsightsState>();
// Tasks: until the backend keeps them (tasks-backend-requests), the page says they are on the way and
// nothing is made up. Before this, every workspace saw the same sample requests.
builder.Services.AddSingleton<Parrot.Web.Features.Dashboard.TaskRequests.ITaskStore, Parrot.Web.Features.Dashboard.TaskRequests.UnavailableTaskStore>();
// The person's own time zone, read from their browser, so "Today" is their today.
builder.Services.AddScoped<Parrot.Web.Services.UserClock>();
builder.Services.AddScoped<TeamState>();

WebApplication app = builder.Build();

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
