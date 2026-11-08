using System.Globalization;
using System.Text;

namespace FieldManualVaultImporter.Vault;

/// <summary>
/// Reads the JavaScript object literals a SvelteKit page embeds in its own HTML for hydration ({id:"x",toc:[[1,"A",3]]}):
/// bare or quoted keys, strings, numbers, true/false/null, arrays and objects. Anything else (a variable reference,
/// a function call) reads as null. Objects come back as Dictionary&lt;string, object?&gt;, arrays as List&lt;object?&gt;.
/// </summary>
public sealed class JsLiteral
{
    private readonly string _s;
    private int _i;

    private JsLiteral(string s, int start) { _s = s; _i = start; }

    /// <summary>The value that starts at <paramref name="start"/> (an object, array or scalar).</summary>
    public static object? Parse(string text, int start) => new JsLiteral(text, start).Value();

    private object? Value()
    {
        Skip();
        if (_i >= _s.Length) throw new FormatException("Unexpected end of data.");
        var c = _s[_i];
        if (c == '{') return Object();
        if (c == '[') return Array();
        if (c is '"' or '\'' or '`') return String();
        if (c == '-' || c == '.' || char.IsDigit(c)) return Number();
        var word = Identifier();
        if (word.Length == 0) throw new FormatException($"Unexpected '{c}' at {_i}.");
        switch (word)
        {
            case "true": return true;
            case "false": return false;
            case "null": case "undefined": return null;
            case "void": Value(); return null;
        }
        // A reference or a call (a, new Date(…), f(…)): skipped.
        Skip();
        if (word == "new") { Identifier(); Skip(); }
        if (_i < _s.Length && _s[_i] == '(') SkipBalanced('(', ')');
        return null;
    }

    private Dictionary<string, object?> Object()
    {
        var o = new Dictionary<string, object?>(StringComparer.Ordinal);
        _i++;
        while (true)
        {
            Skip();
            if (_s[_i] == '}') { _i++; return o; }
            var key = _s[_i] is '"' or '\'' ? String() : Identifier();
            Skip();
            Expect(':');
            o[key] = Value();
            Skip();
            if (_s[_i] == ',') _i++;
        }
    }

    private List<object?> Array()
    {
        var a = new List<object?>();
        _i++;
        while (true)
        {
            Skip();
            if (_s[_i] == ']') { _i++; return a; }
            a.Add(Value());
            Skip();
            if (_s[_i] == ',') _i++;
        }
    }

    private string String()
    {
        var quote = _s[_i++];
        var sb = new StringBuilder();
        while (_s[_i] != quote)
        {
            var c = _s[_i++];
            if (c != '\\') { sb.Append(c); continue; }
            c = _s[_i++];
            switch (c)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'v': sb.Append('\v'); break;
                case '0': sb.Append('\0'); break;
                case 'x': sb.Append((char)int.Parse(_s.AsSpan(_i, 2), NumberStyles.HexNumber)); _i += 2; break;
                case 'u' when _s[_i] == '{':
                    var end = _s.IndexOf('}', _i);
                    sb.Append(char.ConvertFromUtf32(int.Parse(_s.AsSpan(_i + 1, end - _i - 1), NumberStyles.HexNumber)));
                    _i = end + 1;
                    break;
                case 'u': sb.Append((char)int.Parse(_s.AsSpan(_i, 4), NumberStyles.HexNumber)); _i += 4; break;
                case '\r': if (_s[_i] == '\n') _i++; break;   // line continuation
                case '\n': break;
                default: sb.Append(c); break;
            }
        }
        _i++;
        return sb.ToString();
    }

    private object Number()
    {
        var start = _i;
        while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] is '.' or '-' or '+')) _i++;
        var text = _s[start.._i];
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return l;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0d;
    }

    private string Identifier()
    {
        var start = _i;
        while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] is '_' or '$')) _i++;
        return _s[start.._i];
    }

    private void SkipBalanced(char open, char close)
    {
        var depth = 0;
        do
        {
            var c = _s[_i];
            if (c is '"' or '\'' or '`') { String(); continue; }
            if (c == open) depth++;
            else if (c == close) depth--;
            _i++;
        } while (depth > 0);
    }

    private void Expect(char c)
    {
        if (_s[_i] != c) throw new FormatException($"Expected '{c}' at {_i}.");
        _i++;
    }

    private void Skip()
    {
        while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
    }
}
