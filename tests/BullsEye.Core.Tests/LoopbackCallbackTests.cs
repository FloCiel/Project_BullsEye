namespace BullsEye.Core.Tests;

public class LoopbackCallbackTests
{
    [Fact]
    public void ParseTarget_DecodesCallbackQuery()
    {
        var query = LoopbackCallback.ParseTarget("/auth/callback?code=a%2Fb&scope=openid+email&state=s&client_id=oaiapp_1");

        Assert.Equal("a/b", query!["code"]);
        Assert.Equal("openid email", query["scope"]);
        Assert.Equal("oaiapp_1", query["client_id"]);
    }

    [Theory]
    [InlineData("/favicon.ico")]
    [InlineData("/auth/callbackx?code=1")]
    [InlineData("/")]
    public void ParseTarget_OtherPath_IsNull(string target)
    {
        Assert.Null(LoopbackCallback.ParseTarget(target));
    }

    [Fact]
    public async Task Wait_ReturnsQueryFromBrowserRedirect_AfterIgnoringOtherRequests()
    {
        using var callback = LoopbackCallback.Start(preferredPort: 0);
        Assert.StartsWith("http://127.0.0.1:", callback.RedirectUri);
        Assert.EndsWith("/auth/callback", callback.RedirectUri);

        var waiting = callback.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        using var browser = new HttpClient();
        var favicon = await browser.GetAsync($"http://127.0.0.1:{callback.Port}/favicon.ico");
        var page = await browser.GetAsync(callback.RedirectUri + "?code=c1&state=s1");

        var query = await waiting;
        Assert.Equal(404, (int)favicon.StatusCode);
        Assert.Equal(200, (int)page.StatusCode);
        Assert.Contains("BullsEye", await page.Content.ReadAsStringAsync());
        Assert.Equal("c1", query["code"]);
        Assert.Equal("s1", query["state"]);
    }

    [Fact]
    public void Start_PreferredPortBusy_FallsBackToFreePort()
    {
        using var first = LoopbackCallback.Start(preferredPort: 0);

        using var second = LoopbackCallback.Start(preferredPort: first.Port);

        Assert.NotEqual(first.Port, second.Port);
    }
}
