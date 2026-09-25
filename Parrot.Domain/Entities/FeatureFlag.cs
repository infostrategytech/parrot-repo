namespace Parrot.Domain.Entities;

// A switch someone can flip in the database without a deploy. The key is the
// primary key; a missing row means "use the configured default".
public class FeatureFlag
{
    public required string Key { get; set; }

    public bool Enabled { get; set; }

    public string? Note { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
