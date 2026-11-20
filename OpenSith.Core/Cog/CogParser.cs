using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace OpenSith.Core.Cog;

/// <summary>
/// Parser for Jedi Knight COG scripts (.cog).
/// Parses symbols section and code section into a CogScript AST.
/// </summary>
public sealed class CogParser
{
    private readonly string _text;
    private readonly string _name;
    private readonly CogScript _script;

    private CogParser(string text, string name)
    {
        _text = text ?? string.Empty;
        _name = name ?? string.Empty;
        _script = new CogScript { Name = _name };
    }

    /// <summary>
    /// Parses a COG script source text into a CogScript object.
    /// </summary>
    public static CogScript Parse(string scriptText, string scriptName = "")
    {
        var parser = new CogParser(scriptText, scriptName);
        return parser.ParseInternal();
    }

    private CogScript ParseInternal()
    {
        using var reader = new StringReader(_text);
        string? line;
        bool inSymbols = false;
        bool inCode = false;
        var codeBuilder = new StringBuilder();

        while ((line = reader.ReadLine()) != null)
        {
            string trimmed = line.Trim();

            // Strip line comments
            int cIdx = IndexOfComment(trimmed);
            if (cIdx >= 0)
                trimmed = trimmed.Substring(0, cIdx).Trim();

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                if (inCode) codeBuilder.AppendLine();
                continue;
            }

            // Flags header before symbols
            if (!inSymbols && !inCode)
            {
                if (trimmed.StartsWith("flags", StringComparison.OrdinalIgnoreCase))
                {
                    ParseFlags(trimmed);
                    continue;
                }
                if (trimmed.Equals("symbols", StringComparison.OrdinalIgnoreCase))
                {
                    inSymbols = true;
                    continue;
                }
            }

            if (inSymbols)
            {
                if (trimmed.Equals("end", StringComparison.OrdinalIgnoreCase))
                {
                    inSymbols = false;
                    continue;
                }

                ParseSymbolLine(trimmed);
                continue;
            }

            if (!inCode && trimmed.Equals("code", StringComparison.OrdinalIgnoreCase))
            {
                inCode = true;
                continue;
            }

            if (inCode)
            {
                codeBuilder.AppendLine(line);
            }
        }

        // Parse code section AST
        ParseCodeSection(codeBuilder.ToString());

