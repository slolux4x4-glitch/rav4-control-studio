using System.Globalization;
using System.Text.Json;

namespace Obd2.Core.Obd;

/// <summary>
/// A user-defined read-only parameter, for values the standard PID list does not cover (for example Toyota-specific
/// data). Service "01" reads a Mode 01 PID; service "22" reads a data identifier (UDS read-by-identifier).
/// The formula uses the reply bytes as A, B, C... for example "(A*256+B)/4 - 40".
/// </summary>
public sealed class CustomParameter
{
    public CustomParameter(string name, string unit, string service, string id, string? header,
        int bytes, string formula, double min, double max, string format)
    {
        Name = name; Unit = unit; Service = service; Id = id; Header = header;
        Bytes = bytes; Formula = formula; Min = min; Max = max; Format = format;
        Decode = FormulaEvaluator.Compile(formula);
    }

    public string Name { get; }
    public string Unit { get; }
    public string Service { get; }
    public string Id { get; }
    public string? Header { get; }
    public int Bytes { get; }
    public string Formula { get; }
    public double Min { get; }
    public double Max { get; }
    public string Format { get; }
    public Func<byte[], double> Decode { get; }

    public string Display(double value) =>
        (value.ToString(Format, CultureInfo.InvariantCulture) + " " + Unit).Trim();

    public static (IReadOnlyList<CustomParameter> Parameters, IReadOnlyList<string> Errors) LoadFolder(string folder)
    {
        var list = new List<CustomParameter>();
        var errors = new List<string>();
        if (!Directory.Exists(folder)) return (list, errors);

        foreach (var file in Directory.EnumerateFiles(folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.ValueKind != JsonValueKind.Array) { errors.Add($"{Path.GetFileName(file)}: expected a JSON array"); continue; }

                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    try { list.Add(Parse(item)); }
                    catch (Exception ex) when (ex is FormatException or KeyNotFoundException or InvalidOperationException or JsonException)
                    { errors.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return (list, errors);
    }

    private static CustomParameter Parse(JsonElement e)
    {
        string S(string n, string? d = null) =>
            e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : d ?? throw new KeyNotFoundException($"missing \"{n}\"");
        double N(string n, double d) => e.TryGetProperty(n, out var v) && v.TryGetDouble(out var x) ? x : d;

        string service = S("service", "01").ToUpperInvariant();
        string id = S("id").ToUpperInvariant();
        if (service is not ("01" or "22")) throw new FormatException($"service must be 01 or 22 (read-only), got {service}");
        if (service == "01" && id.Length != 2 || service == "22" && id.Length != 4 || !id.All(Uri.IsHexDigit))
            throw new FormatException($"bad id '{id}' for service {service}");

        string? header = e.TryGetProperty("header", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString()!.ToUpperInvariant() : null;
        if (header is not null && (header.Length != 3 || !header.All(Uri.IsHexDigit)))
            throw new FormatException($"bad header '{header}' (expected 3 hex digits, e.g. 7E0)");

        int bytes = (int)N("bytes", 1);
        if (bytes is < 1 or > 8) throw new FormatException("bytes must be 1 to 8");

        return new CustomParameter(S("name"), S("unit", ""), service, id, header, bytes, S("formula", "A"),
            N("min", 0), N("max", 100), S("format", "0.#"));
    }
}

/// <summary>Tiny arithmetic expression compiler: numbers, A..H (reply bytes), + - * / and parentheses.</summary>
public static class FormulaEvaluator
{
    /// <summary>Longest formula accepted. Real decoding formulas are a few dozen characters.</summary>
    public const int MaxLength = 200;

    /// <summary>Deepest nesting of brackets and minus signs accepted, so a broken file cannot exhaust the stack.</summary>
    public const int MaxDepth = 32;

    public static Func<byte[], double> Compile(string formula)
    {
        if (formula.Length > MaxLength)
            throw new FormatException($"formula is longer than {MaxLength} characters");
        var parser = new Parser(formula);
        var fn = parser.ParseExpression();
        parser.ExpectEnd();
        return fn;
    }

    private sealed class Parser
    {
        private readonly string _s;
        private int _i;

        public Parser(string s) => _s = s;

        public void ExpectEnd()
        {
            Skip();
            if (_i < _s.Length) throw new FormatException($"unexpected '{_s[_i]}' in formula '{_s}'");
        }

        public Func<byte[], double> ParseExpression()
        {
            var left = ParseTerm();
            while (true)
            {
                Skip();
                if (Peek('+')) { _i++; var l = left; var r = ParseTerm(); left = b => l(b) + r(b); }
                else if (Peek('-')) { _i++; var l = left; var r = ParseTerm(); left = b => l(b) - r(b); }
                else return left;
            }
        }

        private Func<byte[], double> ParseTerm()
        {
            var left = ParseFactor();
            while (true)
            {
                Skip();
                if (Peek('*')) { _i++; var l = left; var r = ParseFactor(); left = b => l(b) * r(b); }
                else if (Peek('/')) { _i++; var l = left; var r = ParseFactor(); left = b => { double d = r(b); return d == 0 ? 0 : l(b) / d; }; }
                else return left;
            }
        }

        private int _depth;

        private Func<byte[], double> ParseFactor()
        {
            Skip();
            if (_i >= _s.Length) throw new FormatException($"formula '{_s}' ends too early");

            if (Peek('-') || Peek('('))
            {
                if (++_depth > MaxDepth) throw new FormatException($"formula '{_s}' is nested too deeply");
                try
                {
                    if (Peek('-')) { _i++; var f = ParseFactor(); return b => -f(b); }

                    _i++;
                    var inner = ParseExpression();
                    Skip();
                    if (!Peek(')')) throw new FormatException($"missing ')' in formula '{_s}'");
                    _i++;
                    return inner;
                }
                finally { _depth--; }
            }

            char c = _s[_i];
            if (char.IsDigit(c) || c == '.')
            {
                int start = _i;
                while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
                double value = double.Parse(_s[start.._i], CultureInfo.InvariantCulture);
                return _ => value;
            }

            char v = char.ToUpperInvariant(c);
            if (v is >= 'A' and <= 'H')
            {
                _i++;
                int index = v - 'A';
                return b => index < b.Length ? b[index] : 0;
            }

            throw new FormatException($"unexpected '{c}' in formula '{_s}'");
        }

        private bool Peek(char c) => _i < _s.Length && _s[_i] == c;

        private void Skip()
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
        }
    }
}
