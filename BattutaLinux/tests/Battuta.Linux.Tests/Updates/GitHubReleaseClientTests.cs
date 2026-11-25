using System.Net;
using System.Net.Http;
using System.Text;
using Battuta.Windows.Updates;

using Battuta.Linux;

namespace Battuta.Linux.Tests.Updates;

public sealed class GitHubReleaseClientTests
{
    [Fact]
    public async Task SelectsLatestStableReleaseWithCompatibleLinuxAsset()
    {
        var json = """
            [
              {
                "tag_name": "v1.1.0",
                "html_url": "https://github.com/wormforce/battuta/releases/tag/v1.1.0",
                "draft": false,
                "prerelease": false,
                "published_at": "2026-08-25T07:32:31Z",
                "assets": [
                  { "name": "Battuta-Windows-1.1.0-win-x64.zip" },
                  { "name": "Battuta-1.1.0-unnotarized.dmg" }
                ]
              },
              {
                "tag_name": "v0.1.1",
                "html_url": "https://github.com/wormforce/battuta/releases/tag/v0.1.1",
                "draft": false,
                "prerelease": false,
                "published_at": "2026-08-25T08:00:00Z",
                "assets": [
                  { "name": "Battuta-Linux-0.1.1-linux-x64.tar.gz" }
                ]
              }
            ]
            """;
        using var httpClient = new HttpClient(new JsonHandler(json));
        var client = new GitHubReleaseClient(httpClient, "Battuta-Linux/1.0", GpuiRuntimeHost.MatchesLinuxReleaseAsset);

        var result = await client.FetchLatestAsync(etag: null);

        var modified = Assert.IsType<ReleaseFetchResult.Modified>(result);
        Assert.Equal("v0.1.1", modified.Release.TagName);
    }

    [Fact]
    public async Task IgnoresOtherPlatformAndLinuxPrereleaseEntries()
    {
        var json = """
            [
              {
                "tag_name": "v1.1.0",
                "html_url": "https://github.com/wormforce/battuta/releases/tag/v1.1.0",
                "draft": false,
                "prerelease": false,
                "assets": [{ "name": "Battuta-1.1.0-unnotarized.dmg" }]
              },
              {
                "tag_name": "v0.1.1-rc.1",
                "html_url": "https://github.com/wormforce/battuta/releases/tag/v0.1.1-rc.1",
                "draft": false,
                "prerelease": true,
                "assets": [{ "name": "Battuta-Linux-0.1.1-linux-x64.tar.gz" }]
              }
            ]
            """;
        using var httpClient = new HttpClient(new JsonHandler(json));
        var client = new GitHubReleaseClient(httpClient, "Battuta-Linux/1.0", GpuiRuntimeHost.MatchesLinuxReleaseAsset);

        var exception = await Assert.ThrowsAsync<ReleaseClientException>(
            () => client.FetchLatestAsync(etag: null));

        Assert.Equal(ReleaseClientErrorKind.NoPublishedRelease, exception.Kind);
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(GitHubReleaseClient.ReleasesEndpoint, request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
