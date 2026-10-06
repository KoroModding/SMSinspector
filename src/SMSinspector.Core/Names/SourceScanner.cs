using SMSinspector.Core.Layouts;

namespace SMSinspector.Core.Names;

/// <summary>A function definition found in a decomp source or header, with its body.</summary>
/// <param name="ClassName">The qualified class the function belongs to.</param>
/// <param name="Parameters">Parameter names, in order; unnamed parameters are skipped.</param>
/// <param name="Body">The tokens between the braces.</param>
/// <param name="IsUnusedMarked">A <c>// UNUSED</c> comment precedes or follows the definition.</param>
public sealed record FunctionBody(
    string File,
    int Line,
    string ClassName,
    string Name,
    bool IsConst,
    IReadOnlyList<string> Parameters,
    IReadOnlyList<HeaderToken> Body,
    bool IsUnusedMarked);

/// <summary>A member function declared without a body and marked <c>// UNUSED</c>.</summary>
public sealed record UnusedDeclaration(string File, int Line, string ClassName, string Name);

public sealed class ScannedSource
{
    public List<FunctionBody> Bodies { get; } = [];

    public List<UnusedDeclaration> UnusedDeclarations { get; } = [];
}

/// <summary>
/// Finds member function definitions in C++ sources: inline bodies inside class
/// definitions, and <c>TClass::name(...) { ... }</c> at namespace level. Like the header
/// parser it reads tokens and matches brackets; it is not a C++ parser. Only the tokens of
/// one game version are read.
/// </summary>
public sealed class SourceScanner
{
    /// <summary>Words that end an unnamed parameter's type, so they are not taken for its name.</summary>
    private static readonly HashSet<string> TypeWords = new(StringComparer.Ordinal)
    {
        "const", "void", "bool", "char", "short", "int", "long", "float", "double", "signed", "unsigned",
        "u8", "s8", "u16", "s16", "u32", "s32", "u64", "s64", "f32", "f64",
    };

    private enum FrameKind
    {
        Namespace,
        Class,

        /// <summary><c>extern "C" { }</c>: no scope of its own.</summary>
        Transparent,
    }

    private sealed record Frame(FrameKind Kind, string QualifiedName);

    private readonly string _file;
    private readonly List<HeaderToken> _tokens;
    private readonly ScannedSource _result;
    private readonly Stack<Frame> _frames = new();
    private readonly List<HeaderToken> _statement = [];
    private bool _pendingUnused;
    private int _lastTerminatorLine = -1;
    private Action? _markLastUnused;

    private SourceScanner(string file, List<HeaderToken> tokens, ScannedSource result)
    {
        _file = file;
        _tokens = tokens;
        _result = result;
    }

    public static ScannedSource Scan(string file, string source, VersionMask version, ScannedSource? into = null)
    {
        var result = into ?? new ScannedSource();
        var tokens = HeaderLexer.Tokenize(source).Tokens
            .Where(t => (t.Versions & version) != 0 && t.Kind != TokenKind.OffsetComment)
            .ToList();
        new SourceScanner(file, tokens, result).Run();
        return result;
    }

    private string CurrentScope => _frames.FirstOrDefault(f => f.Kind != FrameKind.Transparent)?.QualifiedName ?? "";

    private bool InClass => _frames.FirstOrDefault(f => f.Kind != FrameKind.Transparent)?.Kind == FrameKind.Class;

    private static string Combine(string scope, string name) => scope.Length == 0 ? name : $"{scope}::{name}";

    private void Run()
    {
        for (var i = 0; i < _tokens.Count; i++)
        {
            var token = _tokens[i];
            if (token.Kind == TokenKind.LineComment)
            {
                OnComment(token);
                continue;
            }

            if (token.Is("{"))
            {
                i = OnOpenBrace(i);
                continue;
            }

            if (token.Is("}"))
            {
                _markLastUnused = null;
                if (_frames.Count > 0)
                {
                    _frames.Pop();
                }

                EndStatement(token.Line);
                continue;
            }

            if (token.Is(";"))
            {
                OnDeclarationEnd();
                EndStatement(token.Line);
                continue;
            }

            _statement.Add(token);
        }
    }