        return _script;
    }

    private static int IndexOfComment(string line)
    {
        bool inQuote = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
                inQuote = !inQuote;
            else if (!inQuote)
            {
                if (line[i] == '#') return i;
                if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/') return i;
            }
        }
        return -1;
    }

    private void ParseFlags(string line)
    {
        // e.g. flags=0x240 or flags 0x240
        int eq = line.IndexOf('=');
        string valStr = eq >= 0 ? line.Substring(eq + 1).Trim() : line.Substring(5).Trim();
        if (valStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            _script.Flags = Convert.ToInt32(valStr, 16);
        else if (int.TryParse(valStr, out int fl))
            _script.Flags = fl;
    }

    private void ParseSymbolLine(string line)
    {
        var tokens = TokenizeSymbolLine(line);
        if (tokens.Count == 0) return;

        int idx = 0;
        // Skip optional line number prefix e.g. "0:" or "12:"
        if (tokens[idx].EndsWith(':') && int.TryParse(tokens[idx].TrimEnd(':'), out _))
        {
            idx++;
            if (idx >= tokens.Count) return;
        }

        string typeStr = tokens[idx++];
        CogSymbolType type;
        if (typeStr.Equals("ai", StringComparison.OrdinalIgnoreCase)) type = CogSymbolType.Ai;
        else if (typeStr.Equals("cog", StringComparison.OrdinalIgnoreCase)) type = CogSymbolType.Cog;
        else if (typeStr.Equals("flex", StringComparison.OrdinalIgnoreCase)) type = CogSymbolType.Flex;
        else if (typeStr.Equals("float", StringComparison.OrdinalIgnoreCase)) type = CogSymbolType.Float;
        else if (typeStr.Equals("int", StringComparison.OrdinalIgnoreCase)) type = CogSymbolType.Int;
        else if (typeStr.Equals("keyframe", StringComparison.OrdinalIgnoreCase)) type = CogSymbolType.Keyframe;
        else if (typeStr.Equals("material", StringComparison.OrdinalIgnoreCase)) type = CogSymbolType.Material;
        else if (typeStr.Equals("model", StringComparison.OrdinalIgnoreCase)) type = CogSymbolType.Model;
        else if (typeStr.Equals("sector", StringComparison.OrdinalIgnoreCase)) type = CogSymbolType.Sector;
        else if (typeStr.Equals("sound", StringComparison.OrdinalIgnoreCase)) type = CogSymbolType.Sound;
        else if (typeStr.Equals("surface", StringComparison.OrdinalIgnoreCase)) type = CogSymbolType.Surface;
        else if (typeStr.Equals("template", StringComparison.OrdinalIgnoreCase)) type = CogSymbolType.Template;
        else if (typeStr.Equals("thing", StringComparison.OrdinalIgnoreCase)) type = CogSymbolType.Thing;
        else if (typeStr.Equals("vector", StringComparison.OrdinalIgnoreCase)) type = CogSymbolType.Vector;
        else if (typeStr.Equals("message", StringComparison.OrdinalIgnoreCase)) type = CogSymbolType.Message;
        else return; // Ignore unknown symbol line

        if (idx >= tokens.Count) return;

        var sym = new CogSymbol { Type = type };
        string nameToken = tokens[idx++];
        int eqIdx = nameToken.IndexOf('=');
        if (eqIdx >= 0)
        {
            sym.Name = nameToken.Substring(0, eqIdx);
            sym.DefaultValue = nameToken.Substring(eqIdx + 1);
        }
        else
        {
            sym.Name = nameToken;
        }

        while (idx < tokens.Count)
        {
            string tok = tokens[idx++];
            if (tok.Equals("local", StringComparison.OrdinalIgnoreCase))
            {
                sym.IsLocal = true;
            }
            else if (tok.Equals("nolink", StringComparison.OrdinalIgnoreCase))
            {
                sym.LinkId = -1;
            }
            else if (tok.StartsWith("desc=", StringComparison.OrdinalIgnoreCase))
            {
                sym.Desc = tok.Substring(5).Trim('"');
            }
            else if (tok.StartsWith("mask=", StringComparison.OrdinalIgnoreCase))
            {
                string mStr = tok.Substring(5);
                if (mStr == "-1")
                {
                    sym.Mask = -1;
                }
                else
                {
                    ReadOnlySpan<char> hexSpan = mStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? mStr.AsSpan(2)
                        : mStr.AsSpan();
                    if (int.TryParse(hexSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int m))
                        sym.Mask = m;
                }
            }
            else if (tok.StartsWith("linkid=", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(tok.Substring(7), out int lid))
                    sym.LinkId = lid;
            }
            else if (sym.DefaultValue == null)
            {
                sym.DefaultValue = tok;
            }
        }

        _script.Symbols.Add(sym);
    }

    private static List<string> TokenizeSymbolLine(string line)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < line.Length)
        {
            // Skip whitespace and commas outside quotes
            while (i < line.Length && (char.IsWhiteSpace(line[i]) || line[i] == ','))
                i++;
            if (i >= line.Length) break;

            int start = i;
            bool inQuote = false;
            while (i < line.Length)
            {
                if (line[i] == '"')
                {
                    inQuote = !inQuote;
                }
                else if (!inQuote && (char.IsWhiteSpace(line[i]) || line[i] == ','))
                {
                    break;
                }
                i++;
            }
            tokens.Add(line[start..i]);
        }
        return tokens;
    }

    private void ParseCodeSection(string codeText)
    {
        var lexer = new CogLexer(codeText);
        var tokens = lexer.TokenizeAll();
        var codeParser = new StatementParser(tokens, _script);
        codeParser.ParseCode();
    }

    private sealed class StatementParser
    {
        private readonly List<CogToken> _tokens;
        private readonly CogScript _script;
        private int _pos;

        public StatementParser(List<CogToken> tokens, CogScript script)
        {
            _tokens = tokens;
            _script = script;
        }

        private CogToken Current => _pos < _tokens.Count ? _tokens[_pos] : new CogToken(CogTokenType.EndOfFile, "", 0, 0);

        private CogToken Peek(int offset)
        {
            int i = _pos + offset;
            return i < _tokens.Count ? _tokens[i] : new CogToken(CogTokenType.EndOfFile, "", 0, 0);
        }

        private bool Check(CogTokenType type) => Current.Type == type;

        private bool Match(CogTokenType type)
        {
            if (Check(type))
            {
                _pos++;
                return true;
            }
            return false;
        }

        private CogToken Consume(CogTokenType type, string message = "")
        {
            if (Check(type))
            {
                var tok = Current;
                _pos++;
                return tok;
            }
            throw new FormatException($"Parser error at line {Current.Line}:{Current.Column}. Expected {type}, got {Current.Type} ('{Current.Text}'). {message}");
        }

        public void ParseCode()
        {
            while (!Check(CogTokenType.EndOfFile) && !Check(CogTokenType.End))
            {
                var stmt = ParseStatement();
                if (stmt != null)
                {
                    _script.Statements.Add(stmt);
                }
            }
        }

        private CogStmt? ParseStatement()
        {
            if (Check(CogTokenType.EndOfFile) || Check(CogTokenType.End))
                return null;

            // Empty statement ;
            if (Match(CogTokenType.Semicolon))
                return null;

            // Labeled statement: identifier ':'
            if (Check(CogTokenType.Identifier) && Peek(1).Type == CogTokenType.Colon)
            {
                string labelName = Current.Text;
                _pos += 2; // consume identifier and ':'
                var labelStmt = new Label(labelName);
                _script.Labels[labelName] = _script.Statements.Count;
                return labelStmt;
            }

            // Compound statement: { ... }
            if (Match(CogTokenType.OpenBrace))
            {
                var stmts = new List<CogStmt>();
                while (!Check(CogTokenType.CloseBrace) && !Check(CogTokenType.EndOfFile) && !Check(CogTokenType.End))
                {
                    var s = ParseStatement();
                    if (s != null)
                        stmts.Add(s);
                }
                Consume(CogTokenType.CloseBrace, "Expected '}'");
                return new Block(stmts);
            }

            // Selection statement: if (condition) statement [else statement]
            if (Match(CogTokenType.If))
            {
                Consume(CogTokenType.OpenParen, "Expected '(' after 'if'");
                var cond = ParseExpression();
                Consume(CogTokenType.CloseParen, "Expected ')' after if condition");
                var thenBranch = ParseStatement() ?? new Block(new List<CogStmt>());
                CogStmt? elseBranch = null;
                if (Match(CogTokenType.Else))
                {
                    elseBranch = ParseStatement();
                }
                return new @If(cond, thenBranch, elseBranch);
            }

            // Iteration: while (condition) statement
            if (Match(CogTokenType.While))
            {
                Consume(CogTokenType.OpenParen, "Expected '(' after 'while'");
                var cond = ParseExpression();
                Consume(CogTokenType.CloseParen, "Expected ')' after while condition");
                var body = ParseStatement() ?? new Block(new List<CogStmt>());
                return new @While(cond, body);
            }

            // Iteration: do statement while (condition);
            if (Match(CogTokenType.Do))
            {
                var body = ParseStatement() ?? new Block(new List<CogStmt>());
                Consume(CogTokenType.While, "Expected 'while' after do body");
                Consume(CogTokenType.OpenParen, "Expected '(' after 'while'");
                var cond = ParseExpression();
                Consume(CogTokenType.CloseParen, "Expected ')' after while condition");
                Match(CogTokenType.Semicolon);
                return new DoWhile(body, cond);
            }

            // Iteration: for (init; cond; step) statement
            if (Match(CogTokenType.For))
            {
                Consume(CogTokenType.OpenParen, "Expected '(' after 'for'");
                CogStmt? init = null;
                if (!Match(CogTokenType.Semicolon))
                {
                    init = new ExprStmt(ParseExpression());
                    Consume(CogTokenType.Semicolon, "Expected ';' after for init");
                }
                CogExpr? cond = null;
                if (!Match(CogTokenType.Semicolon))
                {
                    cond = ParseExpression();
                    Consume(CogTokenType.Semicolon, "Expected ';' after for condition");
                }
                CogExpr? step = null;
                if (!Match(CogTokenType.CloseParen))
                {
                    step = ParseExpression();
                    Consume(CogTokenType.CloseParen, "Expected ')' after for step");
                }
                var body = ParseStatement() ?? new Block(new List<CogStmt>());
                return new @For(init, cond, step, body);
            }

            // Jump: goto identifier;
            if (Match(CogTokenType.Goto))
            {
                string target = Consume(CogTokenType.Identifier, "Expected identifier after 'goto'").Text;
                Consume(CogTokenType.Semicolon, "Expected ';' after goto target");
                return new @Goto(target);
            }

            // Jump: call identifier;
            if (Match(CogTokenType.Call))
            {
                string target = Consume(CogTokenType.Identifier, "Expected identifier after 'call'").Text;
                Consume(CogTokenType.Semicolon, "Expected ';' after call target");
                return new CallLabel(target);
            }

            // Jump: return [expr]; or stop;
            if (Match(CogTokenType.Return))
            {
                CogExpr? retVal = null;
                if (!Check(CogTokenType.Semicolon) && !Check(CogTokenType.CloseBrace) && !Check(CogTokenType.EndOfFile) && !Check(CogTokenType.End))
                {
                    retVal = ParseExpression();
                }
                Match(CogTokenType.Semicolon);
                return new @Return(retVal);
            }

            // Jump: break;
            if (Match(CogTokenType.Break))
            {
                Consume(CogTokenType.Semicolon, "Expected ';' after 'break'");
                return new @Break();
            }

            // Jump: continue;
            if (Match(CogTokenType.Continue))
            {
                Consume(CogTokenType.Semicolon, "Expected ';' after 'continue'");
                return new @Continue();
            }

            // Expression statement
            var expr = ParseExpression();
            Match(CogTokenType.Semicolon);
            return new ExprStmt(expr);
        }

        private CogExpr ParseExpression()
        {
            return ParseAssignment();
        }

        private CogExpr ParseAssignment()
        {
            var expr = ParseLogicalOr();
            if (Match(CogTokenType.Equal))
            {
                var right = ParseAssignment();
                return new AssignExpr(expr, right);
            }
            return expr;
        }

        private CogExpr ParseLogicalOr()
        {
            var expr = ParseLogicalAnd();
            while (Match(CogTokenType.OrOr))
            {
                var right = ParseLogicalAnd();
                expr = new BinaryExpr(CogBinaryOp.LogicalOr, expr, right);
            }
            return expr;
        }

        private CogExpr ParseLogicalAnd()
        {
            var expr = ParseBitwiseOr();
            while (Match(CogTokenType.AndAnd))
            {
                var right = ParseBitwiseOr();
                expr = new BinaryExpr(CogBinaryOp.LogicalAnd, expr, right);
            }
            return expr;
        }

        private CogExpr ParseBitwiseOr()
        {
            var expr = ParseBitwiseXor();
            while (Match(CogTokenType.Pipe))
            {
                var right = ParseBitwiseXor();
                expr = new BinaryExpr(CogBinaryOp.BitwiseOr, expr, right);
            }
            return expr;
        }

        private CogExpr ParseBitwiseXor()
        {
            var expr = ParseBitwiseAnd();
            while (Match(CogTokenType.Caret))
            {
                var right = ParseBitwiseAnd();
                expr = new BinaryExpr(CogBinaryOp.BitwiseXor, expr, right);
            }
            return expr;
        }

        private CogExpr ParseBitwiseAnd()
        {
            var expr = ParseEquality();
            while (Match(CogTokenType.Ampersand))
            {
                var right = ParseEquality();
                expr = new BinaryExpr(CogBinaryOp.BitwiseAnd, expr, right);
            }
            return expr;
        }

        private CogExpr ParseEquality()
        {
            var expr = ParseRelational();
            while (true)
            {
                if (Match(CogTokenType.EqualEqual))
                    expr = new BinaryExpr(CogBinaryOp.Equal, expr, ParseRelational());
                else if (Match(CogTokenType.BangEqual))
                    expr = new BinaryExpr(CogBinaryOp.NotEqual, expr, ParseRelational());
                else
                    break;
            }
            return expr;
        }

        private CogExpr ParseRelational()
        {
            var expr = ParseAdditive();
            while (true)
            {
                if (Match(CogTokenType.Less))
                    expr = new BinaryExpr(CogBinaryOp.LessThan, expr, ParseAdditive());
                else if (Match(CogTokenType.Greater))
                    expr = new BinaryExpr(CogBinaryOp.GreaterThan, expr, ParseAdditive());
                else if (Match(CogTokenType.LessEqual))
                    expr = new BinaryExpr(CogBinaryOp.LessThanOrEqual, expr, ParseAdditive());
                else if (Match(CogTokenType.GreaterEqual))
                    expr = new BinaryExpr(CogBinaryOp.GreaterThanOrEqual, expr, ParseAdditive());
                else
                    break;
            }
            return expr;
        }

        private CogExpr ParseAdditive()
        {
            var expr = ParseMultiplicative();
            while (true)
            {
                if (Match(CogTokenType.Plus))
                    expr = new BinaryExpr(CogBinaryOp.Add, expr, ParseMultiplicative());
                else if (Match(CogTokenType.Minus))
                    expr = new BinaryExpr(CogBinaryOp.Subtract, expr, ParseMultiplicative());
                else
                    break;
            }
            return expr;
        }

        private CogExpr ParseMultiplicative()
        {
            var expr = ParseUnary();
            while (true)
            {
                if (Match(CogTokenType.Star))
                    expr = new BinaryExpr(CogBinaryOp.Multiply, expr, ParseUnary());
                else if (Match(CogTokenType.Slash))
                    expr = new BinaryExpr(CogBinaryOp.Divide, expr, ParseUnary());
                else if (Match(CogTokenType.Percent))
                    expr = new BinaryExpr(CogBinaryOp.Modulo, expr, ParseUnary());
                else
                    break;
            }
            return expr;
        }

        private CogExpr ParseUnary()
        {
            if (Match(CogTokenType.Minus))
                return new UnaryExpr(CogUnaryOp.Negate, ParseUnary());
            if (Match(CogTokenType.Bang))
                return new UnaryExpr(CogUnaryOp.Not, ParseUnary());
            if (Match(CogTokenType.Tilde))
                return new UnaryExpr(CogUnaryOp.BitwiseNot, ParseUnary());
            if (Match(CogTokenType.Plus))
                return new UnaryExpr(CogUnaryOp.Plus, ParseUnary());

            return ParsePostfix();
        }

        private CogExpr ParsePostfix()
        {
            var expr = ParsePrimary();

            while (true)
            {
                if (Match(CogTokenType.OpenBracket))
                {
                    var index = ParseExpression();
                    Consume(CogTokenType.CloseBracket, "Expected ']' after array index");
                    expr = new IndexExpr(expr, index);
                }
                else if (Match(CogTokenType.OpenParen))
                {
                    var args = new List<CogExpr>();
                    if (!Check(CogTokenType.CloseParen))
                    {
                        args.Add(ParseAssignment());
                        while (Match(CogTokenType.Comma))
                        {
                            args.Add(ParseAssignment());
                        }
                    }
                    Consume(CogTokenType.CloseParen, "Expected ')' after argument list");
                    string verbName = expr is Identifier id ? id.Name : expr.ToString()!;
                    expr = new CallVerbExpr(verbName, args);
                }
                else
                {
                    break;
                }
            }

            return expr;
        }

        private CogExpr ParsePrimary()
        {
            if (Check(CogTokenType.IntLiteral))
            {
                int val = Current.IntValue;
                _pos++;
                return new IntLiteral(val);
            }

            if (Check(CogTokenType.FloatLiteral))
            {
                float val = Current.FloatValue;
                _pos++;
                return new FloatLiteral(val);
            }

            if (Check(CogTokenType.StringLiteral))
            {
                string text = Current.Text;
                _pos++;
                return new StringLiteral(text);
            }

            if (Check(CogTokenType.VectorLiteral))
            {
                Vector3 vec = Current.VectorValue;
                _pos++;
                return new VectorLiteral(vec);
            }

            if (Check(CogTokenType.Identifier))
            {
                string id = Current.Text;
                _pos++;
                return new Identifier(id);
            }

            if (Match(CogTokenType.OpenParen))
            {
                var inner = ParseExpression();
                Consume(CogTokenType.CloseParen, "Expected ')' after expression");
                return inner;
            }

            throw new FormatException($"Unexpected token in primary expression: {Current.Type} ('{Current.Text}') at line {Current.Line}:{Current.Column}");
        }
    }
}
