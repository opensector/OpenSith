using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace OpenSith.Core.Cog;

public enum CogTokenType
{
    EndOfFile,
    Identifier,
    IntLiteral,
    FloatLiteral,
    StringLiteral,
    VectorLiteral,

    // Keywords
    Break,
    Continue,
    Do,
    Else,
    For,
    Goto,
    If,
    Return,
    While,
    End,
    Call,

    // Operators and delimiters
    AndAnd,         // &&
    OrOr,           // ||
    LessEqual,      // <=
    GreaterEqual,   // >=
    EqualEqual,     // ==
    BangEqual,      // !=
    Less,           // <
    Greater,        // >
    Plus,           // +
    Minus,          // -
    Star,           // *
    Slash,          // /
    Percent,        // %
    Ampersand,      // &
    Pipe,           // |
    Caret,          // ^
    Bang,           // !
    Tilde,          // ~
    Equal,          // =
    Semicolon,      // ;
    Colon,          // :
    Comma,          // ,
    Dot,            // .
    OpenParen,      // (
    CloseParen,     // )
    OpenBracket,    // [
    CloseBracket,   // ]
    OpenBrace,      // {
    CloseBrace       // }
}

public readonly struct CogToken
{
    public CogTokenType Type { get; }
    public string Text { get; }
    public int IntValue { get; }
    public float FloatValue { get; }
    public Vector3 VectorValue { get; }
    public int Line { get; }
    public int Column { get; }

    public CogToken(CogTokenType type, string text, int line, int column, int intVal = 0, float floatVal = 0f, Vector3 vecVal = default)
    {
        Type = type;
        Text = text;
        Line = line;
        Column = column;
        IntValue = intVal;
        FloatValue = floatVal;
        VectorValue = vecVal;
    }

    public override string ToString() => $"{Type} '{Text}' ({Line}:{Column})";
}

/// <summary>
/// Lexer for LucasArts Jedi Knight COG scripts.
/// </summary>
public sealed class CogLexer
{
    private readonly string _source;
    private int _index;
    private int _line = 1;
    private int _column = 1;

    public CogLexer(string source)
    {
        _source = source ?? string.Empty;
    }

    public List<CogToken> TokenizeAll()
    {
        var tokens = new List<CogToken>();
        while (true)
        {
            var token = NextToken();
            tokens.Add(token);
            if (token.Type == CogTokenType.EndOfFile || token.Type == CogTokenType.End)
                break;
        }
        return tokens;
    }

