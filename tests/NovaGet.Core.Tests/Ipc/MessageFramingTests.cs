using System.Text;
using NovaGet.Core.Ipc;

namespace NovaGet.Core.Tests.Ipc;

public sealed class MessageFramingTests
{
    [Fact]
    public async Task Round_trips_multiple_messages()
    {
        using var stream = new MemoryStream();
        await MessageFraming.WriteAsync(stream, Encoding.UTF8.GetBytes("{\"a\":1}"));
        await MessageFraming.WriteAsync(stream, Encoding.UTF8.GetBytes("{}"));
        stream.Position = 0;

        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString((await MessageFraming.ReadAsync(stream))!));
        Assert.Equal("{}", Encoding.UTF8.GetString((await MessageFraming.ReadAsync(stream))!));
        Assert.Null(await MessageFraming.ReadAsync(stream));
    }

    [Fact]
    public async Task Header_is_little_endian_length()
    {
        using var stream = new MemoryStream();
        await MessageFraming.WriteAsync(stream, new byte[258]);

        var bytes = stream.ToArray();
        Assert.Equal([2, 1, 0, 0], bytes[..4]);
        Assert.Equal(262, bytes.Length);
    }

    [Fact]
    public async Task Rejects_oversized_message()
    {
        using var stream = new MemoryStream();
        await MessageFraming.WriteAsync(stream, new byte[100]);
        stream.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => MessageFraming.ReadAsync(stream, maxMessageBytes: 50));
    }

    [Fact]
    public async Task Rejects_truncated_body()
    {
        using var stream = new MemoryStream([10, 0, 0, 0, 1, 2, 3]);

        await Assert.ThrowsAsync<InvalidDataException>(() => MessageFraming.ReadAsync(stream));
    }

    [Fact]
    public async Task Rejects_negative_length()
    {
        using var stream = new MemoryStream([0xFF, 0xFF, 0xFF, 0xFF]);

        await Assert.ThrowsAsync<InvalidDataException>(() => MessageFraming.ReadAsync(stream));
    }
}
