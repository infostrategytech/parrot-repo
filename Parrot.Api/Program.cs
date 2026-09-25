using MongoDB.Driver;
using Microsoft.Extensions.Options;
using Parrot.Infrastructure.Messaging;
using Parrot.Infrastructure.Conversations;
using Parrot.Application.Conversations;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Anthropic;
using Parrot.Application.Agents;
using Parrot.Application.Auth;
using Parrot.Application.Email;
using Parrot.Application.Integrations;
using Parrot.Application.Integrations.BusinessEnrichment;
using Parrot.Application.Logging;
using Parrot.Application.Models;
using Parrot.Application.Utils;
using Parrot.Infrastructure.Agents;
using Parrot.Infrastructure.Auth;
using Parrot.Infrastructure.Data;
using Parrot.Infrastructure.Email;
using Parrot.Infrastructure.SocialMediaIntegrations;
using Parrot.Api.Middleware;
using Parrot.Api.Utils;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Configuration
builder.Services.Configure<FrontendSettings>(builder.Configuration.GetSection(FrontendSettings.SectionName));
builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection(JwtSettings.SectionName));
JwtSettings jwtSettings = builder.Configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>()
    ?? throw new InvalidOperationException("JwtSettings configuration is missing.");

if (!builder.Environment.IsDevelopment() &&
    jwtSettings.Secret is "YourSuperSecretKeyThatIsAtLeast32CharactersLong!")
{
    throw new InvalidOperationException(
        "JwtSettings:Secret must be changed from the default placeholder before running in a non-development environment.");
}

builder.Services.Configure<AuthSettings>(builder.Configuration.GetSection(AuthSettings.SectionName));
AuthSettings authSettings = builder.Configuration.GetSection(AuthSettings.SectionName).Get<AuthSettings>()
    ?? new AuthSettings();

// The fixed verification code stands in for the emailed one; letting it reach
// any other environment would let anyone confirm any address.
if (!builder.Environment.IsDevelopment() && !string.IsNullOrEmpty(authSettings.DevelopmentVerificationCode))
{
    throw new InvalidOperationException(
        "Auth:DevelopmentVerificationCode is set. It bypasses email verification and may only be configured for the Development environment.");
}

builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection(EmailSettings.SectionName));
builder.Services.Configure<WhatsAppSettings>(builder.Configuration.GetSection(WhatsAppSettings.SectionName));
builder.Services.AddWhatsAppCredentialProtection(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddScoped<IWhatsAppConnectionRepository, WhatsAppConnectionRepository>();
builder.Services.AddScoped<IWhatsAppConnectionService, WhatsAppConnectionService>();
builder.Services.Configure<FacebookSettings>(builder.Configuration.GetSection(FacebookSettings.SectionName));
builder.Services.Configure<KafkaSettings>(builder.Configuration.GetSection(KafkaSettings.SectionName));
builder.Services.Configure<BrightDataSettings>(builder.Configuration.GetSection(BrightDataSettings.SectionName));
builder.Services.Configure<AnthropicSettings>(builder.Configuration.GetSection(AnthropicSettings.SectionName));
AnthropicSettings anthropicSettings = builder.Configuration.GetSection(AnthropicSettings.SectionName).Get<AnthropicSettings>()
    ?? new AnthropicSettings();

// Database
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// Identity
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    // NIST SP 800-63B: length, a breach lookup and a common-password list do
    // more than character-class rules, and are far easier on the person typing.
    // PasswordPolicyValidator below carries the rest of the policy.
    options.Password.RequireDigit = false;
    options.Password.RequireLowercase = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequiredLength = authSettings.MinimumPasswordLength;
    options.Password.RequiredUniqueChars = 4;
    options.User.RequireUniqueEmail = true;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.AllowedForNewUsers = true;

    // Six digits typed into the app rather than a long token carried in a link:
    // the same code works on whichever device the person is signing up from.
    options.Tokens.EmailConfirmationTokenProvider = TokenOptions.DefaultEmailProvider;
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultTokenProviders()
.AddPasswordValidator<PasswordPolicyValidator>();

// JWT + Google OAuth Authentication
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultSignInScheme = "ExternalCookies";
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtSettings.Issuer,
        ValidAudience = jwtSettings.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.Secret)),
        ClockSkew = TimeSpan.Zero,
    };
})
.AddCookie("ExternalCookies", options =>
{
    options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
})
.AddGoogle(options =>
{
    options.SignInScheme = "ExternalCookies";
    options.ClientId = builder.Configuration["GoogleOAuth:ClientId"] ?? throw new InvalidOperationException("GoogleOAuth:ClientId is not configured.");
    options.ClientSecret = builder.Configuration["GoogleOAuth:ClientSecret"] ?? throw new InvalidOperationException("GoogleOAuth:ClientSecret is not configured.");
    options.CallbackPath = "/signin-google";
    options.Scope.Add("email");
    options.Scope.Add("profile");
});

