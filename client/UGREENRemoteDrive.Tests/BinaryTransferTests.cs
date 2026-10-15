using Microsoft.VisualStudio.TestTools.UnitTesting;
using UGREENRemoteDrive;

namespace UGREENRemoteDrive.Tests;

[TestClass]
public sealed class BinaryTransferTests
{
    [TestMethod]
    public void BinaryResponsePreservesEveryByteWithoutTextEncoding()
    {
        var fixture = Enumerable.Range(0, BinaryTransfer.MaximumLength).Select(i => (byte)(i % 251)).ToArray();
        using var stream = new MemoryStream(fixture);
        CollectionAssert.AreEqual(fixture, BinaryTransfer.Read(stream, fixture.Length, fixture.Length));
    }

    [TestMethod]
    public void InvalidLengthsAndTruncatedBuffersFail()
    {
        using var stream = new MemoryStream(new byte[4]);
        Assert.ThrowsExactly<IOException>(() => BinaryTransfer.Read(stream, -1, 4));
        Assert.ThrowsExactly<IOException>(() => BinaryTransfer.Read(stream, 5, 4));
        Assert.ThrowsExactly<EndOfStreamException>(() => BinaryTransfer.Read(stream, 5, 8));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BinaryTransfer.Capacity("4194305", null, true));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BinaryTransfer.Capacity("bad", null, true));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BinaryTransfer.Capacity(null, -1, false));
        Assert.ThrowsExactly<ArgumentException>(() => BinaryTransfer.Capacity("10", 10, true));
        Assert.AreEqual(1, BinaryTransfer.Capacity("0", null, true));
        Assert.AreEqual(4096, BinaryTransfer.Capacity(null, 4096, false));
    }
}
