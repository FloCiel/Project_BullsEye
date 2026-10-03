namespace BullsEye.Core.Tests;

public class PinPolicyTests
{
    [Theory]
    [InlineData("abc1234", "8자")]
    [InlineData("abcdefgh", "숫자")]
    [InlineData("12345678", "영문")]
    [InlineData("", "8자")]
    public void Validate_RejectsWeakPin_AndSaysWhatIsMissing(string pin, string hint)
    {
        var exception = Assert.Throws<WeakPinException>(() => PinPolicy.Validate(pin));

        Assert.Contains(hint, exception.Message);
    }

    [Theory]
    [InlineData("bullseye7")]
    [InlineData("MATLAB2026lab")]
    public void Validate_AcceptsLettersAndDigitsOfEightOrMore(string pin)
    {
        PinPolicy.Validate(pin);
    }
}
