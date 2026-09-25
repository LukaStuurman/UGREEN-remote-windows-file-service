using Microsoft.VisualStudio.TestTools.UnitTesting;
using UGREENRemoteDrive;

namespace UGREENRemoteDrive.Tests;

[TestClass]
public sealed class TechbaseSharePathTests
{
    [TestMethod]
    public void AcceptsOnlyTheTechbaseUncShareRoot()
    {
        Assert.AreEqual(@"\\NASNAME\Techbase", TechbaseSharePath.ValidateUncRoot(@"\\NASNAME\Techbase"));
        Assert.AreEqual(@"\\nasname\techbase", TechbaseSharePath.ValidateUncRoot(@"\\nasname\techbase\"));
    }

    [TestMethod]
    public void RejectsAnyOtherShareOrSubpath()
    {
        foreach (var path in new[]
        {
            @"\\NASNAME\OtherShare",
            @"\\NASNAME\Techbase\Subfolder",
            @"C:\Techbase",
            "//NASNAME/Techbase"
        })
        {
            Assert.ThrowsExactly<ArgumentException>(() => TechbaseSharePath.ValidateUncRoot(path), path);
        }
    }
}