    private void OnComment(HeaderToken comment)
    {
        if (!comment.Text.StartsWith("UNUSED", StringComparison.Ordinal))
        {
            return;
        }

        // "void f(); // UNUSED" marks the statement it trails; a comment on its own line marks the next one.
        if (_statement.Count == 0 && comment.Line == _lastTerminatorLine && _markLastUnused is not null)
        {
            _markLastUnused();
            _markLastUnused = null;
        }
        else
        {
            _pendingUnused = true;
        }
    }

    private void EndStatement(int line)
    {
        _statement.Clear();
        _pendingUnused = false;
        _lastTerminatorLine = line;
    }

    private int OnOpenBrace(int index)
    {
        var start = SkipPrefixes(_statement);
        var head = start < _statement.Count ? _statement[start] : default;

        if (head.Is("namespace"))
        {
            var name = start + 1 < _statement.Count && _statement[start + 1].Kind == TokenKind.Identifier ? _statement[start + 1].Text : "";
            _frames.Push(new Frame(FrameKind.Namespace, name.Length == 0 ? CurrentScope : Combine(CurrentScope, name)));
            EndStatement(_tokens[index].Line);
            return index;
        }

        if (head.Is("extern") && start + 1 < _statement.Count && _statement[start + 1].Kind == TokenKind.String)
        {
            _frames.Push(new Frame(FrameKind.Transparent, CurrentScope));
            EndStatement(_tokens[index].Line);
            return index;
        }

        if ((head.Is("class") || head.Is("struct") || head.Is("union")) && !HasTopLevelParen(_statement, start))
        {
            // An anonymous struct or union inside a class puts its members in that class.
            var name = ClassName(_statement, start + 1);
            _frames.Push(new Frame(FrameKind.Class, name.Length == 0 ? CurrentScope : Combine(CurrentScope, name)));
            EndStatement(_tokens[index].Line);
            return index;
        }

        var close = MatchingBrace(index);
        if (HasTopLevelParen(_statement, start) && !head.Is("enum") && !IsInitializer(_statement, start))
        {
            RecordDefinition(start, index, close);
            EndStatement(_tokens[Math.Min(close, _tokens.Count - 1)].Line);
            return close;
        }

        // An enum body or a braced initializer: skip it, the statement goes on to its ';'.
        return close;
    }

    private void RecordDefinition(int start, int open, int close)
    {
        if (!TryReadSignature(_statement, start, out var qualifier, out var name, out var parameters, out var isConst))
        {
            return;
        }

        var scope = CurrentScope;
        string className;
        if (qualifier.Length > 0)
        {
            className = Combine(scope, qualifier);
        }
        else if (InClass)
        {
            className = scope;
        }
        else
        {
            return;
        }

        var body = _tokens.GetRange(open + 1, Math.Max(0, close - open - 1)).Where(t => t.Kind != TokenKind.LineComment).ToList();
        var record = new FunctionBody(_file, _statement[start].Line, className, name, isConst, parameters, body, _pendingUnused);
        _result.Bodies.Add(record);
        var index = _result.Bodies.Count - 1;
        _markLastUnused = () => _result.Bodies[index] = _result.Bodies[index] with { IsUnusedMarked = true };
    }

    private void OnDeclarationEnd()
    {
        _markLastUnused = null;
        var start = SkipPrefixes(_statement);
        if (!InClass || !HasTopLevelParen(_statement, start) || _statement.Any(t => t.Is("friend") || t.Is("typedef")))
        {
            return;
        }

        if (!TryReadSignature(_statement, start, out var qualifier, out var name, out _, out _) || qualifier.Length > 0)
        {
            return;
        }

        var declaration = new UnusedDeclaration(_file, _statement[start].Line, CurrentScope, name);
        if (_pendingUnused)
        {
            _result.UnusedDeclarations.Add(declaration);
        }
        else
        {
            _markLastUnused = () => _result.UnusedDeclarations.Add(declaration);
        }
    }

    /// <summary>Skips access specifiers ("public:") and template headers ("template &lt;typename T&gt;").</summary>
    private static int SkipPrefixes(List<HeaderToken> statement)
    {
        var i = 0;
        while (i < statement.Count)
        {
            if (statement[i].Text is "public" or "private" or "protected" && i + 1 < statement.Count && statement[i + 1].Is(":"))
            {
                i += 2;
                continue;
            }

            if (statement[i].Is("template") && i + 1 < statement.Count && statement[i + 1].Is("<"))
            {
                var depth = 0;
                i++;
                do
                {
                    depth += statement[i].Is("<") ? 1 : statement[i].Is(">") ? -1 : 0;
                    i++;
                }
                while (i < statement.Count && depth > 0);
                continue;
            }

            break;
        }

        return i;
    }

