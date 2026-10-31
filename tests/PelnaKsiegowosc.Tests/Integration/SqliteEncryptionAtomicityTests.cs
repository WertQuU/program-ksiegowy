using System.Security.Cryptography;
using PelnaKsiegowosc.Data;
using PelnaKsiegowosc.Tests.Infrastructure;

namespace PelnaKsiegowosc.Tests.Integration;

public sealed class SqliteEncryptionAtomicityTests
{
    [Fact]
    public async Task Interrupted_encryption_artifact_is_not_overwritten_and_original_remains_intact()
    {
        await using var books = await TestBooks.CreateAsync();
        var original = books.DbPath;
        var shaBefore = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(original)));
        var pending = original + ".szyfrowanie";
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        await File.WriteAllBytesAsync(pending, bytes);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SqliteEncryption.EncryptInPlace(original, SqliteEncryption.NewKey()));

        Assert.Contains("przerwanym szyfrowaniu", ex.Message);
        Assert.True(SqliteEncryption.IsPlaintext(original));
        Assert.Equal(shaBefore, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(original))));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(pending));
    }
}
