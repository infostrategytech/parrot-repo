namespace Parrot.Application.Email;

public class EmailSettings
{
    public const string SectionName = "EmailSettings";

    public required string SmtpHost { get; init; }

    public int SmtpPort { get; init; } = 587;

    public bool UseStartTls { get; init; } = true;

    public bool RequiresAuthentication { get; init; } = true;

    public bool SendInDevelopment { get; init; }

    public required string SmtpUser { get; init; }

    public required string SmtpPassword { get; init; }

    public required string FromAddress { get; init; }

    public required string FromName { get; init; }

    public required string BaseUrl { get; init; }
}