// nginx terminates TLS and is the only route to this port, so the last hop of
// X-Forwarded-For is the client. Without this every request arrives from the
// proxy's address and all users share a single rate-limit bucket.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Rate Limiting
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    static FixedWindowRateLimiterOptions PerIp(int permitLimit, int windowMinutes) =>
        new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromMinutes(windowMinutes),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
        };

    // 5 registrations per IP per 15 minutes
    options.AddPolicy(RateLimitPolicies.Register, ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => PerIp(5, 15)));

    // 10 login attempts per IP per 15 minutes
    options.AddPolicy(RateLimitPolicies.Login, ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => PerIp(10, 15)));

    // 10 verify-email attempts per IP per 15 minutes
    options.AddPolicy(RateLimitPolicies.VerifyEmail, ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => PerIp(10, 15)));

    // 3 resend-verification emails per IP per 15 minutes
    options.AddPolicy(RateLimitPolicies.ResendVerification, ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => PerIp(3, 15)));

    // 3 forgot-password requests per IP per 15 minutes
    options.AddPolicy(RateLimitPolicies.ForgotPassword, ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => PerIp(3, 15)));

    // 5 reset-password attempts per IP per 15 minutes
    options.AddPolicy(RateLimitPolicies.ResetPassword, ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => PerIp(5, 15)));

    // 5 change-password attempts per IP per 15 minutes
    options.AddPolicy(RateLimitPolicies.ChangePassword, ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => PerIp(5, 15)));

    // 1000 WhatsApp webhook deliveries per IP per minute (Meta sends bursts)
    options.AddPolicy(RateLimitPolicies.WhatsAppWebhook, ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 1000,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0,
            }));

    // 1000 Facebook webhook deliveries per IP per minute (Meta sends bursts)
    options.AddPolicy(RateLimitPolicies.FacebookWebhook, ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 1000,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 0,
            }));
});

// Services
builder.Services.AddScoped<IJwtHelper, JwtHelper>();
builder.Services.AddScoped<IAgentRepository, AgentRepository>();
builder.Services.AddScoped<IAgentService, AgentService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddHttpClient<IPasswordBreachChecker, PwnedPasswordsBreachChecker>(client =>
{
    client.BaseAddress = new Uri("https://api.pwnedpasswords.com/");
    client.Timeout = TimeSpan.FromSeconds(3);
    client.DefaultRequestHeaders.Add("Add-Padding", "true");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Parrot-CRM");
});
builder.Services.AddSingleton<KafkaMessagePublisher>();
builder.Services.AddSingleton<IMessagePublisher>(sp => sp.GetRequiredService<KafkaMessagePublisher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<KafkaMessagePublisher>());
builder.Services.AddScoped<IWhatsAppWebhookService, WhatsAppWebhookService>();
builder.Services.AddScoped<IFacebookWebhookService, FacebookWebhookService>();
builder.Services.AddScoped<IFacebookConnectionRepository, FacebookConnectionRepository>();
builder.Services.AddScoped<IFacebookConnectionService, FacebookConnectionService>();
builder.Services.AddScoped<RequestContext>();
builder.Services.AddScoped<IRequestContext>(sp => sp.GetRequiredService<RequestContext>());
builder.Services.AddScoped(typeof(IAppLogger<>), typeof(AppLogger<>));
builder.Services.AddHttpClient<IBrightDataScraperService, BrightDataScraperService>(c =>
    c.BaseAddress = new Uri("https://api.brightdata.com/"));
