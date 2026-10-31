using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;

namespace PelnaKsiegowosc.Infrastructure.Configuration;

/// <summary>
/// Jeden uruchomiony klient programu na indywidualny login PostgreSQL.
/// Osobne połączenie bez poolingu utrzymuje sesyjną blokadę doradczą PostgreSQL;
/// serwer automatycznie zwalnia ją po zerwaniu połączenia lub zamknięciu procesu.
/// To NIE ogranicza liczby technicznych połączeń Npgsql wewnątrz tej samej instancji.
/// </summary>
public sealed class ExclusivePostgresSession : IDisposable, IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _watchdog;
    private int _healthy = 1;
    private int _disposed;

    private ExclusivePostgresSession(NpgsqlConnection connection, string identity)
    {
        _connection = connection;
        Identity = identity;
        _watchdog = WatchAsync();
    }

    public string Identity { get; }
    public bool IsHealthy => Volatile.Read(ref _healthy) == 1 &&
        Volatile.Read(ref _disposed) == 0 && _connection.State == ConnectionState.Open;

    /// <summary>Blokada identyfikuje konto w konkretnej bazie; wynik stabilny między komputerami.</summary>
    public static long LockKey(string databaseLogin)
    {
        if (string.IsNullOrWhiteSpace(databaseLogin)) throw new ArgumentException("Brak loginu bazy.", nameof(databaseLogin));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("PelnaKsiegowosc/one-active-device/v1/" + databaseLogin));
        return BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(0, 8));
    }

    public static string ProfileIdentity(DatabaseProfile p) =>
        $"{p.Host?.Trim().ToLowerInvariant()}:{p.Port}/{p.Database}/{p.Username}";

    public static async Task<ExclusivePostgresSession> AcquireAsync(
        DatabaseProfile profile, string connectionString, CancellationToken ct = default)
    {
        if (profile.Engine != DatabaseEngine.PostgreSql)
            throw new DomainException("Aktywna baza programu musi być centralnym PostgreSQL.");

        // PooledConnection = false: po zwrocie do puli lock nie może pozostać przypadkowo przypięty do fizycznej sesji.
        var cs = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, KeepAlive = 10 };
        var conn = new NpgsqlConnection(cs.ConnectionString);
        try
        {
            await conn.OpenAsync(ct);
            await using (var who = new NpgsqlCommand("SELECT session_user", conn))
            {
                var actualLogin = (string?)await who.ExecuteScalarAsync(ct);
                if (string.IsNullOrWhiteSpace(actualLogin) || !string.Equals(actualLogin, profile.Username, StringComparison.Ordinal))
                    throw new DomainException("Serwer PostgreSQL uwierzytelnił inne konto niż skonfigurowany login. Sprawdź profil połączenia.");
            }
            await using (var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@session_key)", conn))
            {
                command.Parameters.AddWithValue("session_key", LockKey(profile.Username!));
                if (await command.ExecuteScalarAsync(ct) is not true)
                    throw new DomainException($"Login PostgreSQL „{profile.Username}” jest już aktywny na innym urządzeniu. Zakończ tamtą sesję i ponów próbę. Po awarii połączenia blokada zwolni się na serwerze po wykryciu zerwania.");
            }
            return new ExclusivePostgresSession(conn, ProfileIdentity(profile));
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    private async Task WatchAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                // Dedykowane połączenie ma być stale żywe; utraty blokady NIGDY nie odzyskujemy automatycznie.
                await using var command = new NpgsqlCommand("SELECT 1", _connection);
                if (await command.ExecuteScalarAsync(_stop.Token) is not 1)
                    throw new IOException("Utracono sesję blokującą.");
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch
        {
            Interlocked.Exchange(ref _healthy, 0); // Fail closed; wymagany restart klienta.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Exchange(ref _healthy, 0);
        _stop.Cancel();
        _connection.Dispose(); // zamknięcie dedykowanego połączenia zwalnia advisory lock na serwerze
        _stop.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Exchange(ref _healthy, 0);
        await _stop.CancelAsync();
        try { await _watchdog; } catch { /* watchdog sam sygnalizuje utratę sesji */ }
        await _connection.DisposeAsync();
        _stop.Dispose();
    }
}
