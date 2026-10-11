using FileTransferPoc.Api.Download;

namespace FileTransferPoc.Api.Tests;

public class ByteRangeTests
{
    private const long Length = 1000;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("items=0-10")]
    [InlineData("bytes=")]
    [InlineData("bytes=-")]
    [InlineData("bytes=abc-10")]
    [InlineData("bytes=10-5")]
    [InlineData("bytes=0-10,20-30")]
    public void Missing_invalid_or_multipart_ranges_give_the_full_file(string? header)
    {
        Assert.Equal(RangeDecision.Full(Length), ByteRange.Resolve(header, Length));
    }

    [Theory]
    [InlineData("bytes=0-0", 0, 1)]
    [InlineData("bytes=0-499", 0, 500)]
    [InlineData("bytes=500-999", 500, 500)]
    [InlineData("bytes=0-999", 0, 1000)]
    [InlineData("bytes=500-", 500, 500)]
    [InlineData("bytes=0-", 0, 1000)]
    [InlineData("bytes=-100", 900, 100)]
    [InlineData("bytes=-1000", 0, 1000)]
    [InlineData("BYTES= 10 - 19 ", 10, 10)]
    public void Satisfiable_ranges_give_partial_content(string header, long start, long length)
    {
        Assert.Equal(RangeDecision.Partial(start, length), ByteRange.Resolve(header, Length));
    }

    [Theory]
    [InlineData("bytes=0-1000")]
    [InlineData("bytes=0-99999999999")]
    [InlineData("bytes=-1001")]
    public void Range_larger_than_the_file_gives_200_with_the_whole_file(string header)
    {
        Assert.Equal(RangeDecision.Full(Length), ByteRange.Resolve(header, Length));
    }

    [Fact]
    public void Range_past_the_end_from_a_later_offset_is_clamped_so_resume_works()
    {
        Assert.Equal(RangeDecision.Partial(900, 100), ByteRange.Resolve("bytes=900-5000", Length));
    }

    [Theory]
    [InlineData("bytes=1000-")]
    [InlineData("bytes=1000-2000")]
    [InlineData("bytes=-0")]
    public void Ranges_outside_the_file_are_not_satisfiable(string header)
    {
        Assert.Equal(RangeOutcome.NotSatisfiable, ByteRange.Resolve(header, Length).Outcome);
    }

    [Fact]
    public void Empty_file_is_always_sent_whole()
    {
        Assert.Equal(RangeDecision.Full(0), ByteRange.Resolve("bytes=0-10", 0));
    }
}
