using System.Globalization;

namespace Textalonia.Model.Fields;

/// <summary>Small bounded arithmetic language; never invokes host code.</summary>
internal sealed class FieldFormula(string source, Func<string, double> reference)
{
    private int _position;
    private int _depth;
    internal double Evaluate()
    {
        if (source.Length > FieldInstructionParser.MaximumInstructionLength) throw new FormatException("Formula exceeds the input limit.");
        var result = Comparison(); White();
        if (_position != source.Length || !double.IsFinite(result)) throw new FormatException("Invalid formula or non-finite result.");
        return result;
    }
    private double Comparison()
    {
        var left = Sum();
        foreach (var op in new[] { "<=", ">=", "<>", "=", "<", ">" })
            if (Take(op))
            {
                var right = Sum();
                return (op switch { "<=" => left <= right, ">=" => left >= right, "<>" => left != right,
                    "=" => left == right, "<" => left < right, _ => left > right }) ? 1 : 0;
            }
        return left;
    }
    private double Sum()
    {
        var value = Product();
        while (true) { if (Take("+")) value += Product(); else if (Take("-")) value -= Product(); else return value; }
    }
    private double Product()
    {
        var value = Unary();
        while (true) { if (Take("*")) value *= Unary(); else if (Take("/")) value /= Unary(); else return value; }
    }
    private double Unary()
    {
        if (++_depth > 64) throw new FormatException("Formula nesting exceeds the limit.");
        try { return Take("+") ? Unary() : Take("-") ? -Unary() : Power(); }
        finally { _depth--; }
    }
    private double Power()
    {
        if (++_depth > 64) throw new FormatException("Formula nesting exceeds the limit.");
        try { var value = Atom(); return Take("^") ? Math.Pow(value, Unary()) : value; }
        finally { _depth--; }
    }
    private double Atom()
    {
        if (++_depth > 64) throw new FormatException("Formula nesting exceeds the limit.");
        try
        {
            if (Take("(")) { var value = Comparison(); Require(")"); return value; }
            White(); var start = _position;
            if (_position < source.Length && (char.IsDigit(source[_position]) || source[_position] == '.'))
            {
                while (_position < source.Length && (char.IsDigit(source[_position]) || source[_position] == '.')) _position++;
                if (_position < source.Length && source[_position] is 'e' or 'E')
                {
                    _position++;
                    if (_position < source.Length && source[_position] is '+' or '-') _position++;
                    while (_position < source.Length && char.IsDigit(source[_position])) _position++;
                }
                if (!double.TryParse(source[start.._position], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) throw new FormatException("Invalid formula number.");
                if (Take("%")) value /= 100;
                return value;
            }
            while (_position < source.Length && (char.IsLetterOrDigit(source[_position]) || source[_position] == '_')) _position++;
            if (start == _position) throw new FormatException("Expected a number, bookmark, or function.");
            var name = source[start.._position];
            if (!Take("(")) return name.ToUpperInvariant() switch { "TRUE" => 1, "FALSE" => 0, _ => reference(name) };
            var values = new List<double>();
            if (!Take(")"))
            {
                do { if (values.Count >= 1024) throw new FormatException("Too many function arguments."); values.Add(Comparison()); } while (Take(",") || Take(";"));
                Require(")");
            }
            var args = values.ToArray();
            return name.ToUpperInvariant() switch
            {
                "ABS" when args.Length == 1 => Math.Abs(args[0]),
                "INT" when args.Length == 1 => Math.Floor(args[0]),
                "SIGN" when args.Length == 1 => Math.Sign(args[0]),
                "ROUND" when args.Length == 2 && args[1] is >= 0 and <= 15 => Math.Round(args[0], (int)args[1], MidpointRounding.AwayFromZero),
                "MOD" when args.Length == 2 => args[0] % args[1],
                "SUM" => args.Sum(), "PRODUCT" => args.Aggregate(1d, (a, b) => a * b), "COUNT" => args.Length,
                "AVERAGE" when args.Length > 0 => args.Average(), "MIN" when args.Length > 0 => args.Min(), "MAX" when args.Length > 0 => args.Max(),
                "IF" when args.Length == 3 => args[0] != 0 ? args[1] : args[2],
                "AND" when args.Length > 0 => args.All(v => v != 0) ? 1 : 0,
                "OR" when args.Length > 0 => args.Any(v => v != 0) ? 1 : 0,
                "NOT" when args.Length == 1 => args[0] == 0 ? 1 : 0,
                _ => throw new FormatException($"Unsupported formula function or argument count: {name}.")
            };
        }
        finally { _depth--; }
    }
    private bool Take(string text)
    {
        White();
        if (!source.AsSpan(_position).StartsWith(text, StringComparison.Ordinal)) return false;
        _position += text.Length; return true;
    }
    private void Require(string text) { if (!Take(text)) throw new FormatException($"Expected '{text}'."); }
    private void White() { while (_position < source.Length && char.IsWhiteSpace(source[_position])) _position++; }
}
