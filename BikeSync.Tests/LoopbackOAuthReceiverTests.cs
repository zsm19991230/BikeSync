using System.Net;
using System.Net.Sockets;
using BikeSync.Core.Services;

namespace BikeSync.Tests;

public class LoopbackOAuthReceiverTests
{
    [Fact]
    public async Task WaitForCallback_ReturnsCodeAndValidatesState()
    {
        var port = GetFreePort();
        var redirect = $"http://127.0.0.1:{port}/callback";
        var receiver = LoopbackOAuthReceiver.WaitForCallbackAsync(
            redirect,
            "expected-state",
            TimeSpan.FromSeconds(10),
            "Test success",
            CancellationToken.None);

        await Task.Delay(100);
        using var http = new HttpClient();
        var html = await http.GetStringAsync($"{redirect}?code=abc123&state=expected-state");

        var result = await receiver;
        Assert.Equal("abc123", result.Code);
        Assert.Equal("expected-state", result.State);
        Assert.Null(result.Error);
        Assert.Contains("Test success", html);
    }

    [Fact]
    public async Task WaitForCallback_RejectsWrongState()
    {
        var port = GetFreePort();
        var redirect = $"http://127.0.0.1:{port}/callback";
        var receiver = LoopbackOAuthReceiver.WaitForCallbackAsync(
            redirect,
            "expected-state",
            TimeSpan.FromSeconds(10),
            "Test success",
            CancellationToken.None);

        await Task.Delay(100);
        using var http = new HttpClient();
        await http.GetStringAsync($"{redirect}?code=abc123&state=wrong");

        await Assert.ThrowsAsync<InvalidOperationException>(() => receiver);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
