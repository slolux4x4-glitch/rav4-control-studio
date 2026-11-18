using System.Globalization;
using System.IO;
using System.Text;
using RAV4ControlStudio.ViewModels;

namespace RAV4ControlStudio.Services;

/// <summary>Writes one CSV row per poll cycle: timestamp, then one column per live parameter.</summary>
public sealed class CsvLogger : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly IReadOnlyList<PidValue> _columns;

    public CsvLogger(string path, IReadOnlyList<PidValue> columns)
    {
        _columns = columns;
        FilePath = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        // Buffered, flushed about once a second: at most a second of data is lost if the PC loses power.
        _writer = new StreamWriter(path, append: false, new UTF8Encoding(false));

        var header = new StringBuilder("Timestamp");
        foreach (var column in columns)
            header.Append(',').Append(Escape($"{column.Name} [{column.Unit}]"));
        _writer.WriteLine(header.ToString());
    }

    public string FilePath { get; }

    public int RowCount { get; private set; }

    public void WriteRow(DateTime timestamp)
    {
        var line = new StringBuilder(timestamp.ToString("O", CultureInfo.InvariantCulture));
        foreach (var column in _columns)
        {
            line.Append(',');
            if (column.HasValue)
                line.Append(column.Value.ToString("0.###", CultureInfo.InvariantCulture));
        }
        _writer.WriteLine(line.ToString());
        RowCount++;

        if (timestamp - _lastFlush >= TimeSpan.FromSeconds(1))
        {
            _writer.Flush();
            _lastFlush = timestamp;
        }
    }

    private DateTime _lastFlush = DateTime.MinValue;

    private static string Escape(string text) =>
        text.Contains(',') || text.Contains('"') ? $"\"{text.Replace("\"", "\"\"")}\"" : text;

    public void Dispose() => _writer.Dispose();
}
