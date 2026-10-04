namespace Test.Parrot.Catalog.Fixtures;

/// <summary>
/// Runs only when CATALOG_TEST_CONNECTION points at a Postgres superuser connection,
/// e.g. Host=localhost;Port=15433;Database=postgres;Username=postgres;Password=test.
/// The fixture creates a throwaway database and real non-owner roles in that cluster.
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public const string EnvVar = "CATALOG_TEST_CONNECTION";

    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar)))
        {
            Skip = $"Set {EnvVar} to run Postgres-backed catalog tests.";
        }
    }
}
