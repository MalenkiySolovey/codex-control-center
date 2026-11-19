using CodexSwitcher.App;

namespace CodexSwitcher.Core.Tests;

public sealed class ProgramSingleInstanceKeyTests
{
    [Fact]
    public void ResolveSingleInstanceKey_UsesExistingKeyForDefaultHome()
    {
        const string defaultHome = @"C:\Users\tester\AppData\Local\CodexSwitchboard";

        var key = Program.ResolveSingleInstanceKey(defaultHome, defaultHome);

        Assert.Equal(Program.SingleInstanceKey, key);
    }

    [Fact]
    public void ResolveSingleInstanceKey_CustomHomesReceiveDistinctStableKeys()
    {
        const string defaultHome = @"C:\Users\tester\AppData\Local\CodexSwitchboard";
        const string qaHome = @"C:\Users\tester\AppData\Local\Temp\ccc-qa";

        var first = Program.ResolveSingleInstanceKey(qaHome, defaultHome);
        var repeated = Program.ResolveSingleInstanceKey(qaHome + "\\", defaultHome);
        var another = Program.ResolveSingleInstanceKey(qaHome + "-other", defaultHome);

        Assert.NotEqual(Program.SingleInstanceKey, first);
        Assert.Equal(first, repeated);
        Assert.NotEqual(first, another);
    }
}
