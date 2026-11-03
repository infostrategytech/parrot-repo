using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Driver;
using Parrot.Application.Agents;
using Parrot.Application.Auth;
using Parrot.Application.Integrations;
using Parrot.Application.Teams;
using Parrot.Application.Workspaces;
using Parrot.APi.Grpc.Services;
using Parrot.Infrastructure.Agents;
using Parrot.Infrastructure.Data;
using Parrot.Infrastructure.Messaging;
using Parrot.Infrastructure.Teams;
using Parrot.Infrastructure.Workspaces;

var builder = WebApplication.CreateBuilder(args);

// JWT
builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection(JwtSettings.SectionName));
JwtSettings jwtSettings = builder.Configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>()
    ?? throw new InvalidOperationException("JwtSettings configuration is missing.");

if (!builder.Environment.IsDevelopment() &&
    jwtSettings.Secret is "YourSuperSecretKeyThatIsAtLeast32CharactersLong!")
{
    throw new InvalidOperationException(
        "JwtSettings:Secret must be changed from the default placeholder before running in a non-development environment.");
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
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
    });

// Omni-AI's service tokens carry agent:service, which sign-in never grants; the same policy as the API's.
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(ServiceScopes.PolicyName, policy => policy
        .RequireAuthenticatedUser()
        .RequireAssertion(context => ServiceScopes.HasScope(context.User, ServiceScopes.AgentService)));

// gRPC
builder.Services.AddGrpc();

// MongoDB
builder.Services.Configure<MongoDbSettings>(
    builder.Configuration.GetSection(MongoDbSettings.SectionName));

builder.Services.AddSingleton<IMongoClient>(sp =>
{
    MongoDbSettings settings = sp.GetRequiredService<IOptions<MongoDbSettings>>().Value;
    return new MongoClient(settings.ConnectionString);
});

// The repository reads and writes every platform's message document, so it needs their mappers, and the
// discriminators registered before anything is read from the collection (as the API does).
builder.Services.AddSingleton<IMessageDocumentMapper, WhatsAppMessageDocumentMapper>();
builder.Services.AddSingleton<IMessageDocumentMapper, FacebookMessageDocumentMapper>();
builder.Services.AddSingleton<IMessageDocumentMapper, TikTokMessageDocumentMapper>();
builder.Services.AddSingleton<IMessageDocumentMapper, InstagramMessageDocumentMapper>();
builder.Services.AddSingleton<IMessageDocumentMapperFactory, MessageDocumentMapperFactory>();
Parrot.Domain.Entities.Messages.MessageSerializationRegistry.Register();
builder.Services.AddSingleton<IMessageRepository, MongoMessageRepository>();

// Postgres, read only here: who owns a connection, and what a person may do in a workspace (Omni-AI's checks).
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddScoped<IConnectionRepository, ConnectionRepository>();
builder.Services.AddScoped<IConnectionService, ConnectionService>();
builder.Services.AddScoped<IWorkspaceRepository, WorkspaceRepository>();
builder.Services.AddScoped<ITeamRepository, TeamRepository>();
builder.Services.AddScoped<IWorkspaceConnectionDirectory, WorkspaceConnectionDirectory>();
builder.Services.AddScoped<IWorkspaceAccessService, WorkspaceAccessService>();
builder.Services.AddMemoryCache();
// Nothing here changes a team, so there is nothing to tell other instances; answers expire within seconds.
builder.Services.AddSingleton<IAccessCacheBroadcast, NoAccessCacheBroadcast>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGrpcService<MessageVerificationGrpcService>();
app.MapGrpcService<OwnershipGrpcService>();
app.MapGrpcService<UserValidationGrpcService>();
app.MapGet("/", () => "Parrot gRPC — use a gRPC client to connect.");

app.Run();
