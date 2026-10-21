using System.Net;
using System.Text;
using BikeSync.Core.Services;
using BikeSync.Core.Updates;
using Microsoft.Extensions.Logging.Abstractions;

namespace BikeSync.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public async Task Parses_github_release_and_selects_windows_asset()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-update", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            paths.EnsureCreated();
            using var http = new HttpClient(new Handler());
            var service = new UpdateService(http, paths, NullLogger<UpdateService>.Instance);
            var update = await service.CheckAsync(CancellationToken.None);
            Assert.NotNull(update);
            Assert.Equal("v9.9.9", update!.Tag);
            Assert.Equal("9.9.9", update.Version.ToString(3));
            Assert.Equal("https://example.test/BikeSync-win-x64.zip", update.DownloadUri!.ToString());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            const string json = """
                {"tag_name":"v9.9.9","name":"Test","body":"Notes","html_url":"https://example.test/release",
                 "assets":[
                   {"name":"BikeSync-source.zip","browser_download_url":"https://example.test/source.zip"},
                   {"name":"BikeSync-win-x64.zip","browser_download_url":"https://example.test/BikeSync-win-x64.zip"}
                 ]}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
