using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Parrot.Catalog.Tools.Commands;

// Operator commands for the catalog. Every command that touches tenant rows takes an
// explicit --tenant, because RLS matches nothing otherwise (ADR-004).
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

Dictionary<string, string?> parsed = Args.Parse(args, out string? command);
if (command is null || command is "help" or "--help" or "-h")
{
    Console.WriteLine(Args.Usage);
    return command is null ? 1 : 0;
}

IConfiguration configuration = builder.Configuration;
try
{
    int exit = command switch
    {
        "migrate-db" => await MigrateDb.RunAsync(configuration),
        "seed" => await Seed.RunAsync(configuration, parsed),
        "migrate-type-version" => await MigrateTypeVersion.RunAsync(configuration, parsed),
        "mint-token" => MintToken.Run(configuration, parsed),
        "purge-tenant" => await PurgeTenant.RunAsync(configuration, parsed),
        "sweep-outbox" => await SweepOutbox.RunAsync(configuration, parsed),
        "reindex" => await Reindex.RunAsync(configuration, parsed),
        "publish-items" => await PublishItems.RunAsync(configuration, parsed),
        _ => Unknown(command),
    };
    return exit;
}
catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Parrot.Domain.Exceptions.DomainException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"Unknown command '{command}'.");
    Console.Error.WriteLine(Args.Usage);
    return 1;
}
