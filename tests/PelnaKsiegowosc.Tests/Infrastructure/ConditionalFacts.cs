namespace PelnaKsiegowosc.Tests.Infrastructure;

/// <summary>Test integracyjny z PostgreSQL — wymaga zmiennej PK_TEST_PG (łańcuch połączenia do serwera z prawem CREATEDB).</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PK_TEST_PG")))
            Skip = "Brak PK_TEST_PG — test integracyjny PostgreSQL pominięty (np. PK_TEST_PG=\"Host=localhost;Username=psa;Password=...\").";
    }
}

/// <summary>Test integracyjny z serwerem zgodnym z S3 — wymaga PK_TEST_S3_ENDPOINT (np. lokalny moto_server lub MinIO).</summary>
public sealed class S3FactAttribute : FactAttribute
{
    public S3FactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PK_TEST_S3_ENDPOINT")))
            Skip = "Brak PK_TEST_S3_ENDPOINT — test integracyjny Object Storage pominięty.";
    }
}
