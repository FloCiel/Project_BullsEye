using System.Text;

namespace BullsEye.Core.Tests;

public class SecretBoxTests
{
    private static readonly KdfParameters Fast = new(MemoryKiB: 1024, Iterations: 1, Parallelism: 1);
    private static readonly byte[] Plain = Encoding.UTF8.GetBytes("{\"apiKey\":\"sk-or-v1-secret\"}");

    [Fact]
    public void Open_WithSamePin_ReturnsOriginal()
    {
        var sealedBytes = SecretBox.Seal(Plain, "bullseye7", Fast);

        var opened = SecretBox.Open(sealedBytes, "bullseye7");

        Assert.Equal(Plain, opened);
    }

    [Fact]
    public void Seal_SameInput_ProducesDifferentBytes()
    {
        var first = SecretBox.Seal(Plain, "bullseye7", Fast);
        var second = SecretBox.Seal(Plain, "bullseye7", Fast);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Open_WithDefaultParameters_ReturnsOriginal()
    {
        var sealedBytes = SecretBox.Seal(Plain, "bullseye7", KdfParameters.Default);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var opened = SecretBox.Open(sealedBytes, "bullseye7");
        Console.WriteLine($"[unlock] default KDF: {stopwatch.ElapsedMilliseconds} ms");

        Assert.Equal(Plain, opened);
    }

    [Fact]
    public void Open_WithWrongPin_ThrowsWrongPin()
    {
        var sealedBytes = SecretBox.Seal(Plain, "bullseye7", Fast);

        Assert.Throws<WrongPinException>(() => SecretBox.Open(sealedBytes, "bullseye8"));
    }

    [Theory]
    [InlineData(-1)] // 암호문 마지막 바이트
    [InlineData(17)] // salt 첫 바이트 (AAD)
    public void Open_TamperedBytes_ThrowsWrongPin(int index)
    {
        var sealedBytes = SecretBox.Seal(Plain, "bullseye7", Fast);
        var i = index < 0 ? sealedBytes.Length + index : index;
        sealedBytes[i] ^= 0x01;

        Assert.Throws<WrongPinException>(() => SecretBox.Open(sealedBytes, "bullseye7"));
    }

    public static TheoryData<string, Func<byte[], byte[]>> CorruptHeaders => new()
    {
        { "empty", _ => [] },
        { "truncated", b => b[..20] },
        { "bad magic", b => { b[0] = (byte)'X'; return b; } },
        { "unknown version", b => { b[4] = 99; return b; } },
        { "huge memory", b => { BitConverter.GetBytes(int.MaxValue).CopyTo(b, 5); return b; } },
        { "zero iterations", b => { BitConverter.GetBytes(0).CopyTo(b, 9); return b; } },
    };

    [Theory]
    [MemberData(nameof(CorruptHeaders))]
    public void Open_CorruptHeader_ThrowsCorruptSettings(string _, Func<byte[], byte[]> corrupt)
    {
        var sealedBytes = corrupt(SecretBox.Seal(Plain, "bullseye7", Fast));

        Assert.Throws<CorruptSettingsException>(() => SecretBox.Open(sealedBytes, "bullseye7"));
    }
}
