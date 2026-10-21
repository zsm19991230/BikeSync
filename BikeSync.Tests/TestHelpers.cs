using System.Net;
using System.Text;
using BikeSync.Core.Abstractions;
using BikeSync.Core.Models;

namespace BikeSync.Tests;

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    public FakeHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string> RequestBodies { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (request.Content is not null)
        {
            RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
        }

        return await _handler(request, cancellationToken);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    public static HttpResponseMessage Bytes(byte[] bytes, string contentType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}

internal sealed class InMemoryAccountStore : IAccountStore
{
    public List<PlatformAccount> Accounts { get; } = new();

    public Task<IReadOnlyList<PlatformAccount>> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PlatformAccount>>(Accounts.ToArray());

    public Task SaveAsync(
        IReadOnlyList<PlatformAccount> accounts,
        CancellationToken cancellationToken = default)
    {
        Accounts.Clear();
        Accounts.AddRange(accounts);
        return Task.CompletedTask;
    }
}

internal sealed class InMemorySettingsStore : IAppSettingsStore
{
    public AppSettings Settings { get; set; } = new();

    public Task<AppSettings> LoadAsync(
        string defaultDownloadDirectory,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Settings);

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Settings = settings;
        return Task.CompletedTask;
    }
}
