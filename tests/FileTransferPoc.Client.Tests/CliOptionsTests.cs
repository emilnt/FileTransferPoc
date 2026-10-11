namespace FileTransferPoc.Client.Tests;

public sealed class CliOptionsTests
{
    private static readonly Func<string, string?> NoEnv = _ => null;

    [Fact]
    public void Builds_spec_uri_from_guid_and_base_url()
    {
        var options = CliOptions.Parse(["550e8400-e29b-41d4-a716-446655440000", "--base-url", "https://api.virksomhet.no"], NoEnv);

        Assert.Equal("https://api.virksomhet.no/filer/v1/nedlasting/550e8400-e29b-41d4-a716-446655440000", options.FileUri.ToString());
        Assert.Equal("https://api.virksomhet.no/token", options.TokenEndpoint.ToString());
        Assert.Equal("550e8400-e29b-41d4-a716-446655440000", options.OutputPath);
    }

    [Fact]
    public void Accepts_full_uri_and_reads_environment()
    {
        var env = new Dictionary<string, string> { ["ESAS_CLIENT_ID"] = "abc", ["ESAS_TOKEN_ENDPOINT"] = "https://test.maskinporten.no/token" };

        var options = CliOptions.Parse(
            ["https://x.no/filer/v1/nedlasting/550e8400-e29b-41d4-a716-446655440000", "--out", "a.pdf", "--checksum", "md5:098f6bcd4621d373cade4e832627b4f6"],
            env.GetValueOrDefault);

        Assert.Equal("abc", options.ClientId);
        Assert.Equal("https://test.maskinporten.no/token", options.TokenEndpoint.ToString());
        Assert.Equal("a.pdf", options.OutputPath);
        Assert.Equal("MD5", options.Checksum!.Algorithm.Name);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("https://x.no/filer/v1/nedlasting/123")]
    public void Rejects_targets_without_guid(string target) =>
        Assert.Throws<FormatException>(() => CliOptions.Parse([target], NoEnv));
}
