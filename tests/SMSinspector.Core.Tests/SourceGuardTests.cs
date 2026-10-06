using System.Text;
using System.Text.RegularExpressions;

namespace SMSinspector.Core.Tests;

/// <summary>
/// Plan 5.8: no game name is written in the code, except the anchors in Anchors.cs. This
/// scans the string literals of src/ (C#, raw and verbatim strings included) and the text
/// of the XAML files for identifiers shaped like the decomp's names. Comments are not
/// scanned: they may cite decomp names.
/// </summary>
public partial class SourceGuardTests
{
    private const string AnchorFile = "Anchors.cs";

    [Fact]
    public void Game_names_appear_only_in_the_anchor_file()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(SourceRoot(), "*.*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(file);
            if (extension is not (".cs" or ".axaml") || Path.GetFileName(file) == AnchorFile || IsBuildOutput(file))
            {
                continue;
            }

            var source = File.ReadAllText(file);
            var texts = extension == ".cs" ? CSharpStrings(source) : XamlTexts(source);
            foreach (var (line, name) in GameLikeNames(texts))
            {
                offenders.Add($"{Path.GetRelativePath(SourceRoot(), file)}:{line}: {name}");
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_guard_recognises_game_like_names()
    {
        Assert.Matches(GameLikeName(), "TFooActor");
        Assert.Matches(GameLikeName(), "gpFooPointer");
        Assert.Matches(GameLikeName(), "mFooSpeed");
        Assert.Matches(GameLikeName(), "instance__Q23Gfx7TCanvas");
        Assert.DoesNotMatch(GameLikeName(), "Loading symbols...");
        Assert.DoesNotMatch(GameLikeName(), "TODO");
    }

    [Fact]
    public void The_guard_reads_every_kind_of_string_and_skips_comments()
    {
        const string source = """"
            // "TInComment"
            /* "TInBlock" */
            var a = "TPlain";
            var b = @"TVerbatim ""quoted""";
            var c = $"{x} TInterpolated";
            var d = """
                first line
                TRawString
                """;
            var e = '"';
            var f = "after a char";
            """";

        var strings = CSharpStrings(source);
        var names = GameLikeNames(strings);

        Assert.Equal(["TPlain", "TVerbatim", "TInterpolated", "TRawString"], names.Select(n => n.Name));
        Assert.Equal(8, names.Single(n => n.Name == "TRawString").Line);
        Assert.Contains(strings, s => s.Text == "after a char");
    }

    /// <summary>Each game-like name in the texts, with the line it is on.</summary>
    private static List<(int Line, string Name)> GameLikeNames(IEnumerable<(int Line, string Text)> texts) =>
        texts.SelectMany(t => GameLikeName().Matches(t.Text)
                .Select(m => (t.Line + t.Text.AsSpan(0, m.Index).Count('\n'), m.Value)))
            .ToList();

    /// <summary>The contents of every string literal, with the line it starts on.</summary>
    private static List<(int Line, string Text)> CSharpStrings(string source)
    {
        var result = new List<(int, string)>();
        var line = 1;
        var i = 0;

        while (i < source.Length)
        {
            var c = source[i];
            if (c == '\n')
            {
                line++;
                i++;
            }
            else if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? source.Length : end + 2;
                line += source.AsSpan(i, end - i).Count('\n');
                i = end;
            }
            else if (c == '\'')
            {
                // A char literal, possibly '"' or '\''.
                var end = i + 1;
                while (end < source.Length && source[end] != '\'' && source[end] != '\n')
                {
                    end += source[end] == '\\' ? 2 : 1;
                }

                i = end + 1;
            }
            else if (c == '"')
            {
                var start = line;
                var quotes = 0;
                while (i + quotes < source.Length && source[i + quotes] == '"')
                {
                    quotes++;
                }

                var verbatim = i > 0 && (source[i - 1] == '@' || (i > 1 && source[i - 1] == '$' && source[i - 2] == '@'));
                string text;
                if (quotes >= 3)
                {
                    // Raw string: ends at the same number of quotes.
                    var fence = new string('"', quotes);
                    var end = source.IndexOf(fence, i + quotes, StringComparison.Ordinal);
                    end = end < 0 ? source.Length : end;
                    text = source[(i + quotes)..end];
                    i = Math.Min(source.Length, end + quotes);
                }
                else if (quotes == 2 && !verbatim)
                {
                    text = "";
                    i += 2;
                }
                else
                {
                    var builder = new StringBuilder();
                    i++;
                    while (i < source.Length)
                    {
                        if (verbatim && source[i] == '"' && i + 1 < source.Length && source[i + 1] == '"')
                        {
                            builder.Append('"');
                            i += 2;
                        }
                        else if (source[i] == '"')
                        {
                            i++;
                            break;
                        }
                        else if (!verbatim && source[i] == '\\' && i + 1 < source.Length)
                        {
                            builder.Append(source[i + 1]);
                            i += 2;
                        }
                        else
                        {
                            builder.Append(source[i]);
                            i++;
                        }
                    }

                    text = builder.ToString();
                }

                line += text.Count(ch => ch == '\n');
                result.Add((start, text));
            }
            else
            {
                i++;
            }
        }

        return result;
    }

    private static List<(int Line, string Text)> XamlTexts(string source)
    {
        var withoutComments = XamlComment().Replace(source, m => new string('\n', m.Value.Count(ch => ch == '\n')));
        return XamlText().Matches(withoutComments)
            .Select(m => (withoutComments.AsSpan(0, m.Index).Count('\n') + 1, m.Groups["value"].Value))
            .ToList();
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SMSinspector.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src");
    }

    private static bool IsBuildOutput(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XamlComment();

    [GeneratedRegex("(?:Text|Content|Title|PlaceholderText|Header|ToolTip.Tip)=\"(?<value>[^\"{]*)\"")]
    private static partial Regex XamlText();

    // Class names (TFoo), globals (gpFoo), members (mFoo) and mangled names with a scope (__Q2).
    [GeneratedRegex(@"\b(?:T[A-Z][a-z]\w*|gp[A-Z]\w*|m[A-Z][a-z]\w*|\w+__Q\d\w+)\b")]
    private static partial Regex GameLikeName();
}
