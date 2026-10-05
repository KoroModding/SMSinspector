namespace SMSinspector.Core.Layouts;

/// <summary>
/// A focused parser for the decomp's headers. It is not a C++ parser: it tracks
/// namespaces, class definitions with their bases, data members with their offset
/// comments, enums, typedefs and simple constants, and skips everything else (function
/// bodies, initializers, macros) by matching brackets.
/// </summary>
public sealed class HeaderParser
{
    private static readonly HashSet<string> BuiltinWords = new(StringComparer.Ordinal)
    {
        "unsigned", "signed", "short", "long", "int", "char", "float", "double", "bool", "void", "wchar_t", "__int64",
    };

    private static readonly HashSet<string> IgnoredSpecifiers = new(StringComparer.Ordinal)
    {
        "mutable", "volatile", "register", "inline", "explicit", "extern", "__restrict", "restrict", "typename",
    };

    private readonly List<HeaderToken> _all;
    private readonly List<int> _code = [];
    private readonly HeaderFile _file;
    private int _pos;
    private int _commentsScannedUpTo = -1;

    private HeaderParser(string path, IReadOnlyList<HeaderToken> tokens)
    {
        _all = [.. tokens];
        for (var i = 0; i < _all.Count; i++)
        {
            if (_all[i].Kind is not (TokenKind.OffsetComment or TokenKind.LineComment))
            {
                _code.Add(i);
            }
        }

        _file = new HeaderFile { Path = path };
    }

    public static HeaderFile Parse(string path, string source)
    {
        var lexed = HeaderLexer.Tokenize(source);
        var parser = new HeaderParser(path, lexed.Tokens);
        parser._file.Macros.AddRange(lexed.Macros);
        parser._file.Problems.AddRange(lexed.Warnings);
        parser.ParseDeclarations("", null, untilBrace: false);
        return parser._file;
    }

    private bool AtEnd => _pos >= _code.Count;

    private HeaderToken Cur => Peek(0);

    private HeaderToken Peek(int ahead) =>
        _pos + ahead < _code.Count ? _all[_code[_pos + ahead]] : new HeaderToken(TokenKind.Punct, "", -1, VersionMask.Both);

    private static string Combine(string scope, string name) => scope.Length == 0 ? name : $"{scope}::{name}";

    private void ParseDeclarations(string scope, ClassDecl? owner, bool untilBrace)
    {
        while (!AtEnd)
        {
            var offset = owner is null ? null : TakeLeadingComments(owner);
            if (untilBrace && Cur.Is("}"))
            {
                return;
            }

            var before = _pos;
            ParseDeclaration(scope, owner, offset);
            if (_pos == before)
            {
                // A stray token the rules above do not cover; never loop on it.
                _pos++;
            }
        }
    }

    private void ParseDeclaration(string scope, ClassDecl? owner, uint? offset)
    {
        var token = Cur;

        if (token.Is(";"))
        {
            _pos++;
            return;
        }

        if (owner is not null && token.Text is "public" or "private" or "protected" && Peek(1).Is(":"))
        {
            _pos += 2;
            return;
        }

        switch (token.Text)
        {
            case "namespace" when token.Kind == TokenKind.Identifier:
                ParseNamespace(scope);
                return;
            case "extern" when Peek(1).Kind == TokenKind.String && Peek(2).Is("{"):
                _pos += 3;
                ParseDeclarations(scope, owner, untilBrace: true);
                Expect("}");
                return;
            case "template":
                ParseTemplate(scope, owner, offset);
                return;
            case "class" or "struct" or "union":
                ParseClass(scope, owner, offset, templateParams: [], isSpecialization: false);
                return;
            case "enum":
                ParseEnum(scope, owner, offset);
                return;
            case "typedef":
                ParseTypedef(scope);
                return;
            case "using":
                ParseUsing(scope);
                return;
            case "friend" or "static_assert":
                SkipStatement();
                return;
            case "static" when owner is not null:
                ParseStatic(owner);
                return;
        }

        if (owner is not null)
        {
            ParseMemberStatement(owner, offset);
        }
        else
        {
            SkipStatement();
        }
    }

