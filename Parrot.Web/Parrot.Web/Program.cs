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
using Parrot.Web.Features.Dashboard.TaskRequests.State;
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
builder.Services.AddScoped<IBusinessSetupService, BusinessSetupService>();

builder.Services.AddGrpcClient<MessageVerificationService.MessageVerificationServiceClient>(o =>
{
    o.Address = new Uri(grpcBaseUrl);
});
builder.Services.AddScoped<IMessageVerificationGrpcClient, MessageVerificationGrpcClient>();

builder.Services.AddScoped<TokenStorageService>();
builder.Services.AddScoped<AuthState>();
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
builder.Services.AddScoped<AiAgentsState>();
builder.Services.AddScoped<CatalogState>();
builder.Services.AddScoped<ChannelsState>();
builder.Services.AddScoped<ContactsState>();
builder.Services.AddScoped<ConversationsState>();
builder.Services.AddScoped<QaInsightsState>();
builder.Services.AddScoped<TaskRequestsState>();
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
