namespace BullsEye.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void CoreAssembly_IsReferenced()
    {
        Assert.Equal("BullsEye.Core", typeof(CoreAssembly).Assembly.GetName().Name);
    }
}
