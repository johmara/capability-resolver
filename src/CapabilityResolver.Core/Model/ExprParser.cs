using System.Text;

namespace CapabilityResolver.Model;

/// <summary>
/// Parses constraint text in UVL's boolean syntax: <c>!</c>, <c>&amp;</c>, <c>|</c>, <c>=&gt;</c>, <c>&lt;=&gt;</c>
/// and parentheses. Feature names are bare words (<c>[A-Za-z0-9_.]</c>) or double-quoted strings.
/// Precedence, loosest first: <c>&lt;=&gt;</c>, <c>=&gt;</c> (right-associative), <c>|</c>, <c>&amp;</c>, <c>!</c>.
/// </summary>
public static class ExprParser
{
    public static Expr Parse(string text)
    {
        var parser = new Parser(Tokenize(text), text);
        var expr = parser.Iff();
        parser.ExpectEnd();
        return expr;
    }

    /// <summary>Quotes a feature name when it is not a bare word, so that <see cref="Expr.ToString"/> round-trips.</summary>
    public static string Quote(string name) =>
        name.Length > 0 && name.All(IsWordChar) ? name : $"\"{name.Replace("\"", "\\\"")}\"";

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.';

    private enum T { Ident, Not, And, Or, Implies, Iff, Open, Close }

    private readonly record struct Token(T Type, string Text);

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length) i++;
                    sb.Append(text[i++]);
                }
                if (i >= text.Length) throw new FormatException($"Unterminated quote in constraint: {text}");
                i++;
                tokens.Add(new(T.Ident, sb.ToString()));
            }
            else if (IsWordChar(c))
            {
                var start = i;
                while (i < text.Length && IsWordChar(text[i])) i++;
                tokens.Add(new(T.Ident, text[start..i]));
            }
            else if (text.AsSpan(i).StartsWith("<=>")) { tokens.Add(new(T.Iff, "<=>")); i += 3; }
            else if (text.AsSpan(i).StartsWith("=>")) { tokens.Add(new(T.Implies, "=>")); i += 2; }
            else if (c == '!') { tokens.Add(new(T.Not, "!")); i++; }
            else if (c == '&') { tokens.Add(new(T.And, "&")); i++; }
            else if (c == '|') { tokens.Add(new(T.Or, "|")); i++; }
            else if (c == '(') { tokens.Add(new(T.Open, "(")); i++; }
            else if (c == ')') { tokens.Add(new(T.Close, ")")); i++; }
            else throw new FormatException($"Unexpected '{c}' in constraint: {text}");
        }
        return tokens;
    }

    private sealed class Parser(List<Token> tokens, string text)
    {
        private int _pos;

        private Token? Peek => _pos < tokens.Count ? tokens[_pos] : null;

        private bool Accept(T type)
        {
            if (Peek?.Type != type) return false;
            _pos++;
            return true;
        }

        public Expr Iff()
        {
            var left = Implies();
            while (Accept(T.Iff)) left = Expr.Iff(left, Implies());
            return left;
        }

        private Expr Implies()
        {
            var left = Or();
            return Accept(T.Implies) ? Expr.Implies(left, Implies()) : left;
        }

        private Expr Or()
        {
            var left = And();
            while (Accept(T.Or)) left = Expr.Or(left, And());
            return left;
        }

        private Expr And()
        {
            var left = Unary();
            while (Accept(T.And)) left = Expr.And(left, Unary());
            return left;
        }

        private Expr Unary()
        {
            if (Accept(T.Not)) return Expr.Not(Unary());
            if (Accept(T.Open))
            {
                var inner = Iff();
                if (!Accept(T.Close)) throw new FormatException($"Missing ')' in constraint: {text}");
                return inner;
            }
            if (Peek is { Type: T.Ident } ident)
            {
                _pos++;
                return Expr.Ref(ident.Text);
            }
            throw new FormatException($"Expected a feature name or '(' in constraint: {text}");
        }

        public void ExpectEnd()
        {
            if (_pos < tokens.Count) throw new FormatException($"Unexpected '{tokens[_pos].Text}' in constraint: {text}");
        }
    }
}
