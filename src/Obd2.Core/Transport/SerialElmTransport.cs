using System.Diagnostics;
using System.IO.Ports;
using System.Text;

namespace Obd2.Core.Transport;

/// <summary>ELM327 adapter on a Windows COM port (USB or Bluetooth SPP).</summary>
public sealed class SerialElmTransport : IElmTransport
{
    private readonly SerialPort _port;

    public SerialElmTransport(string portName, int baudRate)
    {
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 200,
            WriteTimeout = 1000,
            DtrEnable = true,
            RtsEnable = true,
            NewLine = "\r",
        };
    }

    public string DisplayName => $"{_port.PortName} @ {_port.BaudRate} baud";

    public void Open()
    {
        _port.Open();
        _port.DiscardInBuffer();
        _port.DiscardOutBuffer();
    }

    public void WriteLine(string command) => _port.Write(command + "\r");

    public string ReadUntilPrompt(TimeSpan timeout)
    {
        var text = new StringBuilder();
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < timeout)
        {
            int value;
            try
            {
                value = _port.ReadByte();
            }
            catch (TimeoutException)
            {
                continue; // no byte within ReadTimeout; keep waiting until our own deadline
            }

            if (value == '>')
                return text.ToString();

            text.Append((char)value);
            IElmTransport.CheckReplyLength(text);
        }

        throw new TimeoutException(
            $"No prompt from adapter after {timeout.TotalSeconds:0.#}s. Partial reply: \"{text}\"");
    }

    public void Dispose()
    {
        if (_port.IsOpen)
            _port.Close();
        _port.Dispose();
    }
}
