using Unlose.Core.Updates;
using Xunit;

namespace Unlose.Tests;

/// <summary>UpdateChecker.IsNewer version comparison semantics.</summary>
public class UpdateCheckerTests
{
    [Theory]
    [InlineData("1.0.3.1", "1.0.4.0", true)]   // patch bump -> update available
    [InlineData("1.0.4.0", "1.0.4.0", false)]  // same version -> no update
    [InlineData("1.0.5.0", "1.0.4.0", false)]  // local is newer -> no prompt
    [InlineData("1.0.4", "1.0.4.0", false)]    // three-part form equals four-part padded with 0
    [InlineData("2.0.0.0", "1.9.9.9", false)]  // higher major -> no prompt
    [InlineData(null, "1.0.4.0", true)]        // local version unknown -> conservatively prompt for update
    [InlineData("not-a-version", "1.0.4.0", true)]
    public void IsNewer_ComparesFourParts(string? current, string latest, bool expected)
    {
        Assert.Equal(expected, UpdateChecker.IsNewer(current, Version.Parse(latest)));
    }
}
