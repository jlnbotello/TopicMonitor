using System.Globalization;
using System.Text;

namespace Panel.Sources.File;

public enum TokenKind
{
    At,         // @
    Ident,      // name, keyword, or dotted topic name / glob ("led.*.raw")
    Number,     // possibly with an attached unit, e.g. "5Hz", "100ms"
    String,     // "quoted"
    Bool,       // true / false
    Equals,     // =
    Comma,      // ,
    LParen,     // (
    RParen,     // )
    LBracket,   // [
    RBracket,   // ]
    Tilde,      // ~
    Plus,       // +
    Bang,       // !
    Eof,
}

public sealed record Token(
    TokenKind Kind,
    int Line,
    int Column,
    string Text = "",
    double Number = 0,
    string? Unit = null,
    bool Bool = false);

/// <summary>Tokenizes a `.scn` file. Comments (`#` to end of line) and blank lines are discarded here.</summary>
public static class ScenarioLexer
{
    public static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        int i = 0, line = 1, col = 1;
        int n = text.Length;

        char Cur() => i < n ? text[i] : '\0';
        char PeekAt(int offset) => i + offset < n ? text[i + offset] : '\0';

        void Advance()
        {
            if (i >= n) return;
            if (text[i] == '\n') { line++; col = 1; }
            else { col++; }
            i++;
        }

        while (i < n)
        {
            var c = Cur();

            if (c == '\r') { Advance(); continue; }
            if (c is '\n' or ' ' or '\t') { Advance(); continue; }
            if (c == '#') { while (i < n && text[i] != '\n') Advance(); continue; }

            var startLine = line;
            var startCol = col;

            switch (c)
            {
                case '@': Advance(); tokens.Add(new Token(TokenKind.At, startLine, startCol)); continue;
                case '=': Advance(); tokens.Add(new Token(TokenKind.Equals, startLine, startCol)); continue;
                case ',': Advance(); tokens.Add(new Token(TokenKind.Comma, startLine, startCol)); continue;
                case '(': Advance(); tokens.Add(new Token(TokenKind.LParen, startLine, startCol)); continue;
                case ')': Advance(); tokens.Add(new Token(TokenKind.RParen, startLine, startCol)); continue;
                case '[': Advance(); tokens.Add(new Token(TokenKind.LBracket, startLine, startCol)); continue;
                case ']': Advance(); tokens.Add(new Token(TokenKind.RBracket, startLine, startCol)); continue;
                case '~': Advance(); tokens.Add(new Token(TokenKind.Tilde, startLine, startCol)); continue;
                case '+': Advance(); tokens.Add(new Token(TokenKind.Plus, startLine, startCol)); continue;
                case '!': Advance(); tokens.Add(new Token(TokenKind.Bang, startLine, startCol)); continue;
            }

            if (c == '"')
            {
                Advance();
                var sb = new StringBuilder();
                while (true)
                {
                    if (i >= n) throw new ScenarioParseException("Unterminated string literal.", startLine, startCol);
                    var cc = Cur();
                    if (cc == '"') { Advance(); break; }
                    if (cc == '\\' && PeekAt(1) == '"') { sb.Append('"'); Advance(); Advance(); continue; }
                    if (cc == '\n') throw new ScenarioParseException("Unterminated string literal.", startLine, startCol);
                    sb.Append(cc);
                    Advance();
                }
                tokens.Add(new Token(TokenKind.String, startLine, startCol, Text: sb.ToString()));
                continue;
            }

            if (char.IsDigit(c))
            {
                var sb = new StringBuilder();
                while (i < n && (char.IsDigit(Cur()) || Cur() == '.')) { sb.Append(Cur()); Advance(); }
                var numText = sb.ToString();
                if (!double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                    throw new ScenarioParseException($"Invalid number '{numText}'.", startLine, startCol);

                string? unit = null;
                if (i < n && char.IsLetter(Cur()))
                {
                    var usb = new StringBuilder();
                    while (i < n && char.IsLetter(Cur())) { usb.Append(Cur()); Advance(); }
                    unit = usb.ToString();
                }
                tokens.Add(new Token(TokenKind.Number, startLine, startCol, Number: num, Unit: unit));
                continue;
            }

            if (char.IsLetter(c) || c == '_' || c == '*')
            {
                var sb = new StringBuilder();
                while (i < n && (char.IsLetterOrDigit(Cur()) || Cur() is '_' or '.' or '*')) { sb.Append(Cur()); Advance(); }
                var word = sb.ToString();

                if (word.Equals("true", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Bool, startLine, startCol, Bool: true));
                else if (word.Equals("false", StringComparison.OrdinalIgnoreCase))
                    tokens.Add(new Token(TokenKind.Bool, startLine, startCol, Bool: false));
                else
                    tokens.Add(new Token(TokenKind.Ident, startLine, startCol, Text: word));
                continue;
            }

            throw new ScenarioParseException($"Unexpected character '{c}'.", startLine, startCol);
        }

        tokens.Add(new Token(TokenKind.Eof, line, col));
        return tokens;
    }
}
