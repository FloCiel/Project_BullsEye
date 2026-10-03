namespace BullsEye.Core.Tests;

public class ApiKeyMaskTests
{
    [Theory]
    [InlineData("sk-or-v1-0123456789abcdef0123456789abcdefa1b2", "sk-or-…a1b2")]
    [InlineData("abcdefghijkl", "abcdef…ijkl")]
    [InlineData("short-key", "••••")]
    [InlineData("", "")]
    public void Mask_ShowsOnlyPrefixAndLastFour(string key, string expected)
    {
        Assert.Equal(expected, ApiKeyMask.Mask(key));
    }
}
