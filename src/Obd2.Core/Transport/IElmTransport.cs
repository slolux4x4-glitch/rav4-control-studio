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

    /// <summary>
    /// Longest reply accepted, in characters. Real replies to the read-only requests are a few hundred characters;
    /// anything streaming far past that is a faulty adapter or another device on its network, and is refused
    /// before it can use up memory.
    /// </summary>
    const int MaxReplyLength = 64 * 1024;

    internal static void CheckReplyLength(System.Text.StringBuilder text)
    {
        if (text.Length > MaxReplyLength)
            throw new IOException($"The adapter sent more than {MaxReplyLength / 1024} KB without finishing its reply. The connection was dropped.");
    }
}
