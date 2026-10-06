namespace Panel.Sources.File;

/// <summary>
/// Recursive-descent parser for the `.scn` grammar (plan section 4). Statement boundaries are inferred from
/// token shape rather than physical newlines: a sample line's assigns are a run of `ident '='` pairs, which ends
/// as soon as the next token looks like a new time spec (a number or `+`) or a new `@` directive.
/// </summary>
public static class ScenarioParser
{
    public static ScenarioDocument Parse(string text)
    {
        var tokens = ScenarioLexer.Tokenize(text);
        return new Impl(tokens).ParseDocument();
    }

    private sealed class Impl
    {
        private readonly List<Token> _tokens;
        private int _pos;

        public Impl(List<Token> tokens) => _tokens = tokens;

        private Token Cur => _tokens[_pos];
        private Token Peek() => _tokens[Math.Min(_pos + 1, _tokens.Count - 1)];

        private Token Advance()
        {
            var t = Cur;
            if (_pos < _tokens.Count - 1) _pos++;
            return t;
        }

        private Token Expect(TokenKind kind, string what)
        {
            if (Cur.Kind != kind) throw Error($"Expected {what}, found {Describe(Cur)}.");
            return Advance();
        }

        private ScenarioParseException Error(string msg) => new(msg, Cur.Line, Cur.Column);

        private static string Describe(Token t) => t.Kind switch
        {
            TokenKind.Eof => "end of file",
            TokenKind.Ident => $"'{t.Text}'",
            TokenKind.Number => $"'{t.Number}{t.Unit}'",
            TokenKind.String => $"\"{t.Text}\"",
            _ => $"'{t.Kind}'",
        };

        public ScenarioDocument ParseDocument()
        {
            var sources = new List<ScenarioSourceDirective>();
            string? currentSourceName = null;
            var topics = new List<ScenarioTopicDirective>();
            var noises = new List<ScenarioNoiseDirective>();
            var samples = new List<ScenarioSampleLine>();
            var loop = false;

            while (Cur.Kind != TokenKind.Eof)
            {
                if (Cur.Kind == TokenKind.At)
                    ParseDirective(sources, ref currentSourceName, topics, noises, ref loop);
                else if (Cur.Kind is TokenKind.Number or TokenKind.Plus)
                    samples.Add(ParseSampleLine());
                else
                    throw Error($"Expected a directive ('@...') or a sample time, found {Describe(Cur)}.");
            }

            return new ScenarioDocument(sources, topics, noises, samples, loop);
        }

        private void ParseDirective(
            List<ScenarioSourceDirective> sources,
            ref string? currentSourceName,
            List<ScenarioTopicDirective> topics,
            List<ScenarioNoiseDirective> noises,
            ref bool loop)
        {
            Advance(); // '@'
            var kw = Expect(TokenKind.Ident, "a directive name");
            switch (kw.Text)
            {
                case "source":
                {
                    var name = Expect(TokenKind.Ident, "a source name").Text;
                    var rateKey = Expect(TokenKind.Ident, "'rate'");
                    if (rateKey.Text != "rate") throw new ScenarioParseException("Expected 'rate=N'.", rateKey.Line, rateKey.Column);
                    Expect(TokenKind.Equals, "'='");
                    var rateTok = Expect(TokenKind.Number, "a rate value");
                    sources.Add(new ScenarioSourceDirective(name, (int)rateTok.Number));
                    currentSourceName = name;
                    break;
                }
                case "topic":
                {
                    var nameTok = Expect(TokenKind.Ident, "a topic name");
                    var (type, enumValues, components) = ParseType();
                    ScenarioPolicy? policy = null;
                    if (Cur.Kind == TokenKind.Ident && Cur.Text == "policy")
                    {
                        Advance();
                        Expect(TokenKind.Equals, "'='");
                        policy = ParsePolicy();
                    }
                    topics.Add(new ScenarioTopicDirective(nameTok.Text, currentSourceName, type, enumValues, components, policy, nameTok.Line, nameTok.Column));
                    break;
                }
                case "noise":
                {
                    var glob = Expect(TokenKind.Ident, "a topic glob").Text;
                    var sigmaKey = Expect(TokenKind.Ident, "'sigma'");
                    if (sigmaKey.Text != "sigma") throw new ScenarioParseException("Expected 'sigma=N'.", sigmaKey.Line, sigmaKey.Column);
                    Expect(TokenKind.Equals, "'='");
                    var sigmaTok = Expect(TokenKind.Number, "a sigma value");
                    noises.Add(new ScenarioNoiseDirective(glob, sigmaTok.Number));
                    break;
                }
                case "loop":
                    loop = true;
                    break;
                default:
                    throw new ScenarioParseException($"Unknown directive '@{kw.Text}'.", kw.Line, kw.Column);
            }
        }

