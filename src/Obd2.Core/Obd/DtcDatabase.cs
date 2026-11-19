using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Obd2.Core.Obd;

/// <summary>One entry of the trouble code database.</summary>
public sealed record DtcEntry(
    string Code,
    string Description,
    string System,
    string Explanation = "",
    IReadOnlyList<string>? Causes = null,
    IReadOnlyList<string>? Symptoms = null,
    string Source = "Generic SAE J2012",
    bool EmissionsRelevant = false,
    bool LimpModePossible = false)
{
    public char Category => Code[0];

    /// <summary>True for Toyota-specific entries and for entries imported from the user's own files.</summary>
    public bool IsManufacturerSpecific =>
        Source.StartsWith("Toyota", StringComparison.Ordinal) || Source.StartsWith("Imported", StringComparison.Ordinal);
}

/// <summary>
/// Offline trouble code database.
/// - 9,500+ generic SAE J2012 codes with explanations, common causes and symptoms (OBDex, CC0).
/// - A few built-in Toyota-specific codes.
/// - Any codes the user drops into a folder as .json or .csv (see <see cref="LoadUserCodes"/>), so a full
///   Toyota list from a licensed source can be added without changing the app.
/// </summary>
public static class DtcDatabase
{
    private static readonly Regex CodePattern = new("^[PBCU][0-9A-F]{4}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Lazy<List<DtcEntry>> Builtin = new(LoadBuiltin);
    private static readonly object Gate = new();
    private static List<DtcEntry> _user = new();
    private static IReadOnlyList<DtcEntry>? _merged;

    public static IReadOnlyList<DtcEntry> All
    {
        get
        {
            lock (Gate)
                return _merged ??= Merge();
        }
    }

    private static Dictionary<string, DtcEntry>? _index;

    /// <summary>The entry for a code with its full explanation, causes and symptoms, or null when unknown.</summary>
    public static DtcEntry? Find(string code) =>
        Lookup(code) is { } entry ? WithDetails(entry) : null;

    /// <summary>Quick lookup of the code's title and system only (no explanation, causes or symptoms).</summary>
    public static DtcEntry? Lookup(string code)
    {
        lock (Gate)
        {
            _index ??= All.ToDictionary(e => e.Code, StringComparer.OrdinalIgnoreCase);
            return _index.GetValueOrDefault(code.Trim());
        }
    }

    // ---- details on demand ----
    // The explanations, causes and symptoms of the 9,500 generic codes are about 20 MB once loaded, but only one is
    // ever shown at a time. The list keeps titles only; details are read from the compressed data when a code is opened.

    private const string GenericSource = "Generic SAE J2012";
    private const int DetailCacheSize = 64;
    private static readonly Dictionary<string, DtcEntry> DetailCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> HasStoredDetails = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the entry with its explanation, causes and symptoms filled in (when the built-in data has them).</summary>
    public static DtcEntry WithDetails(DtcEntry entry)
    {
        if (entry.Source != GenericSource || entry.Causes is not null || entry.Explanation.Length > 0)
            return entry;

        lock (DetailCache)
        {
            if (DetailCache.TryGetValue(entry.Code, out var cached)) return cached;
        }
        _ = Builtin.Value; // makes sure HasStoredDetails is filled
        if (!HasStoredDetails.Contains(entry.Code)) return entry;

        var raw = ReadStoredDetails(entry.Code);
        if (raw is null) return entry;

        var full = entry with
        {
            Explanation = raw.d,
            Causes = raw.cc.Select(x => string.IsNullOrEmpty(x.p) ? x.l : $"{x.l} ({x.p} likelihood)").ToList(),
            Symptoms = raw.s,
        };
        lock (DetailCache)
        {
            if (DetailCache.Count >= DetailCacheSize) DetailCache.Clear();
            DetailCache[entry.Code] = full;
        }
        return full;
    }

    /// <summary>Finds one code's record in the compressed built-in data and reads just that record.</summary>
    private static RawEntry? ReadStoredDetails(string code)
    {
        using var stream = typeof(DtcDatabase).Assembly.GetManifestResourceStream("dtc_obdex.json.gz");
        if (stream is null) return null;

        // The last four bytes of a gzip file hold the uncompressed size.
        var sizeBytes = new byte[4];
        stream.Seek(-4, SeekOrigin.End);
        stream.ReadExactly(sizeBytes);
        int size = BitConverter.ToInt32(sizeBytes);
        stream.Seek(0, SeekOrigin.Begin);

        // A plain temporary buffer, not a pooled one: a pool would keep this 12 MB alive after the first use.
        byte[] buffer = GC.AllocateUninitializedArray<byte>(size);
        using (var gzip = new GZipStream(stream, CompressionMode.Decompress))
            gzip.ReadExactly(buffer, 0, size);

        var json = buffer.AsSpan();
        int start = json.IndexOf(System.Text.Encoding.ASCII.GetBytes($"{{\"c\":\"{code.ToUpperInvariant()}\""));
        if (start < 0) return null;

        var reader = new Utf8JsonReader(json[start..]);
        reader.Read();   // the record's opening brace
        reader.Skip();   // to its closing brace
        return JsonSerializer.Deserialize<RawEntry>(json.Slice(start, (int)reader.BytesConsumed));
    }

    public static IReadOnlyList<DtcEntry> Search(string? text, char? category = null, int limit = 500) =>
        Search(text, category, limit, out _);

    /// <summary>Searches code, title and system. Every word typed must match. <paramref name="total"/> is the full match count.</summary>
    public static IReadOnlyList<DtcEntry> Search(string? text, char? category, int limit, out int total)
    {
        var words = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var matches = All
            .Where(e => category is null || e.Category == char.ToUpperInvariant(category.Value))
            .Where(e => words.All(w =>
                e.Code.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                e.Description.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                e.System.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        total = matches.Count;
        return matches.Count > limit ? matches.Take(limit).ToList() : matches;
    }

    /// <summary>
    /// Reads every *.json and *.csv file in <paramref name="folder"/> and adds the codes (they override built-in
    /// entries with the same code). JSON: an array of objects with "dtc" or "code" and "description" or "title".
    /// CSV: code in the first column, description in the second. Returns the number of codes loaded.
    /// </summary>
    public static int LoadUserCodes(string folder)
    {
        var loaded = new Dictionary<string, DtcEntry>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.*").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                try
                {
                    if (ext == ".json") ReadJson(file, loaded);
                    else if (ext == ".csv") ReadCsv(file, loaded);
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    // A bad file must not break the app; its codes are simply skipped.
                }
            }
        }

        lock (Gate)
        {
            _user = loaded.Values.ToList();
            _merged = null;
            _index = null;
        }
        return loaded.Count;
    }

    /// <summary>Describes the system a code belongs to from its prefix (SAE J2012 code ranges).</summary>
    public static string SystemOf(string code)
    {
        if (code.Length < 5) return "Unknown";

        return code[0] switch
        {
            'P' => code[1] == '1' || code[1] == '3'
                ? "Powertrain (manufacturer-specific range)"
                : code[2] switch
                {
                    '0' or '1' or '2' => "Powertrain: fuel and air metering",
                    '3' => "Powertrain: ignition / misfire",
                    '4' => "Powertrain: auxiliary emission controls",
                    '5' => "Powertrain: speed, idle and auxiliary inputs",
                    '6' => "Powertrain: computer and output circuits",
                    '7' or '8' or '9' => "Powertrain: transmission",
                    _ => "Powertrain",
                },
            'C' => "Chassis (brakes, steering, suspension)",
            'B' => "Body (airbags, comfort, lighting)",
            'U' => "Network (communication)",
            _ => "Unknown",
        };
    }

    private static IReadOnlyList<DtcEntry> Merge()
    {
        var map = new Dictionary<string, DtcEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Builtin.Value) map[e.Code] = e;
        foreach (var e in _user) map[e.Code] = e;
        return map.Values.OrderBy(e => e.Code, StringComparer.Ordinal).ToList();
    }