builder.Services.AddSingleton(new AnthropicClient { ApiKey = anthropicSettings.ApiKey });
builder.Services.AddScoped<IBusinessProfileEnrichmentService, BusinessProfileEnrichmentService>();

builder.Services.Configure<MongoDbSettings>(builder.Configuration.GetSection(MongoDbSettings.SectionName));
builder.Services.AddSingleton<IMongoClient>(sp => new MongoClient(sp.GetRequiredService<IOptions<MongoDbSettings>>().Value.ConnectionString));
builder.Services.AddSingleton<IMessageDocumentMapper, WhatsAppMessageDocumentMapper>();
builder.Services.AddSingleton<IMessageDocumentMapper, FacebookMessageDocumentMapper>();
builder.Services.AddSingleton<IMessageDocumentMapperFactory, MessageDocumentMapperFactory>();
builder.Services.AddSingleton<IConversationStore, MongoConversationStore>();

// Registers every "_t" discriminator before the API reads anything from the collection.
Parrot.Domain.Entities.Messages.MessageSerializationRegistry.Register();
builder.Services.AddScoped<ConversationService>();
builder.Services.AddHttpClient<IWhatsAppSender, WhatsAppSender>();

// Controllers & OpenAPI
builder.Services.AddControllers().ConfigureApiBehaviorOptions(options =>
{
    Func<Microsoft.AspNetCore.Mvc.ActionContext, Microsoft.AspNetCore.Mvc.IActionResult> defaultResponseFactory = options.InvalidModelStateResponseFactory;
    options.InvalidModelStateResponseFactory = context =>
    {
        // Covers every provider webhook: a payload that fails model binding is rejected with a 400
        // before the controller runs, so without this the reason is never recorded anywhere.
        if (context.HttpContext.Request.Path.StartsWithSegments("/api/webhooks", StringComparison.OrdinalIgnoreCase))
        {
            ILogger logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("WebhookValidation");
            var errors = context.ModelState
                .Where(entry => entry.Value?.Errors.Count > 0)
                .SelectMany(entry => entry.Value!.Errors.Select(error => new
                {
                    Field = entry.Key,
                    Reason = string.IsNullOrEmpty(error.ErrorMessage) ? error.Exception?.Message : error.ErrorMessage,
                }));
            logger.LogWarning("Webhook validation failed. Path={Path} CorrelationId={CorrelationId} Errors={Errors}",
                context.HttpContext.Request.Path.Value,
                context.HttpContext.Response.Headers["X-Correlation-ID"].ToString(),
                System.Text.Json.JsonSerializer.Serialize(errors));
        }
        return defaultResponseFactory(context);
    };
});
builder.Services.AddOpenApi();

WebApplication app = builder.Build();

// Resolved eagerly so a platform missing its mapper fails the boot, not the first message of that kind.
app.Services.GetRequiredService<IMessageDocumentMapperFactory>();

if (builder.Configuration.GetValue<bool>("Database:ApplyMigrations"))
{
    using IServiceScope scope = app.Services.CreateScope();
    ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    db.Database.Migrate();
}

// Restores the client address and scheme before anything downstream reads them.
app.UseForwardedHeaders();

// Global Exception Handler (must be first in pipeline)
app.UseGlobalExceptionHandler();

// Request context: populates IRequestContext and opens the structured log scope
app.UseRequestContext();

// Capture raw body for webhook HMAC signature validation
app.UseRawBodyCapture();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
