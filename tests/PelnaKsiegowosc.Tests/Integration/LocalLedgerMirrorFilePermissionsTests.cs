using Microsoft.Data.Sqlite;
using PelnaKsiegowosc.Infrastructure.Integrity;

namespace PelnaKsiegowosc.Tests.Integration;

public sealed class LocalLedgerMirrorFilePermissionsTests
{
    [Fact]
    public void Unix_mirror_directory_and_file_are_private_and_existing_permissions_are_tightened()
    {
        if (OperatingSystem.IsWindows()) return;

        var root = Path.Combine(Path.GetTempPath(), "pk-mirror-permissions-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "KontrolaIntegralnosci");
        var path = Path.Combine(directory, "test.sqlite");
        const UnixFileMode privateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        const UnixFileMode privateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            LocalLedgerMirror.PreparePrivateMirrorFile(path);
            Assert.True(File.Exists(path));
            Assert.Equal(privateDirectory, File.GetUnixFileMode(directory));
            Assert.Equal(privateFile, File.GetUnixFileMode(path));

            File.SetUnixFileMode(directory, privateDirectory | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead);
            File.SetUnixFileMode(path, privateFile | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            LocalLedgerMirror.PreparePrivateMirrorFile(path);
            Assert.Equal(privateDirectory, File.GetUnixFileMode(directory));
            Assert.Equal(privateFile, File.GetUnixFileMode(path));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Precreated_private_mirror_file_can_be_initialized_by_sqlite()
    {
        var root = Path.Combine(Path.GetTempPath(), "pk-mirror-open-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "KontrolaIntegralnosci", "mirror.sqlite");
            LocalLedgerMirror.PreparePrivateMirrorFile(path);
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE test_integrity (id INTEGER PRIMARY KEY)";
            await command.ExecuteNonQueryAsync();
            Assert.True(File.Exists(path));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Unix_mirror_rejects_existing_symbolic_link_instead_of_following_it()
    {
        if (OperatingSystem.IsWindows()) return;

        var root = Path.Combine(Path.GetTempPath(), "pk-mirror-link-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var target = Path.Combine(root, "target.txt");
            File.WriteAllText(target, "nie zmieniaj");
            var mirror = Path.Combine(root, "KontrolaIntegralnosci", "mirror.sqlite");
            Directory.CreateDirectory(Path.GetDirectoryName(mirror)!);
            File.CreateSymbolicLink(mirror, target);

            Assert.Throws<InvalidOperationException>(() => LocalLedgerMirror.PreparePrivateMirrorFile(mirror));
            Assert.Equal("nie zmieniaj", File.ReadAllText(target));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