        private (ScenarioTopicType, IReadOnlyList<string>?, IReadOnlyList<string>?) ParseType()
        {
            var tok = Expect(TokenKind.Ident, "a topic type");
            switch (tok.Text)
            {
                case "float": return (ScenarioTopicType.Float, null, null);
                case "int": return (ScenarioTopicType.Int, null, null);
                case "bool": return (ScenarioTopicType.Bool, null, null);
                case "string": return (ScenarioTopicType.String, null, null);
                case "enum": return (ScenarioTopicType.Enum, ParseBracketedNames(), null);
                case "vec": return (ScenarioTopicType.Vec, null, ParseBracketedNames());
                default:
                    throw new ScenarioParseException($"Unknown topic type '{tok.Text}'.", tok.Line, tok.Column);
            }
        }

        private List<string> ParseBracketedNames()
        {
            Expect(TokenKind.LBracket, "'['");
            var names = new List<string> { Expect(TokenKind.Ident, "a name").Text };
            while (Cur.Kind == TokenKind.Comma) { Advance(); names.Add(Expect(TokenKind.Ident, "a name").Text); }
            Expect(TokenKind.RBracket, "']'");
            return names;
        }

        private ScenarioPolicy ParsePolicy()
        {
            var tok = Expect(TokenKind.Ident, "a policy");
            switch (tok.Text)
            {
                case "every": return new ScenarioPolicy(ScenarioPolicyKind.Every);
                case "change": return new ScenarioPolicy(ScenarioPolicyKind.Change);
                case "deadband":
                {
                    Expect(TokenKind.LParen, "'('");
                    var n = Expect(TokenKind.Number, "a threshold");
                    Expect(TokenKind.RParen, "')'");
                    return new ScenarioPolicy(ScenarioPolicyKind.Deadband, n.Number);
                }
                default:
                    throw new ScenarioParseException($"Unknown policy '{tok.Text}'.", tok.Line, tok.Column);
            }
        }

        private ScenarioSampleLine ParseSampleLine()
        {
            var startTok = Cur;
            var time = ParseTime();
            var assigns = new List<ScenarioAssign>();
            while (Cur.Kind == TokenKind.Ident && Peek().Kind == TokenKind.Equals)
                assigns.Add(ParseAssign());
            return new ScenarioSampleLine(time, assigns, startTok.Line);
        }

        private ScenarioTime ParseTime()
        {
            var relative = false;
            if (Cur.Kind == TokenKind.Plus) { relative = true; Advance(); }
            var numTok = Expect(TokenKind.Number, "a time value");
            var unit = numTok.Unit switch
            {
                null => ScenarioTimeUnit.Ms,
                "ms" => ScenarioTimeUnit.Ms,
                "s" => ScenarioTimeUnit.S,
                _ => throw new ScenarioParseException($"Unknown time unit '{numTok.Unit}'.", numTok.Line, numTok.Column),
            };
            return new ScenarioTime(relative, (long)numTok.Number, unit);
        }

        private ScenarioAssign ParseAssign()
        {
            var topicTok = Expect(TokenKind.Ident, "a topic name");
            Expect(TokenKind.Equals, "'='");
            var value = ParseValue();
            double? confidence = null;
            if (Cur.Kind == TokenKind.Tilde) { Advance(); confidence = Expect(TokenKind.Number, "a confidence value").Number; }
            return new ScenarioAssign(topicTok.Text, value, confidence, topicTok.Line, topicTok.Column);
        }

