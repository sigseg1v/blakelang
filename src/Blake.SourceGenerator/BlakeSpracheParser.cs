using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Sprache;

namespace Blake.SourceGenerator;

/// <summary>
/// Sprache-based parser for Blake language.
/// Parses .blake files into BlakeFile AST nodes.
/// </summary>
public static class BlakeSpracheParser
{
    // ============================================================================
    // Level 0: Primitives and Escape Sequences
    // ============================================================================

    /// <summary>
    /// Calculates 1-based line number from IInput position
    /// </summary>
    private static int CalculateLineNumber(IInput input)
    {
        var source = input.Source;
        var position = input.Position;
        int line = 1; // 1-based line numbers

        for (int i = 0; i < position && i < source.Length; i++)
        {
            if (source[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    /// <summary>
    /// Matches the meta-block start delimiter: @{|
    /// </summary>
    private static readonly Parser<string> MetaBlockStart = Parse.String("@{|").Text();

    /// <summary>
    /// Matches the meta-block end delimiter: |}
    /// </summary>
    private static readonly Parser<string> MetaBlockEnd = Parse.String("|}").Text();

    /// <summary>
    /// Matches an escaped @ symbol: @@ → @
    /// </summary>
    private static readonly Parser<char> EscapedAt = Parse.String("@@").Return('@');

    /// <summary>
    /// Matches an escaped backtick: `` → `
    /// </summary>
    private static readonly Parser<char> EscapedBacktick = Parse.String("``").Return('`');

    // ============================================================================
    // Level 1: Quasi-Quote Parsers
    // ============================================================================

    /// <summary>
    /// Parses balanced parentheses content for splice expressions.
    /// Handles nested parentheses like: expr(nested(deeper()))
    /// </summary>
    private static readonly Parser<string> BalancedParensContent =
        Parse.Ref(() => BalancedParensContentImpl);

    private static readonly Parser<string> BalancedParensContentImpl =
        input =>
        {
            var sb = new StringBuilder();
            var i = input;

            while (!i.AtEnd)
            {
                // Check for closing paren
                var close = Parse.Char(')')(i);
                if (close.WasSuccessful)
                {
                    // Found closing paren, stop (don't consume it)
                    break;
                }

                // Check for nested (...)
                var open = Parse.Char('(')(i);
                if (open.WasSuccessful)
                {
                    // Recursively parse balanced content
                    var nested = BalancedParensContent(open.Remainder);
                    if (nested.WasSuccessful)
                    {
                        var closeParen = Parse.Char(')')(nested.Remainder);
                        if (closeParen.WasSuccessful)
                        {
                            sb.Append('(');
                            sb.Append(nested.Value);
                            sb.Append(')');
                            i = closeParen.Remainder;
                            continue;
                        }
                    }
                    return Result.Failure<string>(i, "unmatched opening parenthesis", new string[0]);
                }

                // Regular char
                var ch = Parse.AnyChar(i);
                if (ch.WasSuccessful)
                {
                    sb.Append(ch.Value);
                    i = ch.Remainder;
                }
                else
                {
                    break;
                }
            }

            return Result.Success(sb.ToString(), i);
        };

    /// <summary>
    /// Parses a splice expression: @(expr)
    /// Captures the expression with balanced parentheses.
    /// </summary>
    private static readonly Parser<QuasiQuoteSplice> Splice =
        input =>
        {
            // Parse @(
            var start = Parse.String("@(")(input);
            if (!start.WasSuccessful)
            {
                return Result.Failure<QuasiQuoteSplice>(input, "expected @(", new string[0]);
            }

            // Parse balanced paren content
            var expr = BalancedParensContent(start.Remainder);
            if (!expr.WasSuccessful)
            {
                return Result.Failure<QuasiQuoteSplice>(start.Remainder, "expected expression", new string[0]);
            }

            // Parse closing )
            var close = Parse.Char(')')(expr.Remainder);
            if (!close.WasSuccessful)
            {
                return Result.Failure<QuasiQuoteSplice>(expr.Remainder, "expected closing )", new string[0]);
            }

            return Result.Success(new QuasiQuoteSplice { Expression = expr.Value.Trim() }, close.Remainder);
        };

    /// <summary>
    /// Parses literal text within a quasi-quote.
    /// Handles escaped backticks (``), escaped @ (@@), and regular text.
    /// </summary>
    private static readonly Parser<QuasiQuoteText> QuasiText =
        input =>
        {
            var chars = new List<char>();
            var i = input;

            while (!i.AtEnd)
            {
                // Try escaped backtick
                var eb = EscapedBacktick(i);
                if (eb.WasSuccessful)
                {
                    chars.Add(eb.Value);
                    i = eb.Remainder;
                    continue;
                }

                // Try escaped @
                var ea = EscapedAt(i);
                if (ea.WasSuccessful)
                {
                    chars.Add(ea.Value);
                    i = ea.Remainder;
                    continue;
                }

                // Try regular char (not backtick or @)
                var ch = Parse.CharExcept("`@")(i);
                if (ch.WasSuccessful)
                {
                    chars.Add(ch.Value);
                    i = ch.Remainder;
                }
                else
                {
                    // Can't parse any more chars
                    break;
                }
            }

            if (chars.Count == 0)
            {
                return Result.Failure<QuasiQuoteText>(input, "expected quasi-quote text", new string[0]);
            }

            return Result.Success(new QuasiQuoteText { Text = new string(chars.ToArray()) }, i);
        };

    /// <summary>
    /// Parses a complete quasi-quote: `...`
    /// Contains interleaved text and splice parts.
    /// </summary>
    private static readonly Parser<QuasiQuote> QuasiQuote =
        input =>
        {
            // Parse opening backtick
            var open = Parse.Char('`')(input);
            if (!open.WasSuccessful)
            {
                return Result.Failure<QuasiQuote>(input, "expected opening backtick", new string[0]);
            }

            var parts = new List<QuasiQuotePart>();
            var i = open.Remainder;

            while (!i.AtEnd)
            {
                // Check for closing backtick
                var close = Parse.Char('`')(i);
                if (close.WasSuccessful)
                {
                    // Found closing backtick, done
                    return Result.Success(CreateQuasiQuote(parts), close.Remainder);
                }

                // Check if we're at @( - if so, require splice to succeed
                var spliceCheck = Parse.String("@(")(i);
                if (spliceCheck.WasSuccessful)
                {
                    // We're at @(, so this MUST be a splice
                    var splice = Splice(i);
                    if (splice.WasSuccessful)
                    {
                        parts.Add(splice.Value);
                        i = splice.Remainder;
                        continue;
                    }
                    else
                    {
                        // @( started but splice failed - propagate error at actual failure position
                        return Result.Failure<QuasiQuote>(splice.Remainder, splice.Message, splice.Expectations);
                    }
                }

                // Try text (not a splice)
                var text = QuasiText(i);
                if (text.WasSuccessful)
                {
                    parts.Add(text.Value);
                    i = text.Remainder;
                }
                else
                {
                    return Result.Failure<QuasiQuote>(i, "expected quasi-quote content or closing backtick", new string[0]);
                }
            }

            return Result.Failure<QuasiQuote>(i, "unclosed quasi-quote", new string[0]);
        };

    private static QuasiQuote CreateQuasiQuote(IEnumerable<QuasiQuotePart> parts)
    {
        var qq = new QuasiQuote();
        foreach (var part in parts)
        {
            qq.Parts.Add(part);
        }
        return qq;
    }

    // ============================================================================
    // Level 2: Meta-Block Parser
    // ============================================================================

    /// <summary>
    /// Helper class for meta-block content elements.
    /// </summary>
    private abstract class MetaBlockElement { }
    private class QuasiQuoteElement : MetaBlockElement
    {
        public QuasiQuote QuasiQuote { get; }
        public QuasiQuoteElement(QuasiQuote qq) => QuasiQuote = qq;
    }
    private class TextElement : MetaBlockElement
    {
        public char Char { get; }
        public TextElement(char c) => Char = c;
    }

    /// <summary>
    /// Parses a single character of meta-block content.
    /// The Until combinator handles stopping before |}
    /// </summary>
    private static readonly Parser<char> MetaBlockChar =
        Parse.AnyChar;

    /// <summary>
    /// Parses meta-block content: quasi-quotes and C# code until |}
    /// </summary>
    private static readonly Parser<(string code, List<QuasiQuote> quotes, bool hasNewline)> MetaBlockContent =
        input =>
        {
            var elements = new List<MetaBlockElement>();
            var i = input;

            while (!i.AtEnd)
            {
                // Check for |}
                var terminator = Parse.String("|}")(i);
                if (terminator.WasSuccessful)
                {
                    // Found terminator, stop
                    break;
                }

                // Check if we're at a backtick - if so, require quasi-quote to succeed
                var backtickCheck = Parse.Char('`')(i);
                if (backtickCheck.WasSuccessful)
                {
                    // We're at a backtick, so this MUST be a quasi-quote
                    var qq = QuasiQuote(i);
                    if (qq.WasSuccessful)
                    {
                        elements.Add(new QuasiQuoteElement(qq.Value));
                        i = qq.Remainder;
                        continue;
                    }
                    else
                    {
                        // Backtick started but quasi-quote failed - propagate error at actual failure position
                        return Result.Failure<(string, List<QuasiQuote>, bool)>(qq.Remainder, qq.Message, qq.Expectations);
                    }
                }

                // Try single char (not a backtick)
                var ch = Parse.AnyChar(i);
                if (ch.WasSuccessful)
                {
                    elements.Add(new TextElement(ch.Value));
                    i = ch.Remainder;
                }
                else
                {
                    return Result.Failure<(string, List<QuasiQuote>, bool)>(i, "expected meta-block content", new string[0]);
                }
            }

            return Result.Success(BuildMetaBlockContent(elements), i);
        };

    private static (string code, List<QuasiQuote> quotes, bool hasNewline) BuildMetaBlockContent(
        IEnumerable<MetaBlockElement> elements)
    {
        var code = new StringBuilder();
        var quotes = new List<QuasiQuote>();
        var hasNewline = false;

        foreach (var element in elements)
        {
            if (element is QuasiQuoteElement qqElem)
            {
                quotes.Add(qqElem.QuasiQuote);
                code.Append($"__emit_{quotes.Count - 1}__();");
            }
            else if (element is TextElement textElem)
            {
                code.Append(textElem.Char);
                if (textElem.Char == '\n')
                {
                    hasNewline = true;
                }
            }
        }

        return (code.ToString(), quotes, hasNewline);
    }

    /// <summary>
    /// Parses a complete meta-block: @{| ... |}
    /// Sets IsInline flag on quasi-quotes based on presence of newlines.
    /// </summary>
    private static readonly Parser<MetaBlock> MetaBlock =
        input =>
        {
            // Parse @{|
            var start = MetaBlockStart(input);
            if (!start.WasSuccessful)
            {
                return Result.Failure<MetaBlock>(input, "expected @{|", new string[0]);
            }

            // Parse content
            var content = MetaBlockContent(start.Remainder);
            if (!content.WasSuccessful)
            {
                // Propagate the error at the actual failure position
                return Result.Failure<MetaBlock>(content.Remainder, content.Message, content.Expectations);
            }

            // Parse |}
            var end = MetaBlockEnd(content.Remainder);
            if (!end.WasSuccessful)
            {
                return Result.Failure<MetaBlock>(content.Remainder, "expected |}", new string[0]);
            }

            // Try to consume optional trailing newline
            var i = end.Remainder;
            var newline = Parse.String("\n")(i);
            if (newline.WasSuccessful)
            {
                i = newline.Remainder;
            }
            else
            {
                var crlf = Parse.String("\r\n")(i);
                if (crlf.WasSuccessful)
                {
                    i = crlf.Remainder;
                }
            }

            // Calculate line number from the start position of this meta-block
            int lineNumber = CalculateLineNumber(input);

            return Result.Success(CreateMetaBlock(content.Value, lineNumber), i);
        };

    private static MetaBlock CreateMetaBlock(
        (string code, List<QuasiQuote> quotes, bool hasNewline) content,
        int lineNumber)
    {
        var (code, quotes, hasNewline) = content;
        var isInline = !hasNewline;

        // Apply IsInline to all quasi-quotes
        foreach (var qq in quotes)
        {
            qq.IsInline = isInline;
        }

        var metaBlock = new MetaBlock { Code = code, Line = lineNumber };
        foreach (var qq in quotes)
        {
            metaBlock.QuasiQuotes.Add(qq);
        }
        return metaBlock;
    }

    // ============================================================================
    // Level 3: Top-Level Parsers
    // ============================================================================

    /// <summary>
    /// Parses a single char for passthrough: either @@ escape, @ not followed by {|, or any non-@ char
    /// </summary>
    private static readonly Parser<char> PassthroughChar =
        EscapedAt  // @@ → @
            .Or(Parse.Char('@').Where(_ => !Parse.String("{|").TryParse("").WasSuccessful))  // @ not followed by {|
            .Or(Parse.CharExcept('@'));  // Any char except @

    /// <summary>
    /// Parses C# passthrough code.
    /// Returns empty result if at meta-block start or EOF.
    /// </summary>
    private static readonly Parser<CSharpPassthrough> CSharpPassthrough =
        input =>
        {
            var sb = new StringBuilder();
            var i = input;

            while (!i.AtEnd)
            {
                // Check for @{| (meta-block start)
                var peek3 = i;
                var at = Parse.Char('@')(peek3);
                if (at.WasSuccessful)
                {
                    var brace = Parse.Char('{')(at.Remainder);
                    if (brace.WasSuccessful)
                    {
                        var pipe = Parse.Char('|')(brace.Remainder);
                        if (pipe.WasSuccessful)
                        {
                            // We're at @{|, stop
                            break;
                        }
                    }
                }

                // Check for @@ escape
                var escaped = EscapedAt(i);
                if (escaped.WasSuccessful)
                {
                    sb.Append('@');
                    i = escaped.Remainder;
                    continue;
                }

                // Regular char
                var ch = Parse.AnyChar(i);
                if (ch.WasSuccessful)
                {
                    sb.Append(ch.Value);
                    i = ch.Remainder;
                }
                else
                {
                    break;
                }
            }

            if (sb.Length == 0)
            {
                // No content - this is OK at start of meta-block or EOF
                return Result.Success<CSharpPassthrough>(null!, i);
            }

            return Result.Success(new CSharpPassthrough
            {
                Code = sb.ToString(),
                Line = CalculateLineNumber(input)
            }, i);
        };

    /// <summary>
    /// Parses file as alternating passthrough and meta-blocks
    /// </summary>
    private static readonly Parser<BlakeFile> BlakeFileParser =
        input =>
        {
            var file = new BlakeFile
            {
                StartPosition = 0,
                Line = 1,
                Column = 1
            };

            var i = input;

            while (!i.AtEnd)
            {
                // Try passthrough first
                var passthrough = CSharpPassthrough(i);
                if (passthrough.WasSuccessful && passthrough.Value != null)
                {
                    file.Children.Add(passthrough.Value);
                    i = passthrough.Remainder;
                }

                if (i.AtEnd)
                {
                    break;
                }

                // Try meta-block
                var meta = MetaBlock(i);
                if (meta.WasSuccessful)
                {
                    file.Children.Add(meta.Value);
                    i = meta.Remainder;
                }
                else
                {
                    // If we can't parse meta-block, propagate the error at the actual failure position
                    return Result.Failure<BlakeFile>(meta.Remainder, meta.Message, meta.Expectations);
                }
            }

            return Result.Success(file, i);
        };

    /// <summary>
    /// Creates the main Blake parser.
    /// </summary>
    public static Parser<BlakeFile> CreateParser() => BlakeFileParser;
}