    public CogToken NextToken()
    {
        while (_index < _source.Length)
        {
            char c = _source[_index];

            // Whitespace
            if (c == '\r' || c == '\n')
            {
                Advance();
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                Advance();
                continue;
            }

            // Line comments: # or //
            if (c == '#')
            {
                SkipLine();
                continue;
            }
            if (c == '/' && Peek(1) == '/')
            {
                SkipLine();
                continue;
            }

            // Block comments /* ... */
            if (c == '/' && Peek(1) == '*')
            {
                Advance();
                Advance();
                while (_index < _source.Length)
                {
                    if (_source[_index] == '*' && Peek(1) == '/')
                    {
                        Advance();
                        Advance();
                        break;
                    }
                    Advance();
                }
                continue;
            }

            int startLine = _line;
            int startCol = _column;

            // Dot: could be float like .5 or dot operator
            if (c == '.')
            {
                if (char.IsAsciiDigit(Peek(1)))
                {
                    return ScanFloatOrInt(startLine, startCol);
                }
                Advance();
                return new CogToken(CogTokenType.Dot, ".", startLine, startCol);
            }

            // Numbers
            if (char.IsAsciiDigit(c))
            {
                return ScanFloatOrInt(startLine, startCol);
            }

            // String literal "..."
            if (c == '"')
            {
                return ScanString(startLine, startCol);
            }

            // Vector literal '...'
            if (c == '\'')
            {
                return ScanVector(startLine, startCol);
            }

            // Identifiers or keywords
            if (char.IsAsciiLetter(c) || c == '_')
            {
                return ScanIdentifierOrKeyword(startLine, startCol);
            }

            // Operators & Delimiters
            switch (c)
            {
                case '&':
                    Advance();
                    if (Current == '&')
                    {
                        Advance();
                        return new CogToken(CogTokenType.AndAnd, "&&", startLine, startCol);
                    }
                    return new CogToken(CogTokenType.Ampersand, "&", startLine, startCol);

                case '|':
                    Advance();
                    if (Current == '|')
                    {
                        Advance();
                        return new CogToken(CogTokenType.OrOr, "||", startLine, startCol);
                    }
                    return new CogToken(CogTokenType.Pipe, "|", startLine, startCol);

                case '<':
                    Advance();
                    if (Current == '=')
                    {
                        Advance();
                        return new CogToken(CogTokenType.LessEqual, "<=", startLine, startCol);
                    }
                    return new CogToken(CogTokenType.Less, "<", startLine, startCol);

                case '>':
                    Advance();
                    if (Current == '=')
                    {
                        Advance();
                        return new CogToken(CogTokenType.GreaterEqual, ">=", startLine, startCol);
                    }
                    return new CogToken(CogTokenType.Greater, ">", startLine, startCol);

                case '=':
                    Advance();
                    if (Current == '=')
                    {
                        Advance();
                        return new CogToken(CogTokenType.EqualEqual, "==", startLine, startCol);
                    }
                    return new CogToken(CogTokenType.Equal, "=", startLine, startCol);

                case '!':
                    Advance();
                    if (Current == '=')
                    {
                        Advance();
                        return new CogToken(CogTokenType.BangEqual, "!=", startLine, startCol);
                    }
                    return new CogToken(CogTokenType.Bang, "!", startLine, startCol);

                case '+':
                    Advance();
                    return new CogToken(CogTokenType.Plus, "+", startLine, startCol);

                case '-':
                    Advance();
                    return new CogToken(CogTokenType.Minus, "-", startLine, startCol);

                case '*':
                    Advance();
                    return new CogToken(CogTokenType.Star, "*", startLine, startCol);

                case '/':
                    Advance();
                    return new CogToken(CogTokenType.Slash, "/", startLine, startCol);

                case '%':
                    Advance();
                    return new CogToken(CogTokenType.Percent, "%", startLine, startCol);

                case '^':
                    Advance();
                    return new CogToken(CogTokenType.Caret, "^", startLine, startCol);

                case '~':
                    Advance();
                    return new CogToken(CogTokenType.Tilde, "~", startLine, startCol);

                case ';':
                    Advance();
                    return new CogToken(CogTokenType.Semicolon, ";", startLine, startCol);

                case ':':
                    Advance();
                    return new CogToken(CogTokenType.Colon, ":", startLine, startCol);

                case ',':
                    Advance();
                    return new CogToken(CogTokenType.Comma, ",", startLine, startCol);

                case '(':
                    Advance();
                    return new CogToken(CogTokenType.OpenParen, "(", startLine, startCol);

                case ')':
                    Advance();
                    return new CogToken(CogTokenType.CloseParen, ")", startLine, startCol);

                case '[':
                    Advance();
                    return new CogToken(CogTokenType.OpenBracket, "[", startLine, startCol);

                case ']':
                    Advance();
                    return new CogToken(CogTokenType.CloseBracket, "]", startLine, startCol);

                case '{':
                    Advance();
                    return new CogToken(CogTokenType.OpenBrace, "{", startLine, startCol);

                case '}':
                    Advance();
                    return new CogToken(CogTokenType.CloseBrace, "}", startLine, startCol);

                default:
                    // Ignore unrecognized/bad characters as flex does
                    Advance();
                    continue;
            }
        }

        return new CogToken(CogTokenType.EndOfFile, string.Empty, _line, _column);
    }

    private CogToken ScanFloatOrInt(int startLine, int startCol)
    {
        int start = _index;

        // Check for hex 0x...
        if (_source[_index] == '0' && (Peek(1) == 'x' || Peek(1) == 'X'))
        {
            Advance(); // 0
            Advance(); // x
            while (_index < _source.Length && char.IsAsciiHexDigit(_source[_index]))
                Advance();

            string hexText = _source[start.._index];
            int hexVal = Convert.ToInt32(hexText, 16);
            return new CogToken(CogTokenType.IntLiteral, hexText, startLine, startCol, intVal: hexVal);
        }

        bool hasDot = false;
        bool hasExponent = false;

        while (_index < _source.Length)
        {
            char ch = _source[_index];
            if (ch == '.')
            {
                if (hasDot || hasExponent) break;
                hasDot = true;
                Advance();
            }
            else if (ch == 'e' || ch == 'E')
            {
                if (hasExponent) break;
                hasExponent = true;
                Advance();
                if (_index < _source.Length && (_source[_index] == '+' || _source[_index] == '-'))
                    Advance();
            }
            else if (char.IsAsciiDigit(ch))
            {
                Advance();
            }
            else
            {
                break;
            }
        }

        string numText = _source[start.._index];

        if (hasDot || hasExponent)
        {
            float fVal = float.Parse(numText, NumberStyles.Float, CultureInfo.InvariantCulture);
            return new CogToken(CogTokenType.FloatLiteral, numText, startLine, startCol, floatVal: fVal);
        }

        // Octal check: leading '0' with more digits
        if (numText.Length > 1 && numText[0] == '0')
        {
            // If all digits are 0-7, parse as octal
            bool isOctal = true;
            foreach (char c in numText)
            {
                if (c < '0' || c > '7') { isOctal = false; break; }
            }
            int val = isOctal
                ? Convert.ToInt32(numText, 8)
                : int.Parse(numText, CultureInfo.InvariantCulture);
            return new CogToken(CogTokenType.IntLiteral, numText, startLine, startCol, intVal: val);
        }

        int iVal = int.Parse(numText, CultureInfo.InvariantCulture);
        return new CogToken(CogTokenType.IntLiteral, numText, startLine, startCol, intVal: iVal);
    }

