using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace Obd2.Core.Transport;

/// <summary>ELM327 Wi-Fi adapter over TCP (typically 192.168.0.10, port 35000).</summary>
public sealed class TcpElmTransport : IElmTransport
{
    private readonly string _host;
    private readonly int _port;
    private TcpClient? _client;
    private NetworkStream? _stream;

    private readonly TimeSpan _connectTimeout;

    public TcpElmTransport(string host, int port, TimeSpan? connectTimeout = null)
    {
        _host = host;
        _port = port;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5);
    }

    public string DisplayName => $"Wi-Fi {_host}:{_port}";

    public void Open()
    {
        _client = new TcpClient { NoDelay = true, SendTimeout = 1000, ReceiveTimeout = 200 };
        var connect = _client.ConnectAsync(_host, _port);
        bool connected;
        try { connected = connect.Wait(_connectTimeout); }
        catch (AggregateException ex)
        {
            throw new IOException($"Could not connect to {_host}:{_port}: {ex.InnerException?.Message ?? ex.Message}", ex);
        }
        if (!connected)
            throw new TimeoutException($"No answer from {_host}:{_port} within {_connectTimeout.TotalSeconds:0.#} s. Check the Wi-Fi network and IP address.");
        _stream = _client.GetStream();
        DiscardGreeting();
    }

    /// <summary>
    /// Some Wi-Fi adapters send a banner or a '>' prompt as soon as the connection opens. Left in the buffer it
    /// would be read as the reply to the first command (ATZ), and every later reply would arrive one command late.
    /// Anything that arrives before the first command is thrown away.
    /// </summary>
    private void DiscardGreeting()
    {
        var buffer = new byte[256];
        var clock = Stopwatch.StartNew();
        var quietSince = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 1000 && quietSince.ElapsedMilliseconds < 250)
        {
            try
            {
                if (_stream!.DataAvailable)
                {
                    if (_stream.Read(buffer, 0, buffer.Length) == 0) return;
                    quietSince.Restart();
                }
                else Thread.Sleep(20);
            }
            catch (IOException) { return; } // a broken link shows up on the first command instead
        }
    }

    public void WriteLine(string command)
    {
        if (_stream is null) throw new InvalidOperationException("Wi-Fi adapter is not open.");
        var bytes = Encoding.ASCII.GetBytes(command + "\r");
        _stream.Write(bytes, 0, bytes.Length);
    }

    public string ReadUntilPrompt(TimeSpan timeout)
    {
        if (_stream is null) throw new InvalidOperationException("Wi-Fi adapter is not open.");

        var text = new StringBuilder();
        var buffer = new byte[256];
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < timeout)
        {
            int read;
            try
            {
                read = _stream.Read(buffer, 0, buffer.Length);
            }
            catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut })
            {
                continue; // receive timeout; keep waiting until our deadline
            }
            catch (IOException ex)
            {
                // A reset or aborted connection fails instantly on every retry, so report it straight away
                // instead of spinning until the deadline.
                throw new IOException("The Wi-Fi adapter connection was lost: " + (ex.InnerException?.Message ?? ex.Message), ex);
            }

            if (read == 0)
                throw new IOException("The Wi-Fi adapter closed the connection.");

            for (int i = 0; i < read; i++)
            {
                if (buffer[i] == '>')
                    return text.ToString();
                text.Append((char)buffer[i]);
            }
            IElmTransport.CheckReplyLength(text);
        }

        throw new TimeoutException($"No prompt from adapter after {timeout.TotalSeconds:0.#}s. Partial reply: \"{text}\"");
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _client?.Dispose();
    }
}
