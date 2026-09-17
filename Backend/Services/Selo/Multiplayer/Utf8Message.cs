using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Unicode;

namespace Selo.Multiplayer;

/// <summary>
///     A protocol frame assembled straight into an <see cref="ArrayPool{T}" /> buffer.
///     Building one costs no string and no intermediate byte[]; the rental goes back on
///     <see cref="Dispose" />, which every send path runs in a finally.
/// </summary>
public sealed class Utf8Message(int capacity = 128) : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(capacity);
    private int _written;

    /// <summary>The frame written so far. Only valid until <see cref="Dispose" />.</summary>
    public ReadOnlyMemory<byte> Memory => _buffer.AsMemory(0, _written);

    public void Advance(int count)
    {
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Reserve(sizeHint > 0 ? sizeHint : 1);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Reserve(sizeHint > 0 ? sizeHint : 1);
        return _buffer.AsSpan(_written);
    }

    public void Dispose()
    {
        var rented = _buffer;
        _buffer = [];
        _written = 0;

        if (rented.Length > 0) ArrayPool<byte>.Shared.Return(rented);
    }

    public void Write(ReadOnlySpan<byte> value)
    {
        Reserve(value.Length);
        value.CopyTo(_buffer.AsSpan(_written));
        _written += value.Length;
    }

    public void Write(ReadOnlySpan<char> value)
    {
        Reserve(Encoding.UTF8.GetMaxByteCount(value.Length));
        _written += Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_written));
    }

    /// <summary>
    ///     Formats a value straight to UTF-8. Handing this to <c>Utf8.TryWrite</c> keeps the numeric paths on
    ///     the framework's own UTF-8 formatters and keeps the culture identical to ToString().
    /// </summary>
    public void Write<T>(T value)
    {
        int count;
        Reserve(32);

        while (!Utf8.TryWrite(_buffer.AsSpan(_written), $"{value}", out count))
            Reserve((_buffer.Length - _written) * 2 + 32);

        _written += count;
    }

    private void Reserve(int size)
    {
        if (_written + size <= _buffer.Length) return;

        var next = ArrayPool<byte>.Shared.Rent(Math.Max(_written + size, _buffer.Length * 2));
        _buffer.AsSpan(0, _written).CopyTo(next);

        if (_buffer.Length > 0) ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = next;
    }
}

/// <summary>
///     Lets a call site write <c>Broadcast($"current {index}")</c> and have the interpolation
///     land in a pooled buffer as UTF-8, instead of producing a string that then has to be
///     encoded into a second throwaway array.
/// </summary>
[InterpolatedStringHandler]
public readonly struct Utf8MessageHandler(int literalLength, int formattedCount)
{
    public Utf8Message Message { get; } = new(literalLength + formattedCount * 24);

    public void AppendLiteral(string value)
    {
        Message.Write(value.AsSpan());
    }

    public void AppendFormatted(string? value)
    {
        Message.Write(value.AsSpan());
    }

    public void AppendFormatted(ReadOnlySpan<char> value)
    {
        Message.Write(value);
    }

    public void AppendFormatted(ReadOnlyMemory<char> value)
    {
        Message.Write(value.Span);
    }

    public void AppendFormatted<T>(T value)
    {
        Message.Write(value);
    }
}