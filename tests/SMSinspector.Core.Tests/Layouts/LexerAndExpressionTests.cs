using SMSinspector.Core.Layouts;

namespace SMSinspector.Core.Tests.Layouts;

public class LexerAndExpressionTests
{
    private const string Source = """
        #ifndef TEST_H
        #define TEST_H
        #define BUF_LEN 0x10
        #if 0
        int hidden;
        #endif
        #ifdef VERSION_GMSP01
        int palOnly;
        #else
        int jpOnly;
        #endif
        #if defined(VERSION_GMSJ01)
        int jpDefined;
        #endif
        #ifndef VERSION_GMSP01
        int notPal;
        #endif
        #ifdef __MWERKS__
        int compiler;
        #endif
        /* 0x1C */ int x; // trailing
        /* a regular comment */
        #endif
        """;

    private static HeaderLexer.Result Lex() => HeaderLexer.Tokenize(Source);

    private static HeaderToken Token(string text) => Lex().Tokens.Single(t => t.Text == text);

    [Fact]
    public void Static_conditions_are_evaluated_once()
    {
        Assert.DoesNotContain(Lex().Tokens, t => t.Text == "hidden");
        Assert.Equal(VersionMask.Both, Token("compiler").Versions);
    }

    [Theory]
    [InlineData("palOnly", VersionMask.Pal)]
    [InlineData("jpOnly", VersionMask.Jp)]
    [InlineData("jpDefined", VersionMask.Jp)]
    [InlineData("notPal", VersionMask.Jp)]
    [InlineData("x", VersionMask.Both)]
    public void Version_branches_are_kept_and_tagged(string identifier, VersionMask expected)
    {
        Assert.Equal(expected, Token(identifier).Versions);
    }

    [Fact]
    public void Offset_comments_are_tokens_and_other_comments_are_dropped()
    {
        var tokens = Lex().Tokens;
        var offset = tokens.Single(t => t.Kind == TokenKind.OffsetComment);
        Assert.Equal(0x1Cu, offset.Offset);
        Assert.Contains(tokens, t => t.Kind == TokenKind.LineComment && t.Text == "trailing");
        Assert.DoesNotContain(tokens, t => t.Text.Contains("regular"));
    }

    [Fact]
    public void Plain_defines_are_collected()
    {
        Assert.Contains(Lex().Macros, m => m.Name == "BUF_LEN" && m.Value == "0x10");
    }

    [Theory]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("0x10 << 2", 64)]
    [InlineData("1 < 2 && 3 >= 3", 1)]
    [InlineData("7 % 4 | 8", 11)]
    [InlineData("-(4) + ~0", -5)]
    [InlineData("N * 2", 8)]
    public void Constant_expressions(string expression, long expected)
    {
        Assert.True(ConstantExpression.TryEvaluate(expression, n => n == "N" ? 4 : null, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("UNKNOWN + 1")]
    [InlineData("10 / 0")]
    [InlineData("(1 + 2")]
    public void Bad_expressions_fail(string expression)
    {
        Assert.False(ConstantExpression.TryEvaluate(expression, _ => null, out _));
    }

    [Theory]
    [InlineData("VERSION_SELECT(GMSJ01(4), GMSP01(8))", VersionMask.Jp, 4)]
    [InlineData("VERSION_SELECT(GMSJ01(4), GMSP01(8))", VersionMask.Pal, 8)]
    [InlineData("2 + GMSP01(1)", VersionMask.Pal, 3)]
    public void Version_macros_expand_per_version(string expression, VersionMask version, long expected)
    {
        var expanded = TypeCatalog.ExpandVersionMacros(expression, version);
        Assert.True(ConstantExpression.TryEvaluate(expanded, _ => null, out var value));
        Assert.Equal(expected, value);
    }
}
