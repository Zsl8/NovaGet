using System.Text;
using NovaGet.Core.Services;
using NovaGet.Core.Tests.Infrastructure;

namespace NovaGet.Core.Tests.Services;

public sealed class ChecksumTests
{
    [Theory]
    [InlineData(Checksum.Md5, "9e107d9d372bb6826bd81d3542a419d6")]
    [InlineData(Checksum.Sha1, "2fd4e1c67a2d28fced849ee1bb76e7391b93eb12")]
    [InlineData(Checksum.Sha256, "d7a8fbb307d7809469ca9abcb0082e4f8d5651e46d3cdb762d02d0bf37c9e592")]
    public async Task Known_digests(string algorithm, string expected)
    {
        using var temp = new TempDirectory();
        var file = temp.Combine("fox.txt");
        await File.WriteAllTextAsync(file, "The quick brown fox jumps over the lazy dog", Encoding.ASCII);
        var reports = new List<double>();

        var actual = await Checksum.ComputeAsync(file, algorithm, new SyncProgress(reports));

        Assert.Equal(expected, actual);
        Assert.Equal(1.0, reports[^1]);
        Assert.True(Checksum.Matches(expected.ToUpperInvariant(), actual));
        Assert.Equal(algorithm, Checksum.GuessAlgorithm(expected));
    }

    [Theory]
    [InlineData("sha256:D7A8FBB307D7809469CA9ABCB0082E4F8D5651E46D3CDB762D02D0BF37C9E592")]
    [InlineData("  d7a8fbb3 07d78094 69ca9abc b0082e4f 8d5651e4 6d3cdb76 2d02d0bf 37c9e592 ")]
    public void Matches_pasted_values(string pasted)
    {
        Assert.True(Checksum.Matches(pasted, "d7a8fbb307d7809469ca9abcb0082e4f8d5651e46d3cdb762d02d0bf37c9e592"));
    }

    [Fact]
    public void Mismatch_and_empty()
    {
        Assert.False(Checksum.Matches("abc", "abd"));
        Assert.False(Checksum.Matches(null, "abc"));
        Assert.Null(Checksum.GuessAlgorithm("xyz"));
    }

    private sealed class SyncProgress(List<double> sink) : IProgress<double>
    {
        public void Report(double value) => sink.Add(value);
    }
}
