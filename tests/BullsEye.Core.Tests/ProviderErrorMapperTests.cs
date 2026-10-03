namespace BullsEye.Core.Tests;

public class ProviderErrorMapperTests
{
    [Theory]
    [InlineData(401, typeof(AuthException))]
    [InlineData(402, typeof(CreditException))]
    [InlineData(429, typeof(RateLimitException))]
    [InlineData(500, typeof(ProviderUnavailableException))]
    [InlineData(502, typeof(ProviderUnavailableException))]
    [InlineData(503, typeof(ProviderUnavailableException))]
    [InlineData(400, typeof(ProviderException))]
    [InlineData(404, typeof(ProviderException))]
    public void Map_ChoosesExceptionByStatus(int status, Type expected)
    {
        var exception = ProviderErrorMapper.Map(status, "");

        Assert.IsType(expected, exception);
    }

    [Fact]
    public void Map_UsesOpenRouterErrorMessage()
    {
        var body = "{\"error\":{\"code\":402,\"message\":\"Insufficient credits\"}}";

        var exception = ProviderErrorMapper.Map(402, body);

        Assert.Equal("Insufficient credits", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>Bad Gateway</html>")]
    [InlineData("{\"error\":\"oops\"}")]
    [InlineData("{\"error\":{\"code\":502}}")]
    public void Map_FallsBackToStatus_WhenBodyHasNoMessage(string body)
    {
        var exception = ProviderErrorMapper.Map(502, body);

        Assert.Equal("HTTP 502", exception.Message);
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(529, true)]
    [InlineData(500, false)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(402, false)]
    public void Map_MarksOnlyTransientFailuresRetryable(int status, bool expected)
    {
        var exception = ProviderErrorMapper.Map(status, "");

        Assert.Equal(expected, exception.IsRetryable);
    }
}
