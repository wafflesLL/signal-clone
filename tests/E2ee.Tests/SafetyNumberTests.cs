namespace E2ee.Tests;

public class SafetyNumberTests
{
    private static readonly byte[] AliceSig = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] AliceDh = Enumerable.Range(32, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] BobSig = Enumerable.Repeat((byte)0xAA, 32).ToArray();
    private static readonly byte[] BobDh = Enumerable.Repeat((byte)0x55, 32).ToArray();

    [Fact]
    public void MatchesIndependentPythonImplementation()
    {
        // From scripts/safety_number_crosscheck.py, a separate implementation of the same scheme.
        Assert.Equal("04837 39223 25490 02308 00885 71464 47386 08710 89710 28860 46343 22681",
            SafetyNumber.Compute("alice", AliceSig, AliceDh, "bob", BobSig, BobDh));
    }

    [Fact]
    public void BothSidesSeeTheSameNumber() =>
        Assert.Equal(
            SafetyNumber.Compute("alice", AliceSig, AliceDh, "bob", BobSig, BobDh),
            SafetyNumber.Compute("bob", BobSig, BobDh, "alice", AliceSig, AliceDh));

    [Fact]
    public void IsTwelveGroupsOfFiveDigits()
    {
        var n = SafetyNumber.Compute("alice", AliceSig, AliceDh, "bob", BobSig, BobDh);
        var groups = n.Split(' ');
        Assert.Equal(12, groups.Length);
        Assert.All(groups, g => Assert.Matches("^[0-9]{5}$", g));
    }

    [Fact]
    public void ChangesWhenAnyKeyOrNameChanges()
    {
        var baseline = SafetyNumber.Compute("alice", AliceSig, AliceDh, "bob", BobSig, BobDh);
        var otherDh = (byte[])BobDh.Clone();
        otherDh[0] ^= 1;
        Assert.NotEqual(baseline, SafetyNumber.Compute("alice", AliceSig, AliceDh, "bob", BobSig, otherDh));
        Assert.NotEqual(baseline, SafetyNumber.Compute("alice", AliceSig, AliceDh, "bob", AliceDh, BobDh));
        Assert.NotEqual(baseline, SafetyNumber.Compute("alicf", AliceSig, AliceDh, "bob", BobSig, BobDh));
    }
}
