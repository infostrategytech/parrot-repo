namespace Parrot.Catalog.Application.Audit;

public static class AuditActions
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Delete = "delete";
    public const string Publish = "publish";
    public const string Retire = "retire";
    public const string VersionCreated = "version_created";
    public const string TypeVersionMigrated = "type_version_migrated";
    public const string Purge = "purge";
}
