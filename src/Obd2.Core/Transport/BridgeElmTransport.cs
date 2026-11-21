using System.Net;
using System.Net.Http;
using System.Text;
using Obd2.Core.Bridge;

namespace Obd2.Core.Transport;

/// <summary>
/// Reaches the adapter through RAV4 Bridge on a laptop: each command is sent to the laptop's web server, which
/// passes it to the adapter and returns the reply exactly as the adapter sent it.
/// </summary>
public sealed class BridgeElmTransport : IElmTransport
{
    private static readonly Encoding Latin1 = Encoding.Latin1;
    private readonly string _host;
    private readonly int _port;
    private readonly string _code;
    private readonly HttpClient _http;
    private string? _pending;

    public BridgeElmTransport(string host, int port, string code, HttpMessageHandler? handler = null)
    {
        _host = host.Trim();
        _port = port;
        _code = code.Trim();
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan; // each request sets its own deadline
    }

    public string DisplayName { get; private set; } = "";

    private Uri Url(string path) => new UriBuilder("http", _host, _port, path).Uri;

    public void Open()
    {
        var (status, body) = Request(HttpMethod.Get, "/hello", null, TimeSpan.FromSeconds(10));
        if (status == HttpStatusCode.OK && body.StartsWith(BridgeServer.Banner, StringComparison.Ordinal))
        {
            string adapter = body.Split('\n').ElementAtOrDefault(1)?.Trim() ?? "";
            DisplayName = $"Laptop bridge {_host}:{_port}" + (adapter.Length > 0 ? $" ({adapter})" : "");
            return;
        }
        throw new IOException(status == HttpStatusCode.OK
            ? $"{_host}:{_port} is not RAV4 Bridge."
            : $"RAV4 Bridge at {_host}:{_port}: {body}");
    }

    public void WriteLine(string command) => _pending = command;

    public string ReadUntilPrompt(TimeSpan timeout)
    {
        // The app also calls this, without a new command, to collect a reply that arrived late. The bridge
        // already does that on the laptop, so there is never anything to collect here.
        var command = _pending;
        _pending = null;
        if (command is null)
            throw new TimeoutException("No prompt from adapter (nothing pending on the bridge). Partial reply: \"\"");

        var (status, body) = Request(HttpMethod.Post, "/elm", command, timeout + TimeSpan.FromSeconds(5),
            ("X-Timeout-Ms", ((int)timeout.TotalMilliseconds).ToString()));

        return status switch
        {
            HttpStatusCode.OK => body,
            HttpStatusCode.GatewayTimeout => throw new TimeoutException(body.Length > 0 ? body : "No prompt from adapter. Partial reply: \"\""),
            HttpStatusCode.Forbidden => throw new InvalidOperationException("The bridge refused the request: " + body),
            _ => throw new IOException($"RAV4 Bridge: {body}"),
        };
    }

    private (HttpStatusCode Status, string Body) Request(HttpMethod method, string path, string? body, TimeSpan deadline,
        params (string Name, string Value)[] headers)
    {
        using var message = new HttpRequestMessage(method, Url(path));
        message.Headers.Add("X-Bridge-Code", _code);
        foreach (var (name, value) in headers) message.Headers.Add(name, value);
        if (body is not null) message.Content = new StringContent(body, Encoding.ASCII, "text/plain");

        using var cts = new CancellationTokenSource(deadline);
        try
        {
            using var response = _http.Send(message, cts.Token);
            using var stream = response.Content.ReadAsStream(cts.Token);
            using var reader = new StreamReader(stream, Latin1);
            string text = reader.ReadToEnd();
            if (text.Length > IElmTransport.MaxReplyLength)
                throw new IOException("RAV4 Bridge sent an oversized reply.");
            return (response.StatusCode, text);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"No answer from RAV4 Bridge at {_host}:{_port} within {deadline.TotalSeconds:0} s. Partial reply: \"\"");
        }
        catch (HttpRequestException ex)
        {
            throw new IOException($"Could not reach RAV4 Bridge at {_host}:{_port}: {ex.Message}", ex);
        }
    }

    public void Dispose() => _http.Dispose();
}