    // ---- built-in data ----

    private sealed class RawCause { public string l { get; set; } = ""; public string p { get; set; } = ""; }

    private sealed class RawEntry
    {
        public string c { get; set; } = "";
        public string t { get; set; } = "";
        public string d { get; set; } = "";
        public List<RawCause> cc { get; set; } = new();
        public List<string> s { get; set; } = new();
        public bool emi { get; set; }
        public bool limp { get; set; }
    }

    /// <summary>The fields kept in memory for the list: the long text is skipped while reading, not stored.</summary>
    private sealed class RawTitle
    {
        public string c { get; set; } = "";
        public string t { get; set; } = "";
        public bool emi { get; set; }
        public bool limp { get; set; }
    }

    private static List<DtcEntry> LoadBuiltin()
    {
        var map = new Dictionary<string, DtcEntry>(StringComparer.OrdinalIgnoreCase);
        var assembly = typeof(DtcDatabase).Assembly;

        using (var stream = assembly.GetManifestResourceStream("dtc_obdex.json.gz"))
        {
            if (stream is not null)
            {
                using var gzip = new GZipStream(stream, CompressionMode.Decompress);
                var rows = JsonSerializer.Deserialize<List<RawTitle>>(gzip) ?? new();
                foreach (var r in rows)
                {
                    // Explanation, causes and symptoms are read on demand by WithDetails.
                    map[r.c] = new DtcEntry(r.c, r.t, SystemOf(r.c), Source: GenericSource,
                        EmissionsRelevant: r.emi, LimpModePossible: r.limp);
                    HasStoredDetails.Add(r.c);
                }
            }
        }

        // Small hand-written set (generic + a few Toyota codes). Only fills gaps; the richer data above wins.
        using (var stream = assembly.GetManifestResourceStream("dtc.txt"))
        {
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    int bar = line.IndexOf('|');
                    if (bar != 5) continue;

                    string code = line[..5].Trim().ToUpperInvariant();
                    string text = line[(bar + 1)..].Trim();
                    bool toyota = text.Contains("[Toyota-specific]", StringComparison.Ordinal);
                    text = text.Replace("[Toyota-specific]", "").Trim();

                    if (toyota || !map.ContainsKey(code))
                        map[code] = new DtcEntry(code, text, SystemOf(code), Source: toyota ? "Toyota-specific" : "Generic SAE J2012");
                }
            }
        }

        return map.Values.ToList();
    }

    // ---- user import ----

    private static void ReadJson(string file, Dictionary<string, DtcEntry> into)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            string? code = Prop(item, "dtc") ?? Prop(item, "code");
            string? text = Prop(item, "description") ?? Prop(item, "title") ?? Prop(item, "desc");
            Add(into, code, text, Path.GetFileName(file));
        }
    }

    private static void ReadCsv(string file, Dictionary<string, DtcEntry> into)
    {
        foreach (var line in File.ReadLines(file))
        {
            var parts = SplitCsv(line);
            if (parts.Count >= 2) Add(into, parts[0], parts[1], Path.GetFileName(file));
        }
    }

    private static string? Prop(JsonElement item, string name) =>
        item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void Add(Dictionary<string, DtcEntry> into, string? code, string? text, string source)
    {
        code = code?.Trim().ToUpperInvariant();
        text = text?.Trim().TrimEnd('.');
        if (code is null || text is null || text.Length == 0 || !CodePattern.IsMatch(code)) return;

        into[code] = new DtcEntry(code, text, SystemOf(code), Source: "Imported: " + source);
    }

    private static List<string> SplitCsv(string line)
    {
        var parts = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char ch in line)
        {
            if (ch == '"') quoted = !quoted;
            else if ((ch == ',' || ch == ';') && !quoted) { parts.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(ch);
        }
        parts.Add(cur.ToString());
        return parts;
    }
}
