using System.Security.Cryptography;
using System.Text;
using FileTransferPoc.Client.Download;

namespace FileTransferPoc.Client.Tests;

public sealed class ChecksumTests
{
    private static readonly byte[] Data = Encoding.UTF8.GetBytes("test");

    [Theory]
    [InlineData("sha256:9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08")]
    [InlineData("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08")]
    [InlineData("SHA-256:n4bQgYhMfWWaL+qgxVrQFaO/TxsrC4Is0V1sFbDwCgg=")]
    [InlineData("md5:098f6bcd4621d373cade4e832627b4f6")]
    public async Task Parses_hex_and_base64_and_matches(string text)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, Data);
            Assert.True(await Checksum.Parse(text).MatchesAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("crc32:deadbeef")]
    [InlineData("sha256:abc")]
    [InlineData("sha256:" + "zz")]
    public void Rejects_bad_input(string text) => Assert.Throws<FormatException>(() => Checksum.Parse(text));

    [Fact]
    public void Round_trips_to_string() =>
        Assert.Equal("SHA256:" + Convert.ToHexStringLower(SHA256.HashData(Data)),
            Checksum.Parse("sha256:" + Convert.ToHexString(SHA256.HashData(Data))).ToString());
}
