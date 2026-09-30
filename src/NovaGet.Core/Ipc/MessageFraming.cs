using System.Buffers.Binary;

namespace NovaGet.Core.Ipc;

/// <summary>
/// 32-bit little-endian length prefix followed by a UTF-8 JSON body.
/// This is the Chrome/Firefox native messaging wire format, and the named pipe uses it too
/// so the native host can relay messages without re-encoding them.
/// </summary>
public static class MessageFraming
{
    /// <summary>Default cap for a single message (native messaging allows up to 64 MiB from the browser).</summary>
    public const int DefaultMaxMessageBytes = 8 * 1024 * 1024;

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one message. Returns null on a clean end of stream before a header.</summary>
    /// <exception cref="InvalidDataException">The length is negative, too large or the stream ended mid-message.</exception>
    public static async Task<byte[]?> ReadAsync(Stream stream, int maxMessageBytes = DefaultMaxMessageBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[4];
        var read = await ReadExactlyOrEndAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        if (read < header.Length)
        {
            throw new InvalidDataException("Stream ended inside a message header.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > maxMessageBytes)
        {
            throw new InvalidDataException($"Message length {length} is outside the allowed range (0..{maxMessageBytes}).");
        }

        var body = new byte[length];
        if (await ReadExactlyOrEndAsync(stream, body, cancellationToken).ConfigureAwait(false) < length)
        {
            throw new InvalidDataException("Stream ended inside a message body.");
        }

        return body;
    }

    private static async Task<int> ReadExactlyOrEndAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }
}
