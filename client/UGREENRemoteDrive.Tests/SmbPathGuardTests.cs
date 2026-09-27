using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UGREENRemoteDrive;

namespace UGREENRemoteDrive.Tests;

[TestClass]
public sealed class SmbPathGuardTests
{
    [TestMethod]
    public void AllowsMissingTargetWhenExistingParentsAreOrdinaryDirectories()
    {
        var temporaryRoot = Directory.CreateTempSubdirectory();
        try
        {
            var parent = Directory.CreateDirectory(Path.Combine(temporaryRoot.FullName, "parent"));
            var missingTarget = Path.Combine(parent.FullName, "not-created-yet");

            SmbPathGuard.EnsureNoReparsePoints(temporaryRoot.FullName, missingTarget);
        }
        finally
        {
            temporaryRoot.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void RejectsReparsePointRootAndParentOfMissingTarget()
    {
        var temporaryRoot = Directory.CreateTempSubdirectory();
        var shareRoot = Directory.CreateDirectory(Path.Combine(temporaryRoot.FullName, "share"));
        var externalTarget = Directory.CreateDirectory(Path.Combine(temporaryRoot.FullName, "outside"));
        var reparseDirectory = Path.Combine(shareRoot.FullName, "linked-directory");

        try
        {
            try
            {
                Directory.CreateSymbolicLink(reparseDirectory, externalTarget.FullName);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                if (!TryCreateJunction(reparseDirectory, externalTarget.FullName))
                    Assert.Inconclusive("The test environment cannot create a directory reparse point.");
            }

            Assert.ThrowsExactly<UnauthorizedAccessException>(
                () => SmbPathGuard.EnsureNoReparsePoints(shareRoot.FullName, Path.Combine(reparseDirectory, "not-created-yet")));
            Assert.ThrowsExactly<UnauthorizedAccessException>(
                () => SmbPathGuard.EnsureNoReparsePoints(reparseDirectory, reparseDirectory));
        }
        finally
        {
            if (Directory.Exists(reparseDirectory)) Directory.Delete(reparseDirectory);
            temporaryRoot.Delete(recursive: true);
        }
    }

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        try
        {
            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/d /c mklink /J \"{linkPath}\" \"{targetPath}\"",
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            using var process = Process.Start(startInfo);
            if (process is null) return false;
            _ = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 && Directory.Exists(linkPath);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
