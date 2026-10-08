using System.Text;

namespace BullsEye.Core.Tests.Support;

/// <summary>정해진 청크를 내보낸 뒤, 끝나거나(EOF) 예외를 던지거나 취소될 때까지 멈춘다.</summary>
/// <param name="gap">두 번째 청크부터 각 청크 앞에서 기다리는 시간 (서버가 띄엄띄엄 보내는 상황).</param>
internal sealed class ScriptedStream(string[] chunks, Exception? thenThrow = null, bool thenHang = false, TimeSpan gap = default) : Stream
{
    private int _next;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_next < chunks.Length)
        {
            if (_next > 0 && gap > TimeSpan.Zero) await Task.Delay(gap, cancellationToken);
            var bytes = Encoding.UTF8.GetBytes(chunks[_next++]);
            bytes.CopyTo(buffer);
            return bytes.Length;
        }

        if (thenThrow is not null) throw thenThrow;
        if (thenHang) await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
