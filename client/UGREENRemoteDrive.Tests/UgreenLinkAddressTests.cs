using Microsoft.VisualStudio.TestTools.UnitTesting;
using UGREENRemoteDrive;

namespace UGREENRemoteDrive.Tests;

[TestClass]
public sealed class UgreenLinkAddressTests
{
    [TestMethod]
    public void AcceptsHttpsUgreenShortcutHosts()
    {
        Assert.AreEqual("nas.ugapp.link", UgreenLinkAddress.Validate("https://nas.ugapp.link/").Host);
        Assert.AreEqual("nas.ugdocker.link", UgreenLinkAddress.Validate("https://nas.ugdocker.link/").Host);
    }

    [TestMethod]
    public void RejectsNonHttpsAndLookalikeHosts()
    {
        foreach (var value in new[]
        {
            "http://nas.ugapp.link/",
            "https://ugapp.link/",
            "https://nas.ugapp.link.example.com/",
            "https://nas.ugdocker.link.example.com/",
            "https://example.com/"
        })
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => UgreenLinkAddress.Validate(value), value);
        }
    }
}
