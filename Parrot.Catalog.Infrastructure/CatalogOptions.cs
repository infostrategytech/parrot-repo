namespace Parrot.Catalog.Infrastructure;

/// <summary>The "Catalog" configuration section. Secrets arrive through environment or Secrets Manager, never files in the repo.</summary>
public sealed class CatalogOptions
{
    public const string SectionName = "Catalog";

    public CatalogConnectionStrings ConnectionStrings { get; set; } = new CatalogConnectionStrings();

    public CatalogCacheOptions Cache { get; set; } = new CatalogCacheOptions();

    public CatalogRelayOptions Relay { get; set; } = new CatalogRelayOptions();

    public CatalogPhotoOptions Photos { get; set; } = new CatalogPhotoOptions();

    public bool ApplyMigrations { get; set; }
}

public sealed class CatalogConnectionStrings
{
    // Owns the schema; runs migrations and operator tools.
    public string Migrator { get; set; } = string.Empty;

    // Member of catalog_writer: the admin API.
    public string Writer { get; set; } = string.Empty;

    // Member of catalog_reader: the read API and AI tools.
    public string Reader { get; set; } = string.Empty;

    // Member of catalog_outbox_relay: the relay and in-repo consumers.
    public string Relay { get; set; } = string.Empty;
}

public sealed class CatalogCacheOptions
{
    /// <summary>"Redis" or "Memory". Memory is for tests and single-instance development only.</summary>
    public string Provider { get; set; } = "Memory";

    public string? RedisConnectionString { get; set; }

    public int TtlSeconds { get; set; } = 300;

    public string KeyPrefix { get; set; } = "catalog";
}

public sealed class CatalogRelayOptions
{
    /// <summary>Run the outbox relay inside the API process (development only).</summary>
    public bool InProcess { get; set; }

    public int PollIntervalMs { get; set; } = 1000;

    public int BatchSize { get; set; } = 100;

    public string Topic { get; set; } = "catalog.events";

    /// <summary>Where ItemChanged and ItemRemoved go: one whole-item message per product per save.</summary>
    public string ItemsTopic { get; set; } = "catalog.items";

    public string BootstrapServers { get; set; } = "localhost:9092";

    public string ConsumerGroupId { get; set; } = "catalog-cache-invalidator";

    /// <summary>Where the AI team reports how far it got with each item message.</summary>
    public string IngestionReportTopic { get; set; } = "catalog.ingestion-report";

    public string IngestionReportGroupId { get; set; } = "catalog-ai-status";

    public int RetentionDays { get; set; } = 7;

    /// <summary>"Kafka" or "Null" (logs and drops; development only).</summary>
    public string Bus { get; set; } = "Kafka";
}

/// <summary>The S3 bucket items' photos go in. With no bucket, photos can't be added.</summary>
public sealed class CatalogPhotoOptions
{
    public string Bucket { get; set; } = string.Empty;

    public string Region { get; set; } = "us-east-1";

    /// <summary>An S3-compatible endpoint such as MinIO; empty for Amazon's own.</summary>
    public string ServiceUrl { get; set; } = string.Empty;

    /// <summary>Where browsers read the bucket from, such as https://cdn.example.com or http://localhost:9000/parrot.</summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    public string AccessKey { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;
}
