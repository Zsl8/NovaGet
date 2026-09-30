using NovaGet.Core.Engine;
using NovaGet.Core.Models;

namespace NovaGet.Core.Tests.Engine;

public sealed class SegmentMapTests
{
    [Fact]
    public void New_map_has_one_segment_covering_the_file()
    {
        var map = SegmentMap.Create(1000);

        var owner = new object();
        var segment = map.AcquirePending(owner)!;
        Assert.Equal(0, segment.Start);
        Assert.Equal(999, segment.End);
        Assert.Null(map.AcquirePending(new object()));
    }

    [Fact]
    public void Accept_clamps_to_the_segment_end()
    {
        var map = SegmentMap.Create(100);
        var segment = map.AcquirePending(this)!;

        Assert.Equal(60, map.Accept(segment, 60));
        Assert.Equal(40, map.Accept(segment, 60));
        Assert.Equal(0, map.Accept(segment, 10));
        Assert.True(segment.IsReceived);
        Assert.False(map.IsComplete); // nothing written yet

        map.MarkWritten(segment, 100);
        Assert.True(map.IsComplete);
        Assert.Equal(100, map.WrittenBytes);
    }

    [Fact]
    public void Checkpoints_persist_written_not_received_offsets()
    {
        var map = SegmentMap.Create(1000);
        var segment = map.AcquirePending(this)!;
        map.Accept(segment, 500);
        map.MarkWritten(segment, 300);

        var saved = map.ToPersisted();

        Assert.Equal(300, saved.Single().CurrentByte);
        Assert.Equal(500, map.ReceivedBytes);

        var restored = SegmentMap.Restore(saved, 1000);
        Assert.Equal(300, restored.WrittenBytes);
        Assert.Equal(300, restored.ReceivedBytes);
    }

    [Fact]
    public void Unknown_size_completes_at_end_of_stream()
    {
        var map = SegmentMap.Create(-1);
        var segment = map.AcquirePending(this)!;
        map.Accept(segment, 12345);
        map.MarkWritten(segment, 12345);
        Assert.False(map.IsComplete);

        map.CompleteUnknownSize(segment);

        Assert.True(map.IsComplete);
        Assert.Equal(12345, map.Size);
    }

    [Fact]
    public void Empty_file_is_complete_immediately()
    {
        Assert.True(SegmentMap.Create(0).IsComplete);
    }

    [Fact]
    public void Restore_orders_segments_and_keeps_done_ones()
    {
        var map = SegmentMap.Restore(
        [
            new Segment { StartByte = 500, EndByte = 999, CurrentByte = 700 },
            new Segment { StartByte = 0, EndByte = 499, CurrentByte = 500, State = SegmentState.Done },
        ], 1000);

        var pending = map.AcquirePending(this)!;
        Assert.Equal(500, pending.Start);
        Assert.Equal(700, pending.Received);
        Assert.Equal(700, map.WrittenBytes);
    }
}
