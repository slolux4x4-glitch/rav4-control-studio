namespace Obd2.Core.Transport;

/// <summary>
/// Byte-level link to an ELM327-compatible adapter. Implementations: a real serial port
/// (<see cref="SerialElmTransport"/>) or an offline ECU simulator (<see cref="SimulatedElmTransport"/>).
/// </summary>
public interface IElmTransport : IDisposable
{
    string DisplayName { get; }

    void Open();

    /// <summary>Sends one command. The transport appends the carriage return.</summary>
    void WriteLine(string command);

    /// <summary>Reads everything up to the ELM327 '&gt;' prompt and returns the text before it.</summary>
    string ReadUntilPrompt(TimeSpan timeout);
}
