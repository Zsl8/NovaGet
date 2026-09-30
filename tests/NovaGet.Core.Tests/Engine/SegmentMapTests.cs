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

    [Fact]
    public void Split_gives_the_second_half_of_the_largest_remaining_segment()
    {
        var map = SegmentMap.Create(1000);
        var a = new object();
        var b = new object();
        var c = new object();
        var first = map.AcquirePending(a)!;
        map.Accept(first, 100);

        var second = map.SplitLargest(b, minSegmentSize: 10)!;   // first has 900 left: [100..549] + [550..999]
        Assert.Equal(550, second.Start);
        Assert.Equal(999, second.End);
        Assert.Equal(549, first.End);

        map.Accept(first, 400);                                  // first: 50 left; second: 450 left
        var third = map.SplitLargest(c, minSegmentSize: 10)!;    // splits second, the largest
        Assert.Equal(775, third.Start);
        Assert.Equal(774, second.End);
        Assert.Same(c, third.Owner);

        // Received bytes can never be handed to another connection.
        Assert.Equal(50, map.Accept(first, 1000));
        Assert.True(first.IsReceived);
    }

    [Fact]
    public void Connection_that_finishes_early_takes_over_the_largest_remaining_segment()
    {
        var map = SegmentMap.Create(10_000);
        var slow = new object();
        var fast = new object();
        var slowSegment = map.AcquirePending(slow)!;
        var fastSegment = map.SplitLargest(fast, 100)!;          // [0..4999] and [5000..9999]
        map.Accept(slowSegment, 1000);                           // slow: 4000 left
        map.Accept(fastSegment, 5000);                           // fast is done
        map.MarkWritten(fastSegment, 10_000);
        map.Release(fastSegment);

        var takeover = map.AcquirePending(fast) ?? map.SplitLargest(fast, 100);

        Assert.NotNull(takeover);
        Assert.Equal(3000, takeover.Start);                      // midpoint of slow's remaining [1000..4999]
        Assert.Equal(4999, takeover.End);
        Assert.Equal(2999, slowSegment.End);
    }

    [Fact]
    public void Segments_are_not_split_below_the_minimum_size()
    {
        var map = SegmentMap.Create(200 * 1024);
        map.AcquirePending(this);

        Assert.NotNull(map.SplitLargest(new object(), 64 * 1024));   // 200 KB → two 100 KB halves
        Assert.Null(map.SplitLargest(new object(), 64 * 1024));      // 100 KB pieces can't make two 64 KB halves
        Assert.False(map.HasWorkFor(64 * 1024));
    }
}
