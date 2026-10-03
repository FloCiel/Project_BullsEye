namespace BullsEye.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private static readonly KdfParameters Fast = new(MemoryKiB: 1024, Iterations: 1, Parallelism: 1);
    private const string Pin = "bullseye7";

    private readonly string _dir = Directory.CreateTempSubdirectory("bullseye-test-").FullName;
    private string SettingsPath => Path.Combine(_dir, "BullsEye.dat");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Load_AfterSave_ReturnsSameSettings()
    {
        var store = new SettingsStore(SettingsPath, Fast);
        var settings = new Settings { ApiKey = "sk-or-v1-secret", History = ["주석 달아줘"] };

        Assert.False(store.Exists);
        store.Save(settings, Pin);

        Assert.True(store.Exists);
        Assert.Equivalent(settings, store.Load(Pin), strict: true);
    }

    [Fact]
    public void LeftoverTempFromInterruptedSave_DoesNotBreakLoadOrSave()
    {
        var store = new SettingsStore(SettingsPath, Fast);
        store.Save(new Settings { ApiKey = "first" }, Pin);
        File.WriteAllBytes(SettingsPath + ".tmp", [1, 2, 3]);

        Assert.Equal("first", store.Load(Pin).ApiKey);
        store.Save(new Settings { ApiKey = "second" }, Pin);
        Assert.Equal("second", store.Load(Pin).ApiKey);
    }

    [Fact]
    public void Reset_DeletesSettingsAndLeftovers()
    {
        var store = new SettingsStore(SettingsPath, Fast);
        store.Save(new Settings(), Pin);
        File.WriteAllBytes(SettingsPath + ".tmp", [1, 2, 3]);

        store.Reset();

        Assert.False(store.Exists);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Save_WithWeakPin_ThrowsAndWritesNothing()
    {
        var store = new SettingsStore(SettingsPath, Fast);

        Assert.Throws<WeakPinException>(() => store.Save(new Settings(), "1234"));
        Assert.Empty(Directory.GetFiles(_dir));
    }
}
