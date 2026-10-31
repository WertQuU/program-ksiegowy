using System.Security.Cryptography;
using PelnaKsiegowosc.Application.Documents;

namespace PelnaKsiegowosc.Tests.Integration;

public sealed class SharedAttachmentWriteTests
{
    private sealed class PausedStream(byte[] data) : MemoryStream(data)
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            Started.TrySetResult();
            await Resume.Task.WaitAsync(ct);
            return await base.ReadAsync(buffer, ct);
        }
    }

    [Fact]
    public async Task Concurrent_workstations_cannot_overwrite_a_document_saved_by_another_station()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var key = RandomNumberGenerator.GetBytes(32);
        var first = new LocalFileStore(root, key);
        var second = new LocalFileStore(root, key);
        using var paused = new PausedStream("first document"u8.ToArray());
        var write = first.SaveAtAsync("2026/10/shared.bin", paused);
        try
        {
            await paused.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // While workstation 1 still holds its staging file, workstation 2 completes.
            await second.SaveAtAsync("2026/10/shared.bin", new MemoryStream("second document"u8.ToArray()));
            paused.Resume.TrySetResult();
            // Stanowisko 1 kończy później, ale nie może nadpisać już zapisanego dowodu.
            await Assert.ThrowsAnyAsync<IOException>(() => write);
            await using var read = await second.OpenAsync("2026/10/shared.bin");
            using var output = new MemoryStream();
            await read.CopyToAsync(output);
            Assert.Equal("second document"u8.ToArray(), output.ToArray());
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            paused.Resume.TrySetResult();
            try { await write; } catch (IOException) { /* drugi zapis przegrał wyścig */ } finally { Directory.Delete(root, true); }
        }
    }
}
