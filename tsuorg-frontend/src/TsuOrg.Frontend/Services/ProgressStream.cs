namespace TsuOrg.Frontend.Services;

/// <summary>
/// Stream wrapper that reports read progress (0–100) while HttpClient uploads multipart content.
/// </summary>
public sealed class ProgressStream : Stream
{
    private readonly Stream _inner;
    private readonly long _length;
    private readonly IProgress<int>? _progress;
    private long _position;

    public ProgressStream(Stream inner, long length, IProgress<int>? progress)
    {
        _inner = inner;
        _length = Math.Max(length, 1);
        _progress = progress;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        if (read > 0)
        {
            _position += read;
            var pct = (int)Math.Clamp(_position * 100.0 / _length, 0, 100);
            _progress?.Report(pct);
        }
        else
        {
            _progress?.Report(100);
        }

        return read;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }
}
