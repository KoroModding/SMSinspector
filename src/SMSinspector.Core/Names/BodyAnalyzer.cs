using System.Text;
using SMSinspector.Core.Layouts;

namespace SMSinspector.Core.Names;

public enum BodyShape
{
    /// <summary><c>return unkXX;</c> or <c>unkXX = argument;</c>, optionally through <c>this-&gt;</c>.</summary>
    Matching,

    /// <summary>At most two statements and no nested block.</summary>
    Short,

    Long,
}

/// <summary>Reads function bodies from the decomp for references to unknown members of <c>this</c>.</summary>
public static class BodyAnalyzer
{
    public const int MaxShortStatements = 2;

    /// <summary>
    /// The <c>unkXX</c> names the body uses as members of <c>this</c>: bare, or after
    /// <c>this-&gt;</c>. A name after another object's <c>.</c> or <c>-&gt;</c> belongs to that object.
    /// </summary>
    public static IReadOnlyList<string> UnknownReferences(IReadOnlyList<HeaderToken> body)
    {
        var names = new List<string>();
        for (var i = 0; i < body.Count; i++)
        {
            var token = body[i];
            if (token.Kind != TokenKind.Identifier || !NameExtractor.IsUnknownName(token.Text))
            {
                continue;
            }

            var ownMember = i == 0
                || !(body[i - 1].Is(".") || body[i - 1].Is("::") || IsArrow(body, i - 1))
                || (IsArrow(body, i - 1) && i >= 3 && body[i - 3].Is("this"));
            if (ownMember && !names.Contains(token.Text))
            {
                names.Add(token.Text);
            }
        }

        return names;
    }

    public static BodyShape Classify(FunctionBody function)
    {
        var tokens = StripThis(function.Body);
        if (tokens is [{ Text: "return" }, var returned, { Text: ";" }] && NameExtractor.IsUnknownName(returned.Text))
        {
            return BodyShape.Matching;
        }

        if (tokens is [var assigned, { Text: "=" }, var value, { Text: ";" }]
            && NameExtractor.IsUnknownName(assigned.Text)
            && value.Kind == TokenKind.Identifier
            && function.Parameters.Contains(value.Text))
        {
            return BodyShape.Matching;
        }

        if (function.Body.Any(t => t.Is("{")))
        {
            return BodyShape.Long;
        }

        return function.Body.Count(t => t.Is(";")) <= MaxShortStatements ? BodyShape.Short : BodyShape.Long;
    }

    /// <summary>The body on one line, for the report.</summary>
    public static string Format(IReadOnlyList<HeaderToken> body)
    {
        var text = new StringBuilder();
        var glue = true;
        for (var i = 0; i < body.Count; i++)
        {
            var token = body[i];
            if (token.Is("-") && i + 1 < body.Count && body[i + 1].Is(">"))
            {
                text.Append("->");
                i++;
                glue = true;
                continue;
            }

            var joinsOperator = i > 0 && token.Kind == TokenKind.Punct && body[i - 1].Kind == TokenKind.Punct
                && $"{body[i - 1].Text}{token.Text}" is ">>" or "<<" or "==" or "!=" or "<=" or ">=" or "&&" or "||" or "++" or "--" or "+=" or "-=" or "|=" or "&=";
            if (!glue && !joinsOperator && !(token.Text is ";" or "," or ")" or "]" or "." or "::" or "(" or "["))
            {
                text.Append(' ');
            }

            text.Append(token.Text);
            glue = token.Text is "(" or "[" or "." or "::" or "!" or "~";
        }

        return text.ToString();
    }

    private static bool IsArrow(IReadOnlyList<HeaderToken> body, int greaterThan) =>
        greaterThan >= 1 && body[greaterThan].Is(">") && body[greaterThan - 1].Is("-");

    /// <summary>Removes "this ->" so that "this->unk4" and "unk4" compare alike.</summary>
    private static List<HeaderToken> StripThis(IReadOnlyList<HeaderToken> body)
    {
        var result = new List<HeaderToken>(body.Count);
        for (var i = 0; i < body.Count; i++)
        {
            if (body[i].Is("this") && i + 2 < body.Count && body[i + 1].Is("-") && body[i + 2].Is(">"))
            {
                i += 2;
                continue;
            }

            result.Add(body[i]);
        }

        return result;
    }
}