        private ScenarioValueNode ParseValue()
        {
            if (Cur.Kind == TokenKind.Bang) { Advance(); return ScenarioInvalidNode.Instance; }
            if (Cur.Kind == TokenKind.Ident && Peek().Kind == TokenKind.LParen &&
                (Cur.Text == "blink" || Cur.Text == "ramp" || Cur.Text == "flicker"))
                return ParseGenerator(Cur.Text);
            return ParseLiteral();
        }

        private ScenarioValueNode ParseLiteral()
        {
            switch (Cur.Kind)
            {
                case TokenKind.LParen:
                    return new ScenarioLiteralNode(ParseVectorLiteral());
                case TokenKind.Number:
                {
                    var t = Advance();
                    return new ScenarioLiteralNode(new ScenarioLiteral(ScenarioLiteralKind.Number, Number: t.Number));
                }
                case TokenKind.String:
                {
                    var t = Advance();
                    return new ScenarioLiteralNode(new ScenarioLiteral(ScenarioLiteralKind.String, Text: t.Text));
                }
                case TokenKind.Bool:
                {
                    var t = Advance();
                    return new ScenarioLiteralNode(new ScenarioLiteral(ScenarioLiteralKind.Bool, Bool: t.Bool));
                }
                case TokenKind.Ident:
                {
                    var t = Advance();
                    return new ScenarioLiteralNode(new ScenarioLiteral(ScenarioLiteralKind.Ident, Text: t.Text));
                }
                default:
                    throw Error($"Expected a value, found {Describe(Cur)}.");
            }
        }

        private ScenarioLiteral ParseVectorLiteral()
        {
            Expect(TokenKind.LParen, "'('");
            var nums = new List<double> { Expect(TokenKind.Number, "a number").Number };
            while (Cur.Kind == TokenKind.Comma) { Advance(); nums.Add(Expect(TokenKind.Number, "a number").Number); }
            Expect(TokenKind.RParen, "')'");
            return new ScenarioLiteral(ScenarioLiteralKind.Vector, Vector: nums.ToArray());
        }

        private ScenarioValueNode ParseGenerator(string kind)
        {
            Advance(); // generator name
            Expect(TokenKind.LParen, "'('");
            var v1 = ParseValue();
            Expect(TokenKind.Comma, "','");
            var v2 = ParseValue();
            Expect(TokenKind.Comma, "','");

            switch (kind)
            {
                case "blink":
                {
                    var freqTok = Expect(TokenKind.Number, "a frequency, e.g. '5Hz'");
                    if (freqTok.Unit is null || !freqTok.Unit.Equals("Hz", StringComparison.OrdinalIgnoreCase))
                        throw new ScenarioParseException("Expected a frequency in Hz, e.g. '5Hz'.", freqTok.Line, freqTok.Column);
                    var duty = 0.5;
                    if (Cur.Kind == TokenKind.Comma) { Advance(); duty = Expect(TokenKind.Number, "a duty cycle").Number; }
                    Expect(TokenKind.RParen, "')'");
                    return new ScenarioBlinkNode(v1, v2, freqTok.Number, duty);
                }
                case "ramp":
                {
                    var durTok = Expect(TokenKind.Number, "a duration, e.g. '100ms'");
                    double ms = durTok.Unit switch
                    {
                        null or "ms" => durTok.Number,
                        "s" => durTok.Number * 1000,
                        _ => throw new ScenarioParseException($"Unknown duration unit '{durTok.Unit}'.", durTok.Line, durTok.Column),
                    };
                    Expect(TokenKind.RParen, "')'");
                    return new ScenarioRampNode(v1, v2, ms);
                }
                case "flicker":
                {
                    var framesTok = Expect(TokenKind.Number, "a frame count");
                    Expect(TokenKind.RParen, "')'");
                    return new ScenarioFlickerNode(v1, v2, (int)framesTok.Number);
                }
                default:
                    throw new InvalidOperationException("Unreachable generator kind.");
            }
        }
    }
}
