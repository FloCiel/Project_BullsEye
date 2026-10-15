using System.Security.Cryptography;
using BullsEye.Core.Tests.Support;

namespace BullsEye.Core.Tests;

public class IdTokenValidatorTests : IDisposable
{
    private const string Client = "oaiapp_issued";
    private const string Nonce = "n-1";

    private readonly FakeOpenAiAuth _server = new();

    public void Dispose() => _server.Dispose();

    private IdTokenValidator Validator() => new(_server.Http, _server.Time);

    [Fact]
    public async Task Valid_ReturnsSubjectAndEmail()
    {
        var claims = await Validator().ValidateAsync(_server.IdToken(Client, Nonce), Client, Nonce, default);

        Assert.Equal(new IdTokenClaims("user-1", "me@example.com"), claims);
    }

    [Fact]
    public async Task AudienceArray_ContainingClient_IsAccepted()
    {
        var token = _server.IdToken(Client, Nonce, aud: new[] { "other", Client });

        var claims = await Validator().ValidateAsync(token, Client, Nonce, default);

        Assert.Equal("user-1", claims.Subject);
    }

    [Fact]
    public async Task SignedByAnotherKey_IsRejected()
    {
        using var attacker = RSA.Create(2048);

        await Assert.ThrowsAsync<ChatGptException>(() =>
            Validator().ValidateAsync(_server.IdToken(Client, Nonce, signWith: attacker), Client, Nonce, default));
    }

    [Fact]
    public async Task TamperedPayload_IsRejected()
    {
        var parts = _server.IdToken(Client, Nonce).Split('.');
        var other = _server.IdToken(Client, Nonce, subject: "admin").Split('.');

        await Assert.ThrowsAsync<ChatGptException>(() =>
            Validator().ValidateAsync($"{parts[0]}.{other[1]}.{parts[2]}", Client, Nonce, default));
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("nonce")]
    [InlineData("expired")]
    [InlineData("format")]
    public async Task WrongClaimOrShape_IsRejected(string broken)
    {
        var token = broken switch
        {
            "issuer" => _server.IdToken(Client, Nonce, issuer: "https://evil.example"),
            "audience" => _server.IdToken("oaiapp_other", Nonce),
            "nonce" => _server.IdToken(Client, "other-nonce"),
            "expired" => _server.IdToken(Client, Nonce, lifetime: TimeSpan.FromMinutes(-10)),
            _ => "not-a-jwt",
        };

        await Assert.ThrowsAsync<ChatGptException>(() => Validator().ValidateAsync(token, Client, Nonce, default));
    }

    [Fact]
    public async Task SlightlyExpired_WithinClockSkew_IsAccepted()
    {
        var token = _server.IdToken(Client, Nonce, lifetime: TimeSpan.FromMinutes(-2));

        var claims = await Validator().ValidateAsync(token, Client, Nonce, default);

        Assert.Equal("user-1", claims.Subject);
    }
}
