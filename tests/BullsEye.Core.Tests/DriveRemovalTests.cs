namespace BullsEye.Core.Tests;

public class DriveRemovalTests
{
    private const uint E = 1u << 4;
    private const uint F = 1u << 5;

    [Theory]
    [InlineData(E, @"E:\BullsEye\BullsEye.exe", true)]
    [InlineData(E, @"e:\tools\BullsEye.exe", true)]
    [InlineData(E | F, @"F:\BullsEye.exe", true)]
    [InlineData(F, @"E:\BullsEye\BullsEye.exe", false)]
    [InlineData(E, @"C:\Users\me\BullsEye.exe", false)]
    [InlineData(E, @"\\?\E:\BullsEye\BullsEye.exe", true)]
    [InlineData(uint.MaxValue, @"\\server\share\BullsEye.exe", false)]
    [InlineData(uint.MaxValue, @"BullsEye.exe", false)]
    [InlineData(0u, @"E:\BullsEye.exe", false)]
    public void Affects_ChecksExeDriveAgainstRemovedUnitMask(uint unitMask, string exePath, bool expected)
    {
        Assert.Equal(expected, DriveRemoval.Affects(unitMask, exePath));
    }
}