    /// <summary>
    /// Reads the comments in front of the next statement. A line holding only an offset
    /// and a <c>//</c> comment becomes a marker member; the last plain offset comment is
    /// returned for the statement that follows.
    /// </summary>
    private uint? TakeLeadingComments(ClassDecl owner)
    {
        if (AtEnd || _pos <= _commentsScannedUpTo)
        {
            return null;
        }

        _commentsScannedUpTo = _pos;
        var from = _pos == 0 ? 0 : _code[_pos - 1] + 1;
        var to = _code[_pos];
        uint? offset = null;

        for (var i = from; i < to; i++)
        {
            var token = _all[i];
            if (token.Kind != TokenKind.OffsetComment)
            {
                continue;
            }

            if (i + 1 < to && _all[i + 1].Kind == TokenKind.LineComment && _all[i + 1].Line == token.Line)
            {
                var text = _all[i + 1].Text;
                var isVtable = text.StartsWith("vt", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith("vptr", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith("__vt", StringComparison.OrdinalIgnoreCase);
                owner.Members.Add(new MemberDecl(
                    text, new TypeSpec("void", [], PointerDepth: 1), token.Offset, token.Versions, token.Line,
                    isVtable ? MemberKind.VtableMarker : MemberKind.OtherMarker, text));
                continue;
            }

            offset = token.Offset;
        }

        return offset;
    }

    private void ParseNamespace(string scope)
    {
        _pos++;
        var name = "";
        while (Cur.Kind == TokenKind.Identifier || Cur.Is("::"))
        {
            name += Cur.Text;
            _pos++;
        }

        if (!Cur.Is("{"))
        {
            SkipStatement();
            return;
        }

        _pos++;
        ParseDeclarations(name.Length == 0 ? scope : Combine(scope, name), null, untilBrace: true);
        Expect("}");
    }

    private void ParseTemplate(string scope, ClassDecl? owner, uint? offset)
    {
        _pos++;
        var parameters = new List<string>();
        var defaults = new List<TemplateArg?>();
        if (Cur.Is("<"))
        {
            var args = ReadAngleGroup();
            foreach (var arg in args)
            {
                // "typename T", "class T", "int N = 4": the name is the last identifier before any default.
                var end = arg.FindIndex(t => t.Is("="));
                var head = end < 0 ? arg : arg[..end];
                var name = head.LastOrDefault(t => t.Kind == TokenKind.Identifier);
                if (name.Text is not null and not "typename" and not "class")
                {
                    parameters.Add(name.Text);
                    defaults.Add(end < 0 ? null : ParseTemplateArg(arg[(end + 1)..]));
                }
            }
        }

        if (Cur.Text is "class" or "struct" or "union")
        {
            ParseClass(scope, owner, offset, parameters, isSpecialization: parameters.Count == 0, defaults);
            return;
        }

        // Function templates and member templates have no layout.
        SkipStatement();
    }

    private ClassDecl? ParseClass(string scope, ClassDecl? owner, uint? offset, List<string> templateParams, bool isSpecialization, List<TemplateArg?>? templateDefaults = null)
    {
        var start = _pos;
        var keyword = Cur;
        _pos++;
        SkipAttributes();

        // "Name" or "Outer::Name"; in "struct Foo foo;" the second identifier is the member.
        var name = "";
        if (Cur.Kind == TokenKind.Identifier && Cur.Text != "final")
        {
            name = Cur.Text;
            _pos++;
            while (Cur.Is("::") && Peek(1).Kind == TokenKind.Identifier)
            {
                name += "::" + Peek(1).Text;
                _pos += 2;
            }
        }

        IReadOnlyList<TemplateArg>? specializationArgs = null;
        if (Cur.Is("<"))
        {
            specializationArgs = ReadAngleGroup().Select(ParseTemplateArg).ToList();
        }

        SkipAttributes();
        if (Cur.Is("final"))
        {
            _pos++;
        }

        if (Cur.Is(";"))
        {
            _pos++;
            return null;
        }

        if (!Cur.Is(":") && !Cur.Is("{"))
        {
            // An elaborated type in a declaration ("struct Foo* mFoo;"), not a definition.
            _pos = start;
            if (owner is not null)
            {
                ParseMemberStatement(owner, offset);
            }
            else
            {
                SkipStatement();
            }

            return null;
        }

        var anonymous = name.Length == 0;
        if (anonymous)
        {
            name = $"<anonymous@{keyword.Line}>";
        }

        var decl = new ClassDecl
        {
            Name = name.Contains("::") ? name[(name.LastIndexOf("::", StringComparison.Ordinal) + 2)..] : name,
            QualifiedName = Combine(scope, name),
            Scope = scope,
            Kind = keyword.Text switch { "struct" => ClassKind.Struct, "union" => ClassKind.Union, _ => ClassKind.Class },
            File = _file.Path,
            Line = keyword.Line,
            Versions = keyword.Versions,
            TemplateParams = templateParams,
            TemplateParamDefaults = templateDefaults ?? [],
            SpecializationArgs = specializationArgs ?? (isSpecialization ? [] : null),
            IsAnonymous = anonymous,
        };

        if (Cur.Is(":"))
        {
            _pos++;
            ParseBases(decl);
        }

        if (!Cur.Is("{"))
        {
            SkipStatement();
            return null;
        }

        _pos++;
        _file.Classes.Add(decl);
        ParseDeclarations(decl.QualifiedName, decl, untilBrace: true);
        Expect("}");

        // Declarators after the closing brace: "} mData;" declares a member of that type.
        if (owner is not null && !Cur.Is(";"))
        {
            var statement = ReadStatementTokens(out _);
            ParseDeclarators(owner, new TypeSpec(decl.QualifiedName, []), statement, 0, offset, keyword.Versions, keyword.Line);
        }
        else if (owner is not null && anonymous)
        {
            // An anonymous union or struct without declarator puts its members in the owner.
            owner.Members.Add(new MemberDecl("", new TypeSpec(decl.QualifiedName, []), offset, keyword.Versions, keyword.Line));
            Expect(";");
        }
        else if (!Cur.Is(";"))
        {
            SkipStatement();
        }
        else
        {
            _pos++;
        }

        return decl;
    }

    private void ParseBases(ClassDecl decl)
    {
        while (!AtEnd && !Cur.Is("{") && !Cur.Is(";"))
        {
            var isVirtual = false;
            while (Cur.Text is "public" or "private" or "protected" or "virtual")
            {
                isVirtual |= Cur.Text == "virtual";
                _pos++;
            }

            var tokens = new List<HeaderToken>();
            var depth = 0;
            while (!AtEnd && !(depth == 0 && (Cur.Is(",") || Cur.Is("{") || Cur.Is(";"))))
            {
                depth += Cur.Is("<") ? 1 : Cur.Is(">") ? -1 : 0;
                tokens.Add(Cur);
                _pos++;
            }

            var index = 0;
            if (TryParseTypeName(tokens, ref index, out var type) && index == tokens.Count)
            {
                decl.Bases.Add(new BaseSpec(type, isVirtual));
            }
            else
            {
                _file.Problems.Add($"{decl.QualifiedName}: could not read base '{string.Join(" ", tokens)}' (line {decl.Line}).");
            }

            if (Cur.Is(","))
            {
                _pos++;
            }
        }
    }

    private EnumDecl? ParseEnum(string scope, ClassDecl? owner, uint? offset, bool declaratorsAllowed = true)
    {
        var keyword = Cur;
        _pos++;
        if (Cur.Text is "class" or "struct")
        {
            _pos++;
        }

        var name = Cur.Kind == TokenKind.Identifier ? Cur.Text : null;
        if (name is not null)
        {
            _pos++;
        }

        TypeSpec? underlying = null;
        if (Cur.Is(":"))
        {
            _pos++;
            var tokens = new List<HeaderToken>();
            while (!AtEnd && !Cur.Is("{") && !Cur.Is(";"))
            {
                tokens.Add(Cur);
                _pos++;
            }

            var index = 0;
            if (TryParseTypeName(tokens, ref index, out var type))
            {
                underlying = type;
            }
        }

        if (!Cur.Is("{"))
        {
            // "enum Foo foo;" or a forward declaration.
            if (owner is not null && declaratorsAllowed && !Cur.Is(";"))
            {
                var statement = ReadStatementTokens(out _);
                ParseDeclarators(owner, new TypeSpec(Combine(scope, name ?? "int"), []), statement, 0, offset, keyword.Versions, keyword.Line);
            }
            else
            {
                SkipStatement();
            }

            return null;
        }

        _pos++;
        var enumerators = new List<Enumerator>();
        while (!AtEnd && !Cur.Is("}"))
        {
            var item = Cur;
            if (item.Kind != TokenKind.Identifier)
            {
                _pos++;
                continue;
            }

            _pos++;
            string? expression = null;
            if (Cur.Is("="))
            {
                _pos++;
                var tokens = new List<HeaderToken>();
                var depth = 0;
                while (!AtEnd && !(depth == 0 && (Cur.Is(",") || Cur.Is("}"))))
                {
                    depth += Cur.Is("(") ? 1 : Cur.Is(")") ? -1 : 0;
                    tokens.Add(Cur);
                    _pos++;
                }

                expression = Join(tokens);
            }

            enumerators.Add(new Enumerator(item.Text, expression, item.Versions));
            if (Cur.Is(","))
            {
                _pos++;
            }
        }

        Expect("}");
        var qualified = Combine(scope, name ?? $"<enum@{keyword.Line}>");
        var decl = new EnumDecl(qualified, scope, underlying, enumerators, _file.Path, keyword.Line);
        _file.Enums.Add(decl);

        if (Cur.Is(";"))
        {
            _pos++;
        }
        else if (owner is not null && declaratorsAllowed)
        {
            var statement = ReadStatementTokens(out _);
            ParseDeclarators(owner, new TypeSpec(qualified, []), statement, 0, offset, keyword.Versions, keyword.Line);
        }

        return decl;
    }

    private void ParseTypedef(string scope)
    {
        var keyword = Cur;
        _pos++;

        TypeSpec? baseType = null;
        if (Cur.Text is "struct" or "class" or "union" && LooksLikeDefinition())
        {
            var decl = ParseClassForTypedef(scope);
            if (decl is not null)
            {
                baseType = new TypeSpec(decl.QualifiedName, []);
            }
        }
        else if (Cur.Is("enum") && LooksLikeDefinition())
        {
            var decl = ParseEnum(scope, null, null, declaratorsAllowed: false);
            baseType = new TypeSpec(decl?.QualifiedName ?? "int", []);
        }

        var statement = ReadStatementTokens(out _);
        var index = 0;
        if (baseType is null && !TryParseTypeName(statement, ref index, out baseType))
        {
            return;
        }

        foreach (var (name, type, _, _) in ReadDeclarators(baseType!, statement, index))
        {
            _file.Typedefs.Add(new TypedefDecl(Combine(scope, name), scope, type, keyword.Versions, _file.Path, keyword.Line));
        }
    }

    /// <summary>True when the struct/enum keyword at the cursor opens a body before the statement ends.</summary>
    private bool LooksLikeDefinition()
    {
        for (var i = 1; _pos + i < _code.Count; i++)
        {
            var token = Peek(i);
            if (token.Is("{"))
            {
                return true;
            }

            if (token.Is(";") || token.Is("(") || token.Is("*"))
            {
                return false;
            }
        }

        return false;
    }

    private ClassDecl? ParseClassForTypedef(string scope)
    {
        // Parse the definition as a namespace-level class, then rewind to its declarators.
        var keyword = Cur;
        _pos++;
        var name = Cur.Kind == TokenKind.Identifier ? Cur.Text : "";
        if (name.Length > 0)
        {
            _pos++;
        }

        if (Cur.Is(":"))
        {
            // Rare in typedefs; let the normal path handle it.
            return null;
        }

        if (!Cur.Is("{"))
        {
            return null;
        }

        _pos++;
        var decl = new ClassDecl
        {
            Name = name.Length > 0 ? name : $"<anonymous@{keyword.Line}>",
            QualifiedName = Combine(scope, name.Length > 0 ? name : $"<anonymous@{keyword.Line}>"),
            Scope = scope,
            Kind = keyword.Text switch { "struct" => ClassKind.Struct, "union" => ClassKind.Union, _ => ClassKind.Class },
            File = _file.Path,
            Line = keyword.Line,
            Versions = keyword.Versions,
            IsAnonymous = name.Length == 0,
        };
        _file.Classes.Add(decl);
        ParseDeclarations(decl.QualifiedName, decl, untilBrace: true);
        Expect("}");
        return decl;
    }

    private void ParseUsing(string scope)
    {
        var keyword = Cur;
        if (Peek(1).Kind == TokenKind.Identifier && Peek(2).Is("="))
        {
            var name = Peek(1).Text;
            _pos += 3;
            var statement = ReadStatementTokens(out _);
            var index = 0;
            if (TryParseTypeName(statement, ref index, out var type))
            {
                var dims = ReadDims(statement, ref index);
                _file.Typedefs.Add(new TypedefDecl(Combine(scope, name), scope, type with { ArrayDims = dims }, keyword.Versions, _file.Path, keyword.Line));
            }

            return;
        }

        if (!Peek(1).Is("namespace"))
        {
            // "using std::uintptr_t;" brings a name into this scope.
            _pos++;
            var statement = ReadStatementTokens(out _);
            var index = 0;
            if (TryParseTypeName(statement, ref index, out var target) && index == statement.Count && target.Name.Contains("::"))
            {
                var alias = TypeCatalog.LastPart(target.Name);
                _file.Typedefs.Add(new TypedefDecl(Combine(scope, alias), scope, target, keyword.Versions, _file.Path, keyword.Line));
            }

            return;
        }

        SkipStatement();
    }

    private void ParseStatic(ClassDecl owner)
    {
        var statement = ReadStatementTokens(out var hadBody);
        if (hadBody)
        {
            return;
        }

        // "static const int N = 4;" can size arrays later on.
        var equals = statement.FindIndex(t => t.Is("="));
        if (equals > 1 && statement.Any(t => t.Is("const")) && statement[equals - 1].Kind == TokenKind.Identifier)
        {
            _file.Constants.Add(new ConstantDecl(
                Combine(owner.QualifiedName, statement[equals - 1].Text), Join(statement.Skip(equals + 1)), statement[0].Versions));
        }
    }

    private void ParseMemberStatement(ClassDecl owner, uint? offset)
    {
        var first = Cur;
        var statement = ReadStatementTokens(out var hadBody);
        if (statement.Count == 0)
        {
            return;
        }

        // "const static T x[];" and the like: static members take no room in the object.
        if (statement.Any(t => t.Is("static")))
        {
            return;
        }

        var isFunction = hadBody
            || statement.Any(t => t.Is("virtual") || t.Is("operator") || t.Is("~"))
            || (HasTopLevelParen(statement) && !TryParseDataMember(owner, statement, offset, first, dryRun: true));

        if (isFunction)
        {
            if (statement.Any(t => t.Is("virtual")))
            {
                owner.DeclaresVirtual = true;
                owner.FirstVirtualAfterMembers ??= owner.Members.Count;
            }

            return;
        }

        if (!TryParseDataMember(owner, statement, offset, first, dryRun: false))
        {
            _file.Problems.Add($"{owner.QualifiedName}: could not read member '{Join(statement)}' (line {first.Line}).");
        }
    }

    private bool TryParseDataMember(ClassDecl owner, List<HeaderToken> statement, uint? offset, HeaderToken first, bool dryRun)
    {
        var index = 0;
        if (!TryParseTypeName(statement, ref index, out var baseType))
        {
            return false;
        }

        var declarators = ReadDeclarators(baseType, statement, index);
        if (declarators.Count == 0)
        {
            return false;
        }

        if (!dryRun)
        {
            var versions = statement.Aggregate(VersionMask.Both, (mask, t) => mask & t.Versions);
            foreach (var (name, type, bitWidth, alignAttribute) in declarators)
            {
                owner.Members.Add(new MemberDecl(name, type, offset, versions, first.Line, BitWidth: bitWidth, AlignAttribute: alignAttribute));
                offset = null;
            }
        }

        return true;
    }

    private void ParseDeclarators(ClassDecl owner, TypeSpec baseType, List<HeaderToken> tokens, int index, uint? offset, VersionMask versions, int line)
    {
        foreach (var (name, type, bitWidth, alignAttribute) in ReadDeclarators(baseType, tokens, index))
        {
            owner.Members.Add(new MemberDecl(name, type, offset, versions, line, BitWidth: bitWidth, AlignAttribute: alignAttribute));
            offset = null;
        }
    }

    /// <summary>
    /// Reads "name", "*name[4]", "(*name)(int)", "(*name)[3]" separated by commas, after the
    /// type. Returns nothing when the tokens are not a data declaration (a function, say).
    /// </summary>
    private static List<(string Name, TypeSpec Type, string? BitWidth, uint? Align)> ReadDeclarators(TypeSpec baseType, List<HeaderToken> tokens, int index)
    {
        var result = new List<(string, TypeSpec, string?, uint?)>();
        while (index < tokens.Count)
        {
            var pointers = 0;
            var isReference = false;
            while (index < tokens.Count && (tokens[index].Is("*") || tokens[index].Is("&") || tokens[index].Is("const")
                || tokens[index].Is("volatile") || tokens[index].Is("__restrict")))
            {
                pointers += tokens[index].Is("*") ? 1 : 0;
                isReference |= tokens[index].Is("&");
                index++;
            }

            if (index >= tokens.Count)
            {
                return [];
            }

            string name;
            var isFunctionPointer = false;
            IReadOnlyList<string> dims;

            if (tokens[index].Is("("))
            {
                // Parenthesized declarator: pointer to function or pointer to array.
                index++;
                var innerPointers = 0;
                while (index < tokens.Count && (tokens[index].Is("*") || tokens[index].Is("&") || tokens[index].Is("const")))
                {
                    innerPointers += tokens[index].Is("*") || tokens[index].Is("&") ? 1 : 0;
                    index++;
                }

                if (innerPointers == 0 || index >= tokens.Count || tokens[index].Kind != TokenKind.Identifier)
                {
                    return [];
                }

                name = tokens[index++].Text;
                dims = ReadDims(tokens, ref index);
                if (index >= tokens.Count || !tokens[index].Is(")"))
                {
                    return [];
                }

                index++;
                if (index < tokens.Count && tokens[index].Is("("))
                {
                    isFunctionPointer = true;
                    SkipGroup(tokens, ref index, "(", ")");
                }
                else
                {
                    // The dimensions after ")" belong to the pointee.
                    ReadDims(tokens, ref index);
                }

                pointers += innerPointers;
            }
            else
            {
                if (tokens[index].Kind != TokenKind.Identifier)
                {
                    return [];
                }

                name = tokens[index++].Text;
                if (index < tokens.Count && tokens[index].Is("("))
                {
                    return [];
                }

                dims = ReadDims(tokens, ref index);
            }

            // Bit-field width or default value: read or skip up to the next declarator.
            var alignAttribute = ReadAlignAttribute(tokens, ref index);
            string? bitWidth = null;
            if (index < tokens.Count && (tokens[index].Is(":") || tokens[index].Is("=")))
            {
                var isBitField = tokens[index].Is(":");
                var widthStart = index + 1;
                var depth = 0;
                while (index < tokens.Count && !(depth == 0 && tokens[index].Is(",")))
                {
                    depth += tokens[index].Text is "(" or "{" or "[" ? 1 : tokens[index].Text is ")" or "}" or "]" ? -1 : 0;
                    index++;
                }

                if (isBitField)
                {
                    bitWidth = Join(tokens.Skip(widthStart).Take(index - widthStart));
                }
            }

            result.Add((name, baseType with
            {
                PointerDepth = baseType.PointerDepth + (isFunctionPointer ? 0 : pointers),
                IsReference = baseType.IsReference || isReference,
                IsFunctionPointer = isFunctionPointer,
                ArrayDims = dims.Count > 0 ? dims : null,
            }, bitWidth, alignAttribute));

            if (index < tokens.Count)
            {
                if (!tokens[index].Is(","))
                {
                    return [];
                }

                index++;
            }
        }

        return result;
    }

    /// <summary>Reads <c>__attribute__((aligned(N)))</c> after a declarator, if present.</summary>
    private static uint? ReadAlignAttribute(List<HeaderToken> tokens, ref int index)
    {
        uint? align = null;
        while (index < tokens.Count && tokens[index].Is("__attribute__"))
        {
            index++;
            if (index >= tokens.Count || !tokens[index].Is("("))
            {
                break;
            }

            var start = index;
            SkipGroup(tokens, ref index, "(", ")");
            for (var i = start; i + 2 < index; i++)
            {
                if (tokens[i].Is("aligned") && tokens[i + 1].Is("(") && tokens[i + 2].Kind == TokenKind.Number
                    && ConstantExpression.TryEvaluate(tokens[i + 2].Text, _ => null, out var value) && value > 0)
                {
                    align = (uint)value;
                }
            }
        }

        return align;
    }

    private static IReadOnlyList<string> ReadDims(List<HeaderToken> tokens, ref int index)
    {
        var dims = new List<string>();
        while (index < tokens.Count && tokens[index].Is("["))
        {
            var start = index + 1;
            SkipGroup(tokens, ref index, "[", "]");
            dims.Add(Join(tokens.Skip(start).Take(index - start - 1)));
        }

        return dims;
    }

    private static void SkipGroup(List<HeaderToken> tokens, ref int index, string open, string close)
    {
        var depth = 0;
        do
        {
            depth += tokens[index].Is(open) ? 1 : tokens[index].Is(close) ? -1 : 0;
            index++;
        }
        while (index < tokens.Count && depth > 0);
    }

    /// <summary>
    /// Reads a type name: builtin words ("unsigned char"), or a qualified name with
    /// template arguments, plus const and trailing pointers belonging to the type itself.
    /// </summary>
    private static bool TryParseTypeName(List<HeaderToken> tokens, ref int index, out TypeSpec type)
    {
        type = new TypeSpec("", []);
        var isConst = false;
        while (index < tokens.Count && (IgnoredSpecifiers.Contains(tokens[index].Text) || tokens[index].Is("const")
            || tokens[index].Text is "struct" or "class" or "union" or "enum" || tokens[index].Is("__attribute__")))
        {
            if (tokens[index].Is("__attribute__"))
            {
                index++;
                if (index < tokens.Count && tokens[index].Is("("))
                {
                    SkipGroup(tokens, ref index, "(", ")");
                }

                continue;
            }

            isConst |= tokens[index].Is("const");
            index++;
        }

        if (index >= tokens.Count)
        {
            return false;
        }

        string name;
        var args = new List<TemplateArg>();

        if (BuiltinWords.Contains(tokens[index].Text))
        {
            var words = new List<string>();
            while (index < tokens.Count && (BuiltinWords.Contains(tokens[index].Text) || tokens[index].Is("const")))
            {
                if (tokens[index].Is("const"))
                {
                    isConst = true;
                }
                else
                {
                    words.Add(tokens[index].Text);
                }

                index++;
            }

            name = NormalizeBuiltin(words);
        }
        else if (tokens[index].Kind == TokenKind.Identifier || tokens[index].Is("::"))
        {
            name = "";
            if (tokens[index].Is("::"))
            {
                index++;
            }

            while (true)
            {
                if (index >= tokens.Count || tokens[index].Kind != TokenKind.Identifier)
                {
                    return false;
                }

                name += tokens[index++].Text;
                if (index < tokens.Count && tokens[index].Is("<"))
                {
                    var group = ReadAngleGroup(tokens, ref index);
                    args = group.Select(ParseTemplateArg).ToList();
                }

                if (index < tokens.Count && tokens[index].Is("::"))
                {
                    // Template arguments on an outer scope ("TOuter<T>::TInner") are kept in the name.
                    if (args.Count > 0)
                    {
                        name += $"<{string.Join(", ", args)}>";
                        args = [];
                    }

                    name += "::";
                    index++;
                    continue;
                }

                break;
            }
        }
        else
        {
            return false;
        }

        while (index < tokens.Count && (tokens[index].Is("const") || tokens[index].Is("volatile")))
        {
            isConst |= tokens[index].Is("const");
            index++;
        }

        type = new TypeSpec(name, args, IsConst: isConst);
        return true;
    }

    private static string NormalizeBuiltin(List<string> words)
    {
        var unsigned = words.Remove("unsigned");
        var signed = words.Remove("signed");
        var longs = words.RemoveAll(w => w == "long");
        words.Remove("int");
        var core = words.Count > 0 ? words[0] : longs > 0 ? "" : "int";
        var name = (longs, core) switch
        {
            (2, _) => "long long",
            (1, "double") => "long double",
            (1, _) => "long",
            _ => core,
        };

        if (unsigned)
        {
            return $"unsigned {name}";
        }

        return signed && name == "char" ? "signed char" : name;
    }

    private static TemplateArg ParseTemplateArg(List<HeaderToken> tokens)
    {
        var index = 0;
        if (TryParseTypeName(tokens, ref index, out var type))
        {
            var pointers = 0;
            var isReference = false;
            while (index < tokens.Count && (tokens[index].Is("*") || tokens[index].Is("&") || tokens[index].Is("const")))
            {
                pointers += tokens[index].Is("*") ? 1 : 0;
                isReference |= tokens[index].Is("&");
                index++;
            }

            if (index == tokens.Count)
            {
                var spec = type with { PointerDepth = pointers, IsReference = isReference };
                return new TemplateArg(spec, spec.ToString());
            }
        }

        return new TemplateArg(null, Join(tokens));
    }

    private List<List<HeaderToken>> ReadAngleGroup()
    {
        var tokens = new List<HeaderToken>();
        var start = _pos;
        var depth = 0;
        do
        {
            depth += Cur.Is("<") ? 1 : Cur.Is(">") ? -1 : 0;
            tokens.Add(Cur);
            _pos++;
        }
        while (!AtEnd && depth > 0);

        var index = 0;
        var group = ReadAngleGroup(tokens, ref index);
        _pos = start + index;
        return group;
    }

    /// <summary>Reads "&lt;a, b&lt;c&gt;&gt;" and returns the top-level arguments.</summary>
    private static List<List<HeaderToken>> ReadAngleGroup(List<HeaderToken> tokens, ref int index)
    {
        var args = new List<List<HeaderToken>>();
        var current = new List<HeaderToken>();
        var depth = 0;
        var parens = 0;

        for (; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Is("(") || token.Is("["))
            {
                parens++;
            }
            else if (token.Is(")") || token.Is("]"))
            {
                parens--;
            }
            else if (parens == 0 && token.Is("<"))
            {
                depth++;
                if (depth == 1)
                {
                    continue;
                }
            }
            else if (parens == 0 && token.Is(">"))
            {
                depth--;
                if (depth == 0)
                {
                    index++;
                    if (current.Count > 0)
                    {
                        args.Add(current);
                    }

                    return args;
                }
            }
            else if (parens == 0 && depth == 1 && token.Is(","))
            {
                args.Add(current);
                current = [];
                continue;
            }

            current.Add(token);
        }

        return args;
    }

    /// <summary>
    /// Reads tokens up to the end of the statement and consumes it. A function body or a
    /// braced initializer is skipped; <paramref name="hadBody"/> tells whether a body
    /// followed a parameter list.
    /// </summary>
    private List<HeaderToken> ReadStatementTokens(out bool hadBody)
    {
        var tokens = new List<HeaderToken>();
        hadBody = false;
        var depth = 0;

        while (!AtEnd)
        {
            var token = Cur;
            if (depth == 0 && token.Is(";"))
            {
                _pos++;
                return tokens;
            }

            if (depth == 0 && token.Is("}"))
            {
                return tokens;
            }

            if (depth == 0 && token.Is("{"))
            {
                var isBody = HasTopLevelParen(tokens);
                SkipBraces();
                if (isBody)
                {
                    hadBody = true;
                    if (Cur.Is(";"))
                    {
                        _pos++;
                    }

                    return tokens;
                }

                continue;
            }

            depth += token.Is("(") || token.Is("[") ? 1 : token.Is(")") || token.Is("]") ? -1 : 0;
            tokens.Add(token);
            _pos++;
        }

        return tokens;
    }

    private void SkipStatement() => ReadStatementTokens(out _);

    private void SkipBraces()
    {
        var depth = 0;
        do
        {
            depth += Cur.Is("{") ? 1 : Cur.Is("}") ? -1 : 0;
            _pos++;
        }
        while (!AtEnd && depth > 0);
    }

    private void SkipAttributes()
    {
        while (Cur.Is("__attribute__") || Cur.Is("__declspec"))
        {
            _pos++;
            if (Cur.Is("("))
            {
                var depth = 0;
                do
                {
                    depth += Cur.Is("(") ? 1 : Cur.Is(")") ? -1 : 0;
                    _pos++;
                }
                while (!AtEnd && depth > 0);
            }
        }
    }

    private static bool HasTopLevelParen(List<HeaderToken> tokens)
    {
        var depth = 0;
        foreach (var token in tokens)
        {
            if (token.Is("(") && depth == 0)
            {
                return true;
            }

            depth += token.Is("[") ? 1 : token.Is("]") ? -1 : 0;
        }

        return false;
    }

    private void Expect(string text)
    {
        if (Cur.Is(text))
        {
            _pos++;
        }
    }

    private static string Join(IEnumerable<HeaderToken> tokens) => string.Join(" ", tokens.Select(t => t.Text));
}
