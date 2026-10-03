namespace BullsEye.Core.Tests;

public class ErrorMessagesTests
{
    public static TheoryData<Exception, ErrorAction, string> Cases => new()
    {
        { new AuthException("Invalid key"), ErrorAction.OpenSettings, "키" },
        { new CreditException("Insufficient credits"), ErrorAction.None, "크레딧" },
        { new RateLimitException("slow down"), ErrorAction.Retry, "재시도" },
        { new ProviderUnavailableException("HTTP 502", isRetryable: true), ErrorAction.Retry, "재시도" },
        { new ProviderException("No endpoints found for vendor/x"), ErrorAction.OpenSettings, "No endpoints found for vendor/x" },
        { new NetworkException(new HttpRequestException()), ErrorAction.Retry, "네트워크" },
        { new ResponseTimeoutException(), ErrorAction.Retry, "응답" },
        { new InvalidOperationException("boom"), ErrorAction.Retry, "예상치 못한" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void For_MapsExceptionToMessageAndAction(Exception exception, ErrorAction action, string mustContain)
    {
        var error = ErrorMessages.For(exception);

        Assert.Equal(action, error.Action);
        Assert.Contains(mustContain, error.Message);
    }

    [Fact]
    public void ForStreamFailure_KeepsProviderMessage_AndOffersRetry()
    {
        var error = ErrorMessages.ForStreamFailure("Provider disconnected");

        Assert.Equal(ErrorAction.Retry, error.Action);
        Assert.Contains("Provider disconnected", error.Message);
    }
}
