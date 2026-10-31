using CommunityToolkit.Mvvm.ComponentModel;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Domain.Common;

namespace PelnaKsiegowosc.Presentation.ViewModels;

/// <summary>Formularz aktywnej centralnej bazy PostgreSQL. SQLite obsługiwany wyłącznie przez narzędzia importu.</summary>
public sealed partial class DatabaseForm : ObservableObject
{
    [ObservableProperty] private bool _usePostgres;
    [ObservableProperty] private string _sqlitePath = "";
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private string _port = "5432";
    [ObservableProperty] private string _database = "";
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private Choice<PgSslMode> _sslMode = Choices.Of<PgSslMode>().First(c => c.Value == PgSslMode.VerifyFull);
    [ObservableProperty] private string _rootCertificate = "";
    [ObservableProperty] private Choice<ServerRole> _role = Choices.Of<ServerRole>()[0];
    public List<Choice<PgSslMode>> SslModes { get; } = Choices.Of<PgSslMode>();
    public List<Choice<ServerRole>> Roles { get; } = Choices.Of<ServerRole>();

    public bool UseSqlite
    {
        get => !UsePostgres;
        set => UsePostgres = !value;
    }

    partial void OnUsePostgresChanged(bool value) => OnPropertyChanged(nameof(UseSqlite));

    public void Load(DatabaseProfile p)
    {
        UsePostgres = p.Engine == DatabaseEngine.PostgreSql;
        SqlitePath = p.SqlitePath ?? "";
        Host = p.Host ?? ""; Port = p.Port.ToString(); Database = p.Database ?? ""; Username = p.Username ?? "";
        SslMode = SslModes.First(s => s.Value == p.SslMode);
        RootCertificate = p.RootCertificatePath ?? "";
        Role = Roles.First(r => r.Value == p.Role);
        Password = "";
    }

    public DatabaseProfile ToProfile()
    {
        if (!UsePostgres)
            throw new DomainException("Jedyną aktywną bazą ksiąg jest centralny PostgreSQL.");
        if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Database) || string.IsNullOrWhiteSpace(Username))
            throw new DomainException("Podaj adres serwera, nazwę bazy i użytkownika PostgreSQL.");
        return new DatabaseProfile
        {
            Name = $"Serwer PostgreSQL {Host.Trim()}", Engine = DatabaseEngine.PostgreSql, Host = Host.Trim(), Port = Parse.Int(Port, "Port"),
            Database = Database.Trim(), Username = Username.Trim(), SslMode = SslMode.Value, RootCertificatePath = Parse.Trim(RootCertificate), Role = Role.Value
        };
    }
}
