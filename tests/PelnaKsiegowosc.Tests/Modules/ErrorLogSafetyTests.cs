using PelnaKsiegowosc.Presentation.Services;

namespace PelnaKsiegowosc.Tests.Modules;

public sealed class ErrorLogSafetyTests
{
    [Fact]
    public void Error_log_never_persists_exception_message_or_credentials()
    {
        var root = Path.Combine(Path.GetTempPath(), "pk-errorlog-" + Guid.NewGuid().ToString("N"));
        try
        {
            ErrorLog.Directory = root;
            ErrorLog.Write(new InvalidOperationException("PESEL=12345678901 Password=superSecret KSeF-Token=secret"));
            var file = Directory.GetFiles(root, "bledy-*.log").Single();
            var recorded = File.ReadAllText(file);
            Assert.Contains(nameof(InvalidOperationException), recorded);
            Assert.DoesNotContain("12345678901", recorded);
            Assert.DoesNotContain("superSecret", recorded);
            Assert.DoesNotContain("KSeF-Token", recorded);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(root));
            }
        }
        finally
        {
            ErrorLog.Directory = null;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
