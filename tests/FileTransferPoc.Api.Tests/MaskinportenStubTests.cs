using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace FileTransferPoc.Api.Tests;

public class MaskinportenStubTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Stub_is_not_exposed_unless_enabled()
    {
        var response = await factory.CreateClient().PostAsync("/maskinporten-stub/token", Form("123456789", ApiFactory.Scope));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Token_from_the_stub_downloads_the_file()
    {
        using var withStub = factory.WithWebHostBuilder(builder => builder.UseSetting("Maskinporten:EnableStubIssuer", "true"));
        var client = withStub.CreateClient();
        var id = await factory.PublishAsync([1, 2, 3]);

        var tokenResponse = await client.PostAsync("/maskinporten-stub/token", Form(ApiFactory.AllowedOrg, ApiFactory.Scope));
        tokenResponse.EnsureSuccessStatusCode();
        var token = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Bearer", token.GetProperty("token_type").GetString());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.GetProperty("access_token").GetString());
        Assert.Equal([1, 2, 3], await client.GetByteArrayAsync($"/filer/v1/nedlasting/{id:D}"));
    }

    [Theory]
    [InlineData("12345", "esas:filer")]
    [InlineData("12345678x", "esas:filer")]
    [InlineData("123456789", "")]
    public async Task Stub_rejects_bad_input(string org, string scope)
    {
        using var withStub = factory.WithWebHostBuilder(builder => builder.UseSetting("Maskinporten:EnableStubIssuer", "true"));

        var response = await withStub.CreateClient().PostAsync("/maskinporten-stub/token", Form(org, scope));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static FormUrlEncodedContent Form(string org, string scope) =>
        new(new Dictionary<string, string> { ["consumer_org"] = org, ["scope"] = scope });
}