    private CogToken ScanString(int startLine, int startCol)
    {
        Advance(); // skip opening quote
        var sb = new StringBuilder();

        while (_index < _source.Length)
        {
            char c = _source[_index];
            if (c == '"')
            {
                Advance();
                break;
            }
            if (c == '\\' && _index + 1 < _source.Length)
            {
                Advance();
                char esc = _source[_index];
                switch (esc)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    default: sb.Append(esc); break;
                }
                Advance();
            }
            else
            {
                sb.Append(c);
                Advance();
            }
        }

        return new CogToken(CogTokenType.StringLiteral, sb.ToString(), startLine, startCol);
    }

    private CogToken ScanVector(int startLine, int startCol)
    {
        Advance(); // skip opening '
        int start = _index;

        while (_index < _source.Length && _source[_index] != '\'')
        {
            Advance();
        }

        string content = _source[start.._index];
        if (_index < _source.Length && _source[_index] == '\'')
            Advance(); // skip closing '

        // Parse vector components separated by spaces, slashes, or commas
        var parts = content.Split(new[] { ' ', '/', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        float x = parts.Length > 0 && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float px) ? px : 0f;
        float y = parts.Length > 1 && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float py) ? py : 0f;
        float z = parts.Length > 2 && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float pz) ? pz : 0f;

        var vec = new Vector3(x, y, z);
        return new CogToken(CogTokenType.VectorLiteral, content, startLine, startCol, vecVal: vec);
    }

    private CogToken ScanIdentifierOrKeyword(int startLine, int startCol)
    {
        int start = _index;
        while (_index < _source.Length && (char.IsAsciiLetterOrDigit(_source[_index]) || _source[_index] == '_'))
        {
            Advance();
        }

        string text = _source[start.._index];

        // Case-insensitive keywords
        if (text.Equals("break", StringComparison.OrdinalIgnoreCase))
            return new CogToken(CogTokenType.Break, text, startLine, startCol);
        if (text.Equals("continue", StringComparison.OrdinalIgnoreCase))
            return new CogToken(CogTokenType.Continue, text, startLine, startCol);
        if (text.Equals("do", StringComparison.OrdinalIgnoreCase))
            return new CogToken(CogTokenType.Do, text, startLine, startCol);
        if (text.Equals("else", StringComparison.OrdinalIgnoreCase))
            return new CogToken(CogTokenType.Else, text, startLine, startCol);
        if (text.Equals("for", StringComparison.OrdinalIgnoreCase))
            return new CogToken(CogTokenType.For, text, startLine, startCol);
        if (text.Equals("goto", StringComparison.OrdinalIgnoreCase))
            return new CogToken(CogTokenType.Goto, text, startLine, startCol);
        if (text.Equals("if", StringComparison.OrdinalIgnoreCase))
            return new CogToken(CogTokenType.If, text, startLine, startCol);
        if (text.Equals("return", StringComparison.OrdinalIgnoreCase) || text.Equals("stop", StringComparison.OrdinalIgnoreCase))
            return new CogToken(CogTokenType.Return, text, startLine, startCol);
        if (text.Equals("while", StringComparison.OrdinalIgnoreCase))
            return new CogToken(CogTokenType.While, text, startLine, startCol);
        if (text.Equals("end", StringComparison.OrdinalIgnoreCase))
            return new CogToken(CogTokenType.End, text, startLine, startCol);
        if (text.Equals("call", StringComparison.OrdinalIgnoreCase))
            return new CogToken(CogTokenType.Call, text, startLine, startCol);

        return new CogToken(CogTokenType.Identifier, text, startLine, startCol);
    }

    private char Current => _index < _source.Length ? _source[_index] : '\0';

    private char Peek(int offset)
    {
        int i = _index + offset;
        return i < _source.Length ? _source[i] : '\0';
    }

    private void Advance()
    {
        if (_index < _source.Length)
        {
            if (_source[_index] == '\n')
            {
                _line++;
                _column = 1;
            }
            else
            {
                _column++;
            }
            _index++;
        }
    }

    private void SkipLine()
    {
        while (_index < _source.Length && _source[_index] != '\r' && _source[_index] != '\n')
        {
            Advance();
        }
    }
}
