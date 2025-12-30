using System;
using Sprache;

namespace Blake.SourceGenerator;

/// <summary>
/// Parser for .blake files using parser combinators.
///
/// Simplified Syntax:
///   - Everything outside @{| |} is pure C# passthrough (untouched)
///   - @{| ... |} delimits a meta-block (C# code executed at compile time)
///   - Inside meta-blocks:
///     - `...` (backtick strings) are quasi-quoted output templates
///     - @(expr) inside backticks are splices (interpolated at compile-time)
///     - Newlines are implicit after each closing backtick `
///   - @@ is an escape for literal @
///   - `` is an escape for literal ` inside backtick strings
///
/// Example:
///   @{|
///       foreach (var prop in schema.Properties) {
/// `    public @(prop.Type) @(prop.Name) { get; set; }`
///       }
///   |}
///
/// Note: Backticks are aligned with the desired output indentation.
/// </summary>
public class BlakeParser
{
    private readonly string _source;
    private readonly string _filePath;

    public BlakeParser(string source, string filePath)
    {
        _source = source;
        _filePath = filePath;
    }

    public BlakeFile Parse()
    {
        var parser = BlakeSpracheParser.CreateParser();

        // Call parser directly instead of using .Parse() which adds .End()
        var input = new Input(_source);
        var result = parser(input);

        if (!result.WasSuccessful)
        {
            // Calculate line and column from position
            int line = 1, column = 1, pos = result.Remainder.Position;
            for (int i = 0; i < pos && i < _source.Length; i++)
            {
                if (_source[i] == '\n')
                {
                    line++;
                    column = 1;
                }
                else
                {
                    column++;
                }
            }

            // Show more context about what failed
            var snippet = pos < _source.Length ? _source.Substring(pos, Math.Min(20, _source.Length - pos)) : "<EOF>";
            throw new BlakeParseException(
                $"Parse error at line {line}, column {column}: {result.Message}\n" +
                $"At: {snippet.Replace("\n", "\\n").Replace("\r", "\\r")}",
                pos,
                line,
                column
            );
        }

        var file = result.Value;
        file.FilePath = _filePath;
        file.EndPosition = _source.Length;
        return file;
    }
}
