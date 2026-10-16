using System.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UGREENRemoteDrive;

namespace UGREENRemoteDrive.Tests;

[TestClass]
public sealed class SmbSignInTests
{
    private const string Root = @"\\fixture-nas\Techbase";

    [TestMethod]
    public void OnlySuccessfulSignInsCanReplaceSavedCredentials()
    {
        var store = new MemoryStore();
        var connector = new FakeConnector { Error = 1326 };
        var service = new SmbSignInService(store, connector);
        using var secret = Secret();
        Assert.IsFalse(service.SignIn(Root, "fixture-user", secret, true).Success);
        Assert.AreEqual(0, store.Saves);
        Assert.AreEqual(0, store.Deletes);
        connector.Error = 0;
        Assert.IsTrue(service.SignIn(Root, "fixture-user", secret, true).Success);
        Assert.AreEqual(1, store.Saves);
        Assert.IsTrue(secret.Length > 0, "The caller keeps ownership until its native call has finished.");
    }

    [TestMethod]
    public void NotRememberingRemovesOnlyTheAppsEntryAfterSuccessfulSignIn()
    {
        var store = new MemoryStore();
        var service = new SmbSignInService(store, new FakeConnector());
        using var secret = Secret();
        Assert.IsTrue(service.SignIn(Root, "fixture-user", secret, false).Success);
        Assert.AreEqual(0, store.Saves);
        Assert.AreEqual(1, store.Deletes);
        Assert.AreEqual(Root, store.LastRoot);
        Assert.AreEqual(WindowsSmbCredentialStore.Target(Root), WindowsSmbCredentialStore.Target(@"\\FIXTURE-NAS\techbase\"));
        Assert.AreNotEqual(WindowsSmbCredentialStore.Target(Root), WindowsSmbCredentialStore.Target(@"\\other-fixture\Techbase"));
        StringAssert.StartsWith(WindowsSmbCredentialStore.Target(Root), "UGREENRemoteDrive:SMB:");
    }

    [TestMethod]
    public void InvalidShareAndInvalidUserCannotReachNativeConnectorOrCredentialStore()
    {
        var store = new MemoryStore();
        var connector = new FakeConnector();
        var service = new SmbSignInService(store, connector);
        using var secret = Secret();
        foreach (var root in new[] { @"\\fixture-nas\Other", @"\\fixture-nas\Techbase\child", "\\\\fixture\n-nas\\Techbase", @"\\fixture@SSL\Techbase" })
        {
            Assert.ThrowsExactly<ArgumentException>(() => service.SignIn(root, "fixture-user", secret, true));
            Assert.ThrowsExactly<ArgumentException>(() => service.Restore(root));
            Assert.ThrowsExactly<ArgumentException>(() => service.Forget(root));
        }
        Assert.ThrowsExactly<ArgumentException>(() => service.SignIn(Root, "bad\0user", secret, true));
        Assert.AreEqual(0, connector.Calls);
        Assert.AreEqual(0, store.Loads + store.Saves + store.Deletes);
    }

    [TestMethod]
    public void RestoreUsesOnlyExactAppEntryAndDoesNotChangeItOnFailure()
    {
        var store = new MemoryStore();
        var connector = new FakeConnector { Error = 1219 };
        var service = new SmbSignInService(store, connector);
        Assert.IsNull(service.Restore(Root));
        Assert.AreEqual(0, connector.Calls);
        store.HasCredential = true;
        var result = service.Restore(Root);
        Assert.AreEqual(1219, result!.Error);
        Assert.AreEqual(1, connector.Calls);
        Assert.AreEqual(0, store.Saves + store.Deletes);
        Assert.AreEqual(Root, store.LastRoot);
        Assert.AreEqual("fixture-user", connector.LastUser);
    }

    [TestMethod]
    public void SaveFailureIsReportedWithoutUndoingAWorkingConnection()
    {
        var service = new SmbSignInService(new MemoryStore { FailSave = true }, new FakeConnector());
        using var secret = Secret();
        var result = service.SignIn(Root, "fixture-user", secret, true);
        Assert.IsTrue(result.Success);
        Assert.IsNotNull(result.Warning);
        Assert.IsFalse(result.Warning.Contains("fixture-user"));
        Assert.IsFalse(result.Warning.Contains("fixture-secret"));
    }

    [TestMethod]
    public void AccountConflictAndAuthenticationErrorsAreDistinctAndDoNotExposeSecrets()
    {
        StringAssert.Contains(new SmbSignInResult(1219).Message, "ander account");
        StringAssert.Contains(new SmbSignInResult(1909).Message, "geblokkeerd");
        StringAssert.Contains(new SmbSignInResult(1326).Message, "klopt niet");
        StringAssert.Contains(new SmbSignInResult(5).Message, "geen toegang");
        StringAssert.Contains(new SmbSignInResult(53).Message, "Windows-code 53");
    }

    [TestMethod]
    public void WindowsCredentialRoundTripUsesOnlyDisposableRunnerFixture()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            Assert.Inconclusive("Native credential writes are limited to a disposable GitHub runner.");
        var root = @"\\auth-fixture-" + Guid.NewGuid().ToString("N") + @"\Techbase";
        var store = new WindowsSmbCredentialStore();
        using var secret = Secret();
        try
        {
            Assert.IsNull(store.Load(root));
            store.Save(root, "fixture-user", secret);
            using var saved = store.Load(root);
            Assert.IsNotNull(saved);
            Assert.AreEqual("fixture-user", saved.UserName);
            Assert.AreEqual(secret.Length, saved.Password.Length);
            // Compare without converting either password to a managed string.
            var connector = new ComparingConnector(secret);
            Assert.IsTrue(new SmbSignInService(store, connector).Restore(root)!.Success);
            Assert.IsTrue(connector.Matched);
        }
        finally { store.Delete(root); }
        Assert.IsNull(store.Load(root));
    }

    private static SecureString Secret()
    {
        var secret = new SecureString();
        foreach (var value in "fixture-secret-\u20ac-\uD83D\uDD11") secret.AppendChar(value);
        secret.MakeReadOnly();
        return secret;
    }

    private sealed class MemoryStore : ISmbCredentialStore
    {
        public int Loads, Saves, Deletes;
        public bool HasCredential, FailSave;
        public string? LastRoot;
        public SmbCredential? Load(string root) { Loads++; LastRoot = root; return HasCredential ? new("fixture-user", Secret()) : null; }
        public void Save(string root, string userName, SecureString password)
        {
            if (FailSave) throw new InvalidOperationException("fixture-secret");
            Saves++; LastRoot = root;
        }
        public void Delete(string root) { Deletes++; LastRoot = root; }
    }

    private sealed class FakeConnector : ISmbConnector
    {
        public int Error, Calls;
        public string? LastUser;
        public int Connect(string root, string userName, SecureString password) { Calls++; LastUser = userName; return Error; }
    }

    private sealed class ComparingConnector(SecureString expected) : ISmbConnector
    {
        public bool Matched;
        public int Connect(string root, string userName, SecureString password)
        {
            var left = System.Runtime.InteropServices.Marshal.SecureStringToGlobalAllocUnicode(expected);
            var right = System.Runtime.InteropServices.Marshal.SecureStringToGlobalAllocUnicode(password);
            try
            {
                Matched = expected.Length == password.Length;
                for (var index = 0; index < expected.Length && Matched; index++)
                    Matched &= System.Runtime.InteropServices.Marshal.ReadInt16(left, index * 2) == System.Runtime.InteropServices.Marshal.ReadInt16(right, index * 2);
                return Matched ? 0 : 1326;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.ZeroFreeGlobalAllocUnicode(left);
                System.Runtime.InteropServices.Marshal.ZeroFreeGlobalAllocUnicode(right);
            }
        }
    }
}
