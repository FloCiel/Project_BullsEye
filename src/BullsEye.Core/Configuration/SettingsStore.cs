namespace BullsEye.Core;

/// <summary>exe 옆의 암호화 설정 파일(<c>BullsEye.dat</c>)을 읽고 쓴다.</summary>
public sealed class SettingsStore(string path, KdfParameters kdf)
{
    public SettingsStore(string path) : this(path, KdfParameters.Default)
    {
    }

    private string TempPath => path + ".tmp";

    public bool Exists => File.Exists(path);

    /// <summary>
    /// 임시 파일에 다 쓰고 디스크에 flush한 뒤 이름을 바꾼다.
    /// 쓰는 도중 USB를 뽑아도 기존 파일은 온전히 남는다.
    /// </summary>
    public void Save(Settings settings, string pin)
    {
        PinPolicy.Validate(pin);
        var sealedBytes = SecretBox.Seal(SettingsSerializer.Serialize(settings), pin, kdf);

        using (var stream = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(sealedBytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(TempPath, path, overwrite: true);
    }

    public Settings Load(string pin) => SettingsSerializer.Deserialize(SecretBox.Open(File.ReadAllBytes(path), pin));

    /// <summary>PIN 분실 시: 설정을 지우고 처음부터 다시 입력한다. 복구 수단은 없다.</summary>
    public void Reset()
    {
        File.Delete(path);
        File.Delete(TempPath);
    }
}
