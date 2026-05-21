using Foundry.Core.Update;

namespace Foundry.Tests;

public class UpdaterTests
{
    [Theory]
    [InlineData("v0.5.0", "0.4.1", true)]   // newer
    [InlineData("0.4.2", "0.4.1", true)]    // newer, no 'v'
    [InlineData("v0.4.1", "0.4.1", false)]  // equal
    [InlineData("v0.4.0", "0.4.1", false)]  // older
    [InlineData("v1.0.0-beta", "0.4.1", true)] // pre-release metadata stripped, still newer
    public void IsNewer_ComparesSemverTolerantOfPrefixAndMetadata(string tag, string current, bool expected)
    {
        Assert.Equal(expected, GitHubUpdater.IsNewer(tag, current));
    }
}
