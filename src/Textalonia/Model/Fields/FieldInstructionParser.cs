using System.Collections.Immutable;
using System.Text;

namespace Textalonia.Model.Fields;

public abstract record FieldExpression;
public sealed record FieldLiteral(string Value) : FieldExpression;
public sealed record FieldNested(FieldInstruction Instruction) : FieldExpression;
public sealed record FieldArgument(ImmutableArray<FieldExpression> Parts, bool IsQuoted = false)
{
    public string? Literal => Parts.All(p => p is FieldLiteral) ? string.Concat(Parts.Cast<FieldLiteral>().Select(p => p.Value)) : null;
}
public sealed record FieldSwitch(string Name, FieldArgument? Argument);
public sealed record FieldInstruction(string Source, string Code, ImmutableArray<FieldArgument> Arguments,
    ImmutableArray<FieldSwitch> Switches)
{
    public FieldSwitch? Switch(string name) => Switches.LastOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Bounded, non-executing parser for quoted arguments, switches and brace-delimited nested fields.</summary>
public static class FieldInstructionParser
{
    public const int MaximumInstructionLength = 16_384;
    public static FieldInstruction Parse(string source, int maximumDepth = 32)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (maximumDepth is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(maximumDepth));
        if (source.Length > MaximumInstructionLength || source.Contains('\0')) throw new FormatException("Field instruction exceeds the input limit.");
        var reader = new Reader(source, maximumDepth);
        return reader.Read();
    }

    /// <summary>Remaps reference operands on paste. Unchanged instructions keep their exact source spelling.</summary>
    public static string RewriteBookmarkReferences(string instruction, IReadOnlyDictionary<string, string> names)
    {
        var parsed = Parse(instruction); var changed = false;
        FieldArgument RewriteArgument(FieldArgument argument) => argument with
        { Parts = argument.Parts.Select(part => part is FieldNested nested ? new FieldNested(Rewrite(nested.Instruction)) : part).ToImmutableArray() };
        FieldInstruction Rewrite(FieldInstruction field)
        {
            var arguments = field.Arguments.Select(RewriteArgument).ToImmutableArray();
            var switches = field.Switches.Select(s => s with { Argument = s.Argument is { } a ? RewriteArgument(a) : null }).ToImmutableArray();
            if (field.Code is "REF" or "PAGEREF" && arguments.Length > 0 && arguments[0].Literal is { } name && names.TryGetValue(name, out var replacement))
            { arguments = arguments.SetItem(0, new([new FieldLiteral(replacement)], arguments[0].IsQuoted)); changed |= name != replacement; }
            if (field.Code == "=")
                arguments = arguments.Select(a => a with { Parts = a.Parts.Select(part => part is FieldLiteral literal ?
                    new FieldLiteral(System.Text.RegularExpressions.Regex.Replace(literal.Value, @"(?<![\p{L}\p{N}_.])[\p{L}_][\p{L}\p{N}_]*", match =>
                    {
                        if (literal.Value[(match.Index + match.Length)..].TrimStart().StartsWith('(') || !names.TryGetValue(match.Value, out var replacement)) return match.Value;
                        changed |= match.Value != replacement; return replacement;
                    }, System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) : part).ToImmutableArray() }).ToImmutableArray();
            if (field.Code == "HYPERLINK")
                switches = switches.Select(s => s.Name.Equals("l", StringComparison.OrdinalIgnoreCase) && s.Argument?.Literal is { } name && names.TryGetValue(name, out var replacement) ?
                    RenameSwitch(s, name, replacement) : s).ToImmutableArray();
            return field with { Arguments = arguments, Switches = switches };
        }
        FieldSwitch RenameSwitch(FieldSwitch fieldSwitch, string old, string replacement)
        { changed |= old != replacement; return fieldSwitch with { Argument = new([new FieldLiteral(replacement)], true) }; }
        var rewritten = Rewrite(parsed);
        return changed ? Write(rewritten) : instruction;
    }
    public static string Write(FieldInstruction instruction)
    {
        static string Argument(FieldArgument argument)
        {
            var text = string.Concat(argument.Parts.Select(part => part switch
            {
                FieldLiteral literal => argument.IsQuoted ? literal.Value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("{", "\\{").Replace("}", "\\}") : literal.Value,
                FieldNested nested => "{ " + Write(nested.Instruction) + " }", _ => ""
            }));
            return argument.IsQuoted ? "\"" + text + "\"" : text;
        }
        return instruction.Code + string.Concat(instruction.Arguments.Select(a => " " + Argument(a))) +
            string.Concat(instruction.Switches.Select(s => " \\" + s.Name + (s.Argument is { } a ? " " + Argument(a) : "")));
    }

    private sealed class Reader(string source, int maximumDepth)
    {
        private int _position;
        public FieldInstruction Read()
        {
            White();
            var wrapped = Peek('{');
            if (wrapped) _position++;
            var result = Instruction(0, wrapped);
            White();
            if (_position != source.Length) throw new FormatException("Unexpected text after field instruction.");
            return result;
        }
        private FieldInstruction Instruction(int depth, bool wrapped)
        {
            if (depth >= maximumDepth) throw new FormatException("Field nesting exceeds the configured limit.");
            White(); var begin = _position;
            while (_position < source.Length && !char.IsWhiteSpace(source[_position]) && source[_position] is not ('{' or '}' or '"'))
            {
                if (source[_position++] == '=' && _position == begin + 1) break;
            }
            if (begin == _position) throw new FormatException("A field code is required.");
            var code = source[begin.._position].ToUpperInvariant();
            var arguments = ImmutableArray.CreateBuilder<FieldArgument>();
            var switches = ImmutableArray.CreateBuilder<FieldSwitch>();
            while (true)
            {
                White();
                if (_position >= source.Length || Peek('}')) break;
                if (Peek('\\'))
                {
                    _position++; var start = _position;
                    while (_position < source.Length && !char.IsWhiteSpace(source[_position]) && source[_position] != '}') _position++;
                    if (start == _position) throw new FormatException("A switch name is required.");
                    var name = source[start.._position]; White();
                    var flag = (code, name.ToLowerInvariant()) switch
                    {
                        ("TOC", "h" or "u" or "w" or "x" or "z") => true,
                        ("SEQ", "c" or "n" or "h") => true,
                        ("REF", "h" or "n" or "p" or "r" or "w") => true,
                        ("STYLEREF", "l" or "n" or "p" or "r" or "t" or "w") => true,
                        ("INCLUDEPICTURE", "d") => true,
                        _ => false
                    };
                    switches.Add(new(name, !flag && _position < source.Length && !Peek('\\') && !Peek('}') ? Argument(depth) : null));
                }
                else arguments.Add(Argument(depth));
            }
            var end = _position;
            if (wrapped)
            {
                if (!Peek('}')) throw new FormatException("Unclosed nested field.");
                _position++;
            }
            else if (Peek('}')) throw new FormatException("Unexpected closing field boundary.");
            return new(source[begin..end], code, arguments.ToImmutable(), switches.ToImmutable());
        }
        private FieldArgument Argument(int depth)
        {
            var quoted = Peek('"'); if (quoted) _position++;
            var parts = ImmutableArray.CreateBuilder<FieldExpression>(); var literal = new StringBuilder();
            void Flush() { if (literal.Length > 0) { parts.Add(new FieldLiteral(literal.ToString())); literal.Clear(); } }
            var closed = !quoted;
            while (_position < source.Length)
            {
                var ch = source[_position];
                if (quoted && ch == '"') { _position++; closed = true; break; }
                if (!quoted && (char.IsWhiteSpace(ch) || ch == '}')) break;
                if (ch == '{') { Flush(); _position++; parts.Add(new FieldNested(Instruction(depth + 1, true))); continue; }
                if (ch == '\\' && quoted && _position + 1 < source.Length && source[_position + 1] is '\\' or '"' or '{' or '}')
                { literal.Append(source[_position + 1]); _position += 2; continue; }
                if (!quoted && ch == '"') throw new FormatException("A quote must begin an argument.");
                literal.Append(ch); _position++;
            }
            if (!closed) throw new FormatException("Unclosed field argument quote.");
            Flush();
            return new(parts.ToImmutable(), quoted);
        }
        private bool Peek(char ch) => _position < source.Length && source[_position] == ch;
        private void White() { while (_position < source.Length && char.IsWhiteSpace(source[_position])) _position++; }
    }
}
