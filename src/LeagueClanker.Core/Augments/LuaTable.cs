using System.Globalization;
using System.Text;

namespace LeagueClanker.Core.Augments;

/// <summary>
/// Minimal parser for the Lua data modules the League wiki publishes (<c>return { ["key"] = { ... } }</c>).
/// Supports tables, strings (quoted and long-bracket), numbers, booleans and comments. Tables become
/// dictionaries; positional entries get their 1-based index as key.
/// </summary>
internal sealed class LuaTable
{
    private readonly string _s;
    private int _i;

    private LuaTable(string source) => _s = source;

    public static Dictionary<string, object?> ParseReturn(string source)
    {
        var parser = new LuaTable(source);
        parser.SkipTrivia();
        if (!parser.TryWord("return"))
            throw new FormatException("Expected 'return' at the start of the module.");
        parser.SkipTrivia();
        return parser.ParseValue() as Dictionary<string, object?> ?? throw new FormatException("Module does not return a table.");
    }

    private object? ParseValue()
    {
        SkipTrivia();
        var c = Peek();
        if (c == '{') return ParseTable();
        if (c is '"' or '\'') return ParseQuoted();
        if (c == '[' && LongBracketLevel() >= 0) return ParseLongString();
        if (char.IsDigit(c) || c == '-' || c == '.') return ParseNumber();
        if (TryWord("true")) return true;
        if (TryWord("false")) return false;
        if (TryWord("nil")) return null;
        throw Error($"Unexpected '{c}'");
    }

    private Dictionary<string, object?> ParseTable()
    {
        Expect('{');
        var table = new Dictionary<string, object?>(StringComparer.Ordinal);
        var index = 1;
        while (true)
        {
            SkipTrivia();
            if (Peek() == '}')
            {
                _i++;
                return table;
            }

            string key;
            if (Peek() == '[' && LongBracketLevel() < 0)
            {
                _i++;
                key = Convert.ToString(ParseValue(), CultureInfo.InvariantCulture) ?? "";
                SkipTrivia();
                Expect(']');
                SkipTrivia();
                Expect('=');
                table[key] = ParseValue();
            }
            else if (IsIdentifierStart(Peek()) && IdentifierFollowedByEquals(out var name))
            {
                table[name] = ParseValue();
            }
            else
            {
                table[(index++).ToString(CultureInfo.InvariantCulture)] = ParseValue();
            }

            SkipTrivia();
            if (Peek() is ',' or ';')
                _i++;
        }
    }

    private string ParseQuoted()
    {
        var quote = _s[_i++];
        var sb = new StringBuilder();
        while (_s[_i] != quote)
        {
            var c = _s[_i++];
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }

            var e = _s[_i++];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case '\n': sb.Append('\n'); break;
                default:
                    if (char.IsDigit(e))
                    {
                        var start = _i - 1;
                        while (_i - start < 3 && char.IsDigit(_s[_i])) _i++;
                        sb.Append((char)int.Parse(_s[start.._i], CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(e); // \" \' \\ and anything else literal
                    }
                    break;
            }
        }
        _i++;
        return sb.ToString();
    }

    private string ParseLongString()
    {
        var level = LongBracketLevel();
        _i += level + 2;
        var close = "]" + new string('=', level) + "]";
        var end = _s.IndexOf(close, _i, StringComparison.Ordinal);
        if (end < 0) throw Error("Unterminated long string");
        var value = _s[_i..end];
        _i = end + close.Length;
        // Lua drops the line break right after the opening bracket, whichever style it is.
        return value.StartsWith("\r\n", StringComparison.Ordinal) ? value[2..]
            : value.StartsWith('\n') || value.StartsWith('\r') ? value[1..]
            : value;
    }

    private double ParseNumber()
    {
        var start = _i;
        while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] is '.' or '-' or '+')) _i++;
        return double.Parse(_s[start.._i], NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>At '[': returns n for "[" + n×"=" + "[", or -1 when this is a table key bracket.</summary>
    private int LongBracketLevel()
    {
        var j = _i + 1;
        while (j < _s.Length && _s[j] == '=') j++;
        return j < _s.Length && _s[j] == '[' ? j - _i - 1 : -1;
    }

    private bool IdentifierFollowedByEquals(out string name)
    {
        var start = _i;
        var j = _i;
        while (j < _s.Length && (char.IsLetterOrDigit(_s[j]) || _s[j] == '_')) j++;
        var k = j;
        while (k < _s.Length && char.IsWhiteSpace(_s[k])) k++;
        if (k < _s.Length && _s[k] == '=' && (k + 1 >= _s.Length || _s[k + 1] != '='))
        {
            name = _s[start..j];
            _i = k + 1;
            return true;
        }
        name = "";
        return false;
    }

    private void SkipTrivia()
    {
        while (_i < _s.Length)
        {
            if (char.IsWhiteSpace(_s[_i]))
            {
                _i++;
            }
            else if (_s.AsSpan(_i).StartsWith("--"))
            {
                _i += 2;
                if (_i < _s.Length && _s[_i] == '[' && LongBracketLevel() >= 0)
                {
                    ParseLongString();
                }
                else
                {
                    while (_i < _s.Length && _s[_i] != '\n') _i++;
                }
            }
            else
            {
                return;
            }
        }
    }

    private bool TryWord(string word)
    {
        if (!_s.AsSpan(_i).StartsWith(word) || (_i + word.Length < _s.Length && char.IsLetterOrDigit(_s[_i + word.Length])))
            return false;
        _i += word.Length;
        return true;
    }

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    private char Peek() => _i < _s.Length ? _s[_i] : throw Error("Unexpected end of input");

    private void Expect(char c)
    {
        if (Peek() != c) throw Error($"Expected '{c}'");
        _i++;
    }

    private FormatException Error(string message) => new($"{message} at position {_i}.");
}