    private static string ClassName(List<HeaderToken> statement, int index)
    {
        var parts = new List<string>();
        for (var i = index; i < statement.Count; i++)
        {
            var token = statement[i];
            if (token.Is(":") || token.Is("<") || token.Is("{"))
            {
                break;
            }

            if (token.Kind == TokenKind.Identifier && token.Text is not ("final" or "__attribute__"))
            {
                parts.Add(token.Text);
            }
            else if (!token.Is("::"))
            {
                break;
            }
        }

        return string.Join("::", parts);
    }

    /// <summary>"T x[] = { ... }": an '=' before the first parenthesis.</summary>
    private static bool IsInitializer(List<HeaderToken> statement, int start)
    {
        for (var i = start; i < statement.Count && !statement[i].Is("("); i++)
        {
            if (statement[i].Is("="))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasTopLevelParen(List<HeaderToken> statement, int start)
    {
        for (var i = start; i < statement.Count; i++)
        {
            if (statement[i].Is("("))
            {
                return true;
            }
        }

        return false;
    }

    private int MatchingBrace(int open)
    {
        var depth = 0;
        for (var i = open; i < _tokens.Count; i++)
        {
            if (_tokens[i].Is("{"))
            {
                depth++;
            }
            else if (_tokens[i].Is("}") && --depth == 0)
            {
                return i;
            }
        }

        return _tokens.Count;
    }

    /// <summary>
    /// Reads "Outer::TFoo::name(T a, U b) const" from a statement: the identifier in front of
    /// the first parenthesis, its qualifier, the parameter names and the const qualifier.
    /// </summary>
    private static bool TryReadSignature(
        List<HeaderToken> statement, int start, out string qualifier, out string name, out List<string> parameters, out bool isConst)
    {
        qualifier = "";
        name = "";
        parameters = [];
        isConst = false;

        var paren = -1;
        for (var i = start; i < statement.Count; i++)
        {
            if (statement[i].Is("("))
            {
                paren = i;
                break;
            }
        }

        if (paren <= start || statement[paren - 1].Kind != TokenKind.Identifier || statement[paren - 1].Is("operator"))
        {
            return false;
        }

        name = statement[paren - 1].Text;
        var cursor = paren - 2;
        if (cursor >= start && statement[cursor].Is("~"))
        {
            name = "~" + name;
            cursor--;
        }

        var scopes = new List<string>();
        while (cursor - 1 >= start && statement[cursor].Is("::") && statement[cursor - 1].Kind == TokenKind.Identifier)
        {
            scopes.Insert(0, statement[cursor - 1].Text);
            cursor -= 2;
        }

        if (cursor >= start && statement[cursor].Is("::"))
        {
            // A template qualifier such as "TBox<T>::get": not handled.
            return false;
        }

        qualifier = string.Join("::", scopes);

        var depth = 0;
        var piece = new List<HeaderToken>();
        var end = paren;
        for (var i = paren + 1; i < statement.Count; i++)
        {
            var token = statement[i];
            if (depth == 0 && (token.Is(",") || token.Is(")")))
            {
                if (ParameterName(piece) is { } parameter)
                {
                    parameters.Add(parameter);
                }

                piece.Clear();
                if (token.Is(")"))
                {
                    end = i;
                    break;
                }

                continue;
            }

            depth += token.Is("(") || token.Is("<") || token.Is("[") ? 1 : token.Is(")") || token.Is(">") || token.Is("]") ? -1 : 0;
            piece.Add(token);
        }

        isConst = end + 1 < statement.Count && statement[end + 1].Is("const");
        return true;
    }

    private static string? ParameterName(List<HeaderToken> piece)
    {
        var end = piece.FindIndex(t => t.Is("=") || t.Is("["));
        var relevant = end < 0 ? piece : piece.GetRange(0, end);
        if (relevant.Count < 2)
        {
            return null;
        }

        var last = relevant[^1];
        return last.Kind == TokenKind.Identifier && !TypeWords.Contains(last.Text) ? last.Text : null;
    }
}
