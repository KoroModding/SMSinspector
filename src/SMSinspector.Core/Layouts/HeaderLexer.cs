using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SMSinspector.Core.Layouts;

public enum TokenKind
{
    Identifier,
    Number,
    String,
    Char,
    Punct,

    /// <summary>A block comment holding only a hex offset: <c>/* 0x1A4 */</c>.</summary>
    OffsetComment,

    /// <summary>A <c>//</c> comment. Kept because <c>/* 0x18 */ // vt</c> marks a vtable pointer.</summary>
    LineComment,
}

public readonly record struct HeaderToken(TokenKind Kind, string Text, int Line, VersionMask Versions)
{
    /// <summary>The offset of an <see cref="TokenKind.OffsetComment"/>.</summary>
    public uint Offset => Kind == TokenKind.OffsetComment
        ? uint.Parse(Text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : 0;

    public bool Is(string text) => Text == text && Kind is TokenKind.Punct or TokenKind.Identifier;

    public override string ToString() => Text;
}

/// <summary>A <c>#define NAME value</c> with a plain value, kept for array sizes.</summary>
public sealed record MacroDefinition(string Name, string Value, VersionMask Versions);

/// <summary>
/// Splits a header into tokens and runs the small part of the preprocessor that layouts
/// need. Version conditionals (<c>#ifdef VERSION_GMSP01</c> and friends) are not resolved:
/// both branches are kept and every token is tagged with the versions it belongs to.
/// Other conditionals are evaluated once, the way the game's compiler would see them.
/// </summary>
public static partial class HeaderLexer
{
    /// <summary>Macros the game's compiler (Metrowerks, C++ mode) defines.</summary>
    private static readonly HashSet<string> CompilerMacros = new(StringComparer.Ordinal)
    {
        "__MWERKS__", "__cplusplus", "__CWCC__", "__POWERPC__", "__PPC__", "__PPCGEKKO__", "GEKKO",
    };

    private const string PalMacro = "VERSION_GMSP01";
    private const string JpMacro = "VERSION_GMSJ01";

    public sealed record Result(IReadOnlyList<HeaderToken> Tokens, IReadOnlyList<MacroDefinition> Macros, IReadOnlyList<string> Warnings);

    public static Result Tokenize(string source)
    {
        var tokens = new List<HeaderToken>();
        var macros = new List<MacroDefinition>();
        var warnings = new List<string>();
        var definedHere = new HashSet<string>(StringComparer.Ordinal);
        var frames = new Stack<Frame>();
        var lines = source.Replace("\r\n", "\n").Split('\n');
        var inBlockComment = false;
        var blockComment = new StringBuilder();
        var blockCommentLine = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            var line = lines[i];

            if (!inBlockComment && line.TrimStart().StartsWith('#'))
            {
                // Directives can continue over several lines with a trailing backslash.
                var directive = new StringBuilder(line.TrimStart()[1..]);
                while (directive.Length > 0 && directive[^1] == '\\' && i + 1 < lines.Length)
                {
                    directive.Length--;
                    directive.Append(' ').Append(lines[++i]);
                }

                HandleDirective(StripComments(directive.ToString()).Trim(), lineNumber, frames, definedHere, macros, warnings);
                continue;
            }

            if (!IsActive(frames))
            {
                continue;
            }

            var versions = CurrentVersions(frames);
            LexLine(line, lineNumber, versions, tokens, ref inBlockComment, blockComment, ref blockCommentLine);
        }

        if (frames.Count > 0)
        {
            warnings.Add($"{frames.Count} conditional block(s) not closed at end of file.");
        }

        return new Result(tokens, macros, warnings);
    }

    private sealed class Frame
    {
        public bool IsVersion;

        /// <summary>For version frames: the versions of the current branch.</summary>
        public VersionMask Versions = VersionMask.Both;

        /// <summary>For version frames: the versions the first branch applied to.</summary>
        public VersionMask FirstBranch = VersionMask.Both;

        /// <summary>For static frames: whether the current branch is compiled.</summary>
        public bool Active = true;

        /// <summary>For static frames: whether an earlier branch was already taken.</summary>
        public bool Taken;

        /// <summary>Whether the enclosing code is compiled at all.</summary>
        public bool ParentActive = true;
    }

    private static bool IsActive(Stack<Frame> frames) => frames.Count == 0 || frames.All(f => f.IsVersion || f.Active);

    private static VersionMask CurrentVersions(Stack<Frame> frames)
    {
        var mask = VersionMask.Both;
        foreach (var frame in frames)
        {
            if (frame.IsVersion)
            {
                mask &= frame.Versions;
            }
        }

        return mask;
    }

    private static void HandleDirective(
        string directive, int line, Stack<Frame> frames, HashSet<string> definedHere, List<MacroDefinition> macros, List<string> warnings)
    {
        var match = DirectivePattern().Match(directive);
        var keyword = match.Success ? match.Groups["keyword"].Value : "";
        var argument = match.Success ? match.Groups["argument"].Value.Trim() : "";
        var parentActive = IsActive(frames);

        switch (keyword)
        {
            case "ifdef":
            case "ifndef":
            case "if":
            {
                var version = keyword switch
                {
                    "ifdef" => VersionOf(argument, negate: false),
                    "ifndef" => VersionOf(argument, negate: true),
                    _ => VersionOfExpression(argument),
                };

                if (version is { } mask)
                {
                    frames.Push(new Frame { IsVersion = true, Versions = mask, FirstBranch = mask, ParentActive = parentActive });
                    break;
                }

                var value = keyword switch
                {
                    "ifdef" => IsDefined(argument, definedHere),
                    "ifndef" => !IsDefined(argument, definedHere),
                    _ => EvaluateCondition(argument, definedHere),
                };
                frames.Push(new Frame { Active = value, Taken = value, ParentActive = parentActive });
                break;
            }

            case "elif":
            {
                if (frames.Count == 0)
                {
                    warnings.Add($"Line {line}: #elif without #if.");
                    break;
                }

                var frame = frames.Peek();
                if (frame.IsVersion)
                {
                    // "#elif" after a version test: the remaining versions take this branch.
                    frame.Versions = VersionMask.Both & ~frame.FirstBranch;
                    break;
                }

                var value = !frame.Taken && EvaluateCondition(argument, definedHere);
                frame.Active = value;
                frame.Taken |= value;
                break;
            }

            case "else":
            {
                if (frames.Count == 0)
                {
                    warnings.Add($"Line {line}: #else without #if.");
                    break;
                }

                var frame = frames.Peek();
                if (frame.IsVersion)
                {
                    frame.Versions = VersionMask.Both & ~frame.FirstBranch;
                }
                else
                {
                    frame.Active = !frame.Taken;
                    frame.Taken = true;
                }

                break;
            }

            case "endif":
                if (frames.Count == 0)
                {
                    warnings.Add($"Line {line}: #endif without #if.");
                }
                else
                {
                    frames.Pop();
                }

                break;

            case "define" when parentActive:
            {
                var define = DefinePattern().Match(argument);
                if (define.Success)
                {
                    definedHere.Add(define.Groups["name"].Value);
                    var value = define.Groups["value"].Value.Trim();
                    if (!define.Groups["params"].Success && value.Length > 0)
                    {
                        macros.Add(new MacroDefinition(define.Groups["name"].Value, value, CurrentVersions(frames)));
                    }
                }

                break;
            }

            case "undef" when parentActive:
                definedHere.Remove(argument);
                break;
        }
    }

    private static VersionMask? VersionOf(string macro, bool negate)
    {
        VersionMask? mask = macro switch
        {
            PalMacro => VersionMask.Pal,
            JpMacro => VersionMask.Jp,
            _ => null,
        };

        return mask is { } m && negate ? VersionMask.Both & ~m : mask;
    }

    /// <summary>Recognises <c>#if defined(VERSION_X)</c>, <c>#if !defined(VERSION_X)</c> and <c>#if VERSION_X</c>.</summary>
    private static VersionMask? VersionOfExpression(string expression)
    {
        var match = VersionExpressionPattern().Match(expression);
        return match.Success ? VersionOf(match.Groups["macro"].Value, negate: match.Groups["not"].Success) : null;
    }

    private static bool IsDefined(string macro, HashSet<string> definedHere) =>
        CompilerMacros.Contains(macro) || definedHere.Contains(macro);

    /// <summary>
    /// Evaluates a static <c>#if</c>: numbers, <c>defined(X)</c>, <c>!</c>, comparisons,
    /// <c>&amp;&amp;</c> and <c>||</c>. Unknown identifiers are 0, as in C.
    /// </summary>
    private static bool EvaluateCondition(string expression, HashSet<string> definedHere)
    {
        var replaced = DefinedPattern().Replace(expression, m => IsDefined(m.Groups["name"].Value, definedHere) ? "1" : "0");
        replaced = IdentifierPattern().Replace(replaced, m => CompilerMacros.Contains(m.Value) ? "1" : "0");
        return ConstantExpression.TryEvaluate(replaced, _ => null, out var value) && value != 0;
    }

    private static string StripComments(string text)
    {
        var withoutBlock = BlockCommentPattern().Replace(text, " ");
        var lineComment = withoutBlock.IndexOf("//", StringComparison.Ordinal);
        return lineComment >= 0 ? withoutBlock[..lineComment] : withoutBlock;
    }

    private static void LexLine(
        string line, int lineNumber, VersionMask versions, List<HeaderToken> tokens,
        ref bool inBlockComment, StringBuilder blockComment, ref int blockCommentLine)
    {
        var pos = 0;
        while (pos < line.Length)
        {
            if (inBlockComment)
            {
                var end = line.IndexOf("*/", pos, StringComparison.Ordinal);
                if (end < 0)
                {
                    blockComment.Append(line, pos, line.Length - pos).Append('\n');
                    return;
                }

                blockComment.Append(line, pos, end - pos);
                inBlockComment = false;
                pos = end + 2;
                EmitBlockComment(blockComment.ToString(), blockCommentLine, versions, tokens);
                continue;
            }

            var c = line[pos];
            if (char.IsWhiteSpace(c))
            {
                pos++;
                continue;
            }

            if (c == '/' && pos + 1 < line.Length && line[pos + 1] == '*')
            {
                inBlockComment = true;
                blockComment.Clear();
                blockCommentLine = lineNumber;
                pos += 2;
                continue;
            }

            if (c == '/' && pos + 1 < line.Length && line[pos + 1] == '/')
            {
                tokens.Add(new HeaderToken(TokenKind.LineComment, line[(pos + 2)..].Trim(), lineNumber, versions));
                return;
            }

            if (c is '"' or '\'')
            {
                var end = pos + 1;
                while (end < line.Length && line[end] != c)
                {
                    end += line[end] == '\\' ? 2 : 1;
                }

                end = Math.Min(end + 1, line.Length);
                tokens.Add(new HeaderToken(c == '"' ? TokenKind.String : TokenKind.Char, line[pos..end], lineNumber, versions));
                pos = end;
                continue;
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                var end = pos + 1;
                while (end < line.Length && (char.IsAsciiLetterOrDigit(line[end]) || line[end] == '_'))
                {
                    end++;
                }

                tokens.Add(new HeaderToken(TokenKind.Identifier, line[pos..end], lineNumber, versions));
                pos = end;
                continue;
            }

            if (char.IsAsciiDigit(c) || (c == '.' && pos + 1 < line.Length && char.IsAsciiDigit(line[pos + 1])))
            {
                var end = pos + 1;
                while (end < line.Length && (char.IsAsciiLetterOrDigit(line[end]) || line[end] == '.'
                    || (line[end] is '+' or '-' && line[end - 1] is 'e' or 'E' && !line[pos..end].StartsWith("0x", StringComparison.OrdinalIgnoreCase))))
                {
                    end++;
                }

                tokens.Add(new HeaderToken(TokenKind.Number, line[pos..end], lineNumber, versions));
                pos = end;
                continue;
            }

            // "::" and "..." are the only multi-character punctuators the parser cares about.
            // ">>" stays as two tokens so nested template arguments close correctly.
            if (c == ':' && pos + 1 < line.Length && line[pos + 1] == ':')
            {
                tokens.Add(new HeaderToken(TokenKind.Punct, "::", lineNumber, versions));
                pos += 2;
                continue;
            }

            if (c == '.' && line.AsSpan(pos).StartsWith("..."))
            {
                tokens.Add(new HeaderToken(TokenKind.Punct, "...", lineNumber, versions));
                pos += 3;
                continue;
            }

            tokens.Add(new HeaderToken(TokenKind.Punct, c.ToString(), lineNumber, versions));
            pos++;
        }

        if (inBlockComment)
        {
            blockComment.Append('\n');
        }
    }

    private static void EmitBlockComment(string content, int line, VersionMask versions, List<HeaderToken> tokens)
    {
        var trimmed = content.Trim();
        if (OffsetPattern().IsMatch(trimmed))
        {
            tokens.Add(new HeaderToken(TokenKind.OffsetComment, "0x" + trimmed[2..].ToUpperInvariant(), line, versions));
        }
    }

    [GeneratedRegex(@"^(?<keyword>[a-z]+)\b(?<argument>.*)$")]
    private static partial Regex DirectivePattern();

    [GeneratedRegex(@"^(?<name>[A-Za-z_]\w*)(?<params>\([^)]*\))?(?<value>.*)$")]
    private static partial Regex DefinePattern();

    [GeneratedRegex(@"^\s*(?<not>!\s*)?(?:defined\s*\(\s*(?<macro>\w+)\s*\)|defined\s+(?<macro>\w+)|(?<macro>VERSION_\w+))\s*$")]
    private static partial Regex VersionExpressionPattern();

    [GeneratedRegex(@"defined\s*\(\s*(?<name>\w+)\s*\)|defined\s+(?<name>\w+)")]
    private static partial Regex DefinedPattern();

    [GeneratedRegex(@"\b[A-Za-z_]\w*\b")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"/\*.*?\*/")]
    private static partial Regex BlockCommentPattern();

    [GeneratedRegex(@"^0[xX][0-9A-Fa-f]+$")]
    private static partial Regex OffsetPattern();
}
