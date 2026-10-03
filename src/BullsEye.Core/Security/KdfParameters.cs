namespace BullsEye.Core;

/// <summary>Argon2id 비용 파라미터. 파일 헤더에 저장되므로 나중에 바꿔도 예전 파일을 열 수 있다.</summary>
public sealed record KdfParameters(int MemoryKiB, int Iterations, int Parallelism)
{
    /// <summary>일반 PC 기준 잠금 해제 약 0.5초.</summary>
    public static KdfParameters Default { get; } = new(MemoryKiB: 64 * 1024, Iterations: 3, Parallelism: 4);
}
