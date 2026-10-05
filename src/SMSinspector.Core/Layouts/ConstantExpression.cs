using System.Globalization;

namespace SMSinspector.Core.Layouts;

/// <summary>
/// Evaluates the integer constant expressions found in headers: array sizes, enum values
/// and preprocessor conditions. Supports C operators except the conditional operator,
/// casts and sizeof; anything else makes evaluation fail rather than guess.
/// </summary>
public static class ConstantExpression
{
    /// <param name="resolve">Value of a named constant, or null when unknown.</param>
    public static bool TryEvaluate(string expression, Func<string, long?> resolve, out long value)
    {
        var parser = new Parser(expression, resolve);
        try
        {
            value = parser.ParseOr();
            parser.SkipSpaces();
            return parser.AtEnd;
        }
        catch (FormatException)
        {
            value = 0;
            return false;
        }
        catch (DivideByZeroException)
        {
            value = 0;
            return false;
        }
    }

    private sealed class Parser(string text, Func<string, long?> resolve)
    {
        private int _pos;

        public bool AtEnd => _pos >= text.Length;

        public void SkipSpaces()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos]))
            {
                _pos++;
            }
        }

        public long ParseOr()
        {
            var left = ParseAnd();
            while (Accept("||"))
            {
                var right = ParseAnd();
                left = left != 0 || right != 0 ? 1 : 0;
            }

            return left;
        }

        private long ParseAnd()
        {
            var left = ParseBitOr();
            while (Accept("&&"))
            {
                var right = ParseBitOr();
                left = left != 0 && right != 0 ? 1 : 0;
            }

            return left;
        }

        private long ParseBitOr()
        {
            var left = ParseBitXor();
            while (AcceptSingle('|', '|'))
            {
                left |= ParseBitXor();
            }

            return left;
        }

        private long ParseBitXor()
        {
            var left = ParseBitAnd();
            while (Accept("^"))
            {
                left ^= ParseBitAnd();
            }

            return left;
        }

        private long ParseBitAnd()
        {
            var left = ParseEquality();
            while (AcceptSingle('&', '&'))
            {
                left &= ParseEquality();
            }

            return left;
        }

        private long ParseEquality()
        {
            var left = ParseRelational();
            while (true)
            {
                if (Accept("=="))
                {
                    left = left == ParseRelational() ? 1 : 0;
                }
                else if (Accept("!="))
                {
                    left = left != ParseRelational() ? 1 : 0;
                }
                else
                {
                    return left;
                }
            }
        }

        private long ParseRelational()
        {
            var left = ParseShift();
            while (true)
            {
                if (Accept("<="))
                {
                    left = left <= ParseShift() ? 1 : 0;
                }
                else if (Accept(">="))
                {
                    left = left >= ParseShift() ? 1 : 0;
                }
                else if (AcceptSingle('<', '<'))
                {
                    left = left < ParseShift() ? 1 : 0;
                }
                else if (AcceptSingle('>', '>'))
                {
                    left = left > ParseShift() ? 1 : 0;
                }
                else
                {
                    return left;
                }
            }
        }

        private long ParseShift()
        {
            var left = ParseAdditive();
            while (true)
            {
                if (Accept("<<"))
                {
                    left <<= (int)ParseAdditive();
                }
                else if (Accept(">>"))
                {
                    left >>= (int)ParseAdditive();
                }
                else
                {
                    return left;
                }
            }
        }

        private long ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (true)
            {
                if (Accept("+"))
                {
                    left += ParseMultiplicative();
                }
                else if (Accept("-"))
                {
                    left -= ParseMultiplicative();
                }
                else
                {
                    return left;
                }
            }
        }

        private long ParseMultiplicative()
        {
            var left = ParseUnary();
            while (true)
            {
                if (Accept("*"))
                {
                    left *= ParseUnary();
                }
                else if (Accept("/"))
                {
                    left /= ParseUnary();
                }
                else if (Accept("%"))
                {
                    left %= ParseUnary();
                }
                else
                {
                    return left;
                }
            }
        }

        private long ParseUnary()
        {
            if (Accept("-"))
            {
                return -ParseUnary();
            }

            if (Accept("+"))
            {
                return ParseUnary();
            }

            if (AcceptSingle('!', '='))
            {
                return ParseUnary() == 0 ? 1 : 0;
            }

            if (Accept("~"))
            {
                return ~ParseUnary();
            }

            return ParsePrimary();
        }

        private long ParsePrimary()
        {
            SkipSpaces();
            if (Accept("("))
            {
                var inner = ParseOr();
                if (!Accept(")"))
                {
                    throw new FormatException();
                }

                return inner;
            }

            if (_pos < text.Length && char.IsAsciiDigit(text[_pos]))
            {
                var start = _pos;
                while (_pos < text.Length && (char.IsAsciiLetterOrDigit(text[_pos])))
                {
                    _pos++;
                }

                var literal = text[start.._pos].TrimEnd('u', 'U', 'l', 'L');
                if (literal.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    return long.Parse(literal.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                }

                if (literal.Length > 1 && literal[0] == '0')
                {
                    return Convert.ToInt64(literal, 8);
                }

                return long.Parse(literal, NumberStyles.None, CultureInfo.InvariantCulture);
            }

            if (_pos < text.Length && (char.IsAsciiLetter(text[_pos]) || text[_pos] == '_'))
            {
                var start = _pos;
                while (_pos < text.Length && (char.IsAsciiLetterOrDigit(text[_pos]) || text[_pos] is '_' or ':'))
                {
                    _pos++;
                }

                return resolve(text[start.._pos]) ?? throw new FormatException();
            }

            throw new FormatException();
        }

        private bool Accept(string op)
        {
            SkipSpaces();
            if (string.CompareOrdinal(text, _pos, op, 0, op.Length) != 0)
            {
                return false;
            }

            _pos += op.Length;
            return true;
        }

        /// <summary>Accepts <paramref name="op"/> only when it is not the start of a two-character operator.</summary>
        private bool AcceptSingle(char op, char notFollowedBy)
        {
            SkipSpaces();
            if (_pos >= text.Length || text[_pos] != op || (_pos + 1 < text.Length && text[_pos + 1] == notFollowedBy))
            {
                return false;
            }

            _pos++;
            return true;
        }
    }
}
