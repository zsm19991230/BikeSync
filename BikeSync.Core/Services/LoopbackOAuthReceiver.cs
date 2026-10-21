using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BikeSync.Core.Services;

internal sealed record OAuthCallbackResult(
    string? Code,
    string? Error,
    string? ErrorDescription,
    string? State);

/// <summary>
/// A tiny loopback HTTP receiver implemented over TcpListener. Unlike HttpListener,
/// it does not require a Windows URL ACL reservation for the current user.
/// </summary>
internal static class LoopbackOAuthReceiver
{
    private const int MaxHeaderBytes = 32 * 1024;

    public static async Task<OAuthCallbackResult> WaitForCallbackAsync(
        string redirectUri,
        string expectedState,
        TimeSpan timeout,
        string successTitle,
        CancellationToken cancellationToken)
    {
        var callbackUri = new Uri(redirectUri, UriKind.Absolute);
        if (!string.Equals(callbackUri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(callbackUri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("OAuth 回调地址必须是本机回环地址。");
        }

        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, callbackUri.Port);
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException(
                $"无法监听本机端口 {callbackUri.Port}，请确认没有其他程序正在占用它。",
                ex);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("等待浏览器授权超时，请重新尝试。");
                }

                using (client)
                {
                    client.ReceiveTimeout = 5000;
                    await using var stream = client.GetStream();
                    var requestLine = await ReadRequestLineAsync(stream, timeoutCts.Token).ConfigureAwait(false);
                    if (requestLine is null)
                    {
                        continue;
                    }

                    var parts = requestLine.Split(' ');
                    if (parts.Length < 2 || !parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteResponseAsync(stream, 405, "Method Not Allowed", "仅支持 GET 请求。", timeoutCts.Token)
                            .ConfigureAwait(false);
                        continue;
                    }

                    var pathAndQuery = parts[1];
                    if (!pathAndQuery.StartsWith(callbackUri.AbsolutePath, StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteResponseAsync(stream, 404, "Not Found", "页面不存在。", timeoutCts.Token)
                            .ConfigureAwait(false);
                        continue;
                    }

                    var query = ParseQuery(pathAndQuery);
                    query.TryGetValue("code", out var code);
                    query.TryGetValue("error", out var error);
                    query.TryGetValue("error_description", out var errorDescription);
                    query.TryGetValue("state", out var state);

                    var stateOk = string.Equals(state, expectedState, StringComparison.Ordinal);
                    var title = stateOk ? successTitle : "授权校验失败";
                    var message = !stateOk
                        ? "state 参数不匹配，已拒绝本次授权。请回到应用重新发起登录。"
                        : error is null
                            ? "授权已完成，可以关闭此页面并回到骑迹同步。"
                            : $"授权未完成：{errorDescription ?? error}";

                    var html = BuildResultPage(title, message, error is null && stateOk);
                    await WriteResponseAsync(stream, 200, "OK", html, timeoutCts.Token, isHtml: true)
                        .ConfigureAwait(false);

                    if (!stateOk)
                    {
                        throw new InvalidOperationException("OAuth state 校验失败，请重新登录。");
                    }

                    return new OAuthCallbackResult(code, error, errorDescription, state);
                }
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxHeaderBytes];
        var count = 0;

        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            count++;
            if (count >= 4 &&
                buffer[count - 4] == '\r' && buffer[count - 3] == '\n' &&
                buffer[count - 2] == '\r' && buffer[count - 1] == '\n')
            {
                break;
            }
        }

        if (count == 0)
        {
            return null;
        }

        var text = Encoding.ASCII.GetString(buffer, 0, count);
        var firstLineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
        return firstLineEnd < 0 ? text : text[..firstLineEnd];
    }

    private static Dictionary<string, string> ParseQuery(string pathAndQuery)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var question = pathAndQuery.IndexOf('?');
        if (question < 0 || question == pathAndQuery.Length - 1)
        {
            return result;
        }

        foreach (var pair in pathAndQuery[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var key = equals < 0 ? pair : pair[..equals];
            var value = equals < 0 ? string.Empty : pair[(equals + 1)..];
            result[Uri.UnescapeDataString(key.Replace("+", " "))] =
                Uri.UnescapeDataString(value.Replace("+", " "));
        }

        return result;
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        int status,
        string reason,
        string body,
        CancellationToken cancellationToken,
        bool isHtml = false)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var contentType = isHtml ? "text/html; charset=utf-8" : "text/plain; charset=utf-8";
        var header = $"HTTP/1.1 {status} {reason}\r\n" +
                     $"Content-Type: {contentType}\r\n" +
                     $"Content-Length: {payload.Length}\r\n" +
                     "Cache-Control: no-store\r\n" +
                     "Connection: close\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);

        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string BuildResultPage(string title, string message, bool success)
    {
        var accent = success ? "#2A9D6F" : "#D9534F";
        var mark = success ? "✓" : "!";
        return $$"""
            <!doctype html>
            <html lang="zh-CN">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width,initial-scale=1">
              <title>{{title}} · 骑迹同步</title>
              <style>
                *{box-sizing:border-box}body{margin:0;min-height:100vh;display:grid;place-items:center;
                background:#0d1117;color:#e6edf3;font-family:'Segoe UI','Microsoft YaHei UI',sans-serif}
                .card{width:min(440px,calc(100vw - 32px));background:#161d27;border:1px solid #263241;
                border-radius:20px;padding:36px;text-align:center;box-shadow:0 24px 80px #0008}
                .mark{width:56px;height:56px;border-radius:50%;display:grid;place-items:center;margin:0 auto 20px;
                background:{{accent}}22;color:{{accent}};font-size:28px;font-weight:700;border:1px solid {{accent}}55}
                h1{font-size:22px;margin:0 0 12px}p{color:#93a4b8;line-height:1.7;margin:0}
                .brand{margin-top:24px;font-size:12px;letter-spacing:.18em;color:#66788d}
              </style>
            </head>
            <body>
              <div class="card">
                <div class="mark">{{mark}}</div>
                <h1>{{title}}</h1>
                <p>{{message}}</p>
                <div class="brand">BIKESYNC</div>
              </div>
              <script>setTimeout(() => window.close(), 1800);</script>
            </body>
            </html>
            """;
    }
}
