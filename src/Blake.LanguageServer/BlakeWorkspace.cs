using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using Blake.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using OmniSharp.Extensions.LanguageServer.Protocol;
using Sprache;

namespace Blake.LanguageServer;

/// <summary>
/// Manages all open .blake documents and their cached analysis data.
/// </summary>
public class BlakeWorkspace
{
    private readonly ConcurrentDictionary<DocumentUri, BlakeDocument> _documents = new();

    public BlakeDocument? GetDocument(DocumentUri uri)
    {
        _documents.TryGetValue(uri, out var doc);
        return doc;
    }

    public void OpenDocument(DocumentUri uri, string text)
    {
        var doc = new BlakeDocument(uri, text);
        _documents[uri] = doc;
    }

    public void UpdateDocument(DocumentUri uri, string text)
    {
        if (_documents.TryGetValue(uri, out var doc))
        {
            doc.UpdateText(text);
        }
        else
        {
            OpenDocument(uri, text);
        }
    }

    public void CloseDocument(DocumentUri uri)
    {
        _documents.TryRemove(uri, out _);
    }
}

/// <summary>
/// Represents a single .blake document with cached parsing and analysis data.
/// </summary>
public class BlakeDocument
{
    private readonly object _lock = new();
    private string _text;
    private BlakeFile? _cachedAst;
    private bool _astInvalid = true;
    private SemanticModel? _cachedSemanticModel;
    private bool _semanticModelInvalid = true;

    public DocumentUri Uri { get; }

    public BlakeDocument(DocumentUri uri, string text)
    {
        Uri = uri;
        _text = text;
    }

    public string GetText()
    {
        lock (_lock)
        {
            return _text;
        }
    }

    public void UpdateText(string newText)
    {
        lock (_lock)
        {
            _text = newText;
            _astInvalid = true;
            _cachedAst = null;
            _semanticModelInvalid = true;
            _cachedSemanticModel = null;
        }
    }

    public BlakeFile? GetOrParseAst()
    {
        lock (_lock)
        {
            if (_astInvalid || _cachedAst == null)
            {
                try
                {
                    var parser = BlakeSpracheParser.CreateParser();
                    _cachedAst = parser.Parse(_text);
                    _astInvalid = false;
                }
                catch
                {
                    // Parse error - return null, diagnostics will report it
                    _cachedAst = null;
                    _astInvalid = false;
                }
            }
            return _cachedAst;
        }
    }

    /// <summary>
    /// Determines the context at a given position in the document.
    /// Detects meta-blocks, quasi-quotes, and splices.
    /// </summary>
    public BlakeContext GetContextAtPosition(int line, int character)
    {
        // Convert line/character to absolute position
        // Handle both \n and \r\n line endings correctly
        int absolutePosition = 0;
        int currentLine = 0;
        int i = 0;

        while (i < _text.Length && currentLine < line)
        {
            if (_text[i] == '\n')
            {
                currentLine++;
            }
            i++;
        }

        absolutePosition = i + character;

        // Look for @{| ... |} patterns
        int pos = 0;
        while (pos < _text.Length)
        {
            // Find next meta-block start
            int metaStart = _text.IndexOf("@{|", pos);
            if (metaStart == -1 || metaStart > absolutePosition)
            {
                // No more meta-blocks, or we're before the next one
                return BlakeContext.CSharpPassthrough;
            }

            // Find corresponding meta-block end
            int metaEnd = FindMetaBlockEnd(metaStart + 3);
            if (metaEnd == -1)
            {
                // Unclosed meta-block
                if (absolutePosition >= metaStart)
                {
                    return BlakeContext.MetaBlockCode;
                }
                return BlakeContext.Unknown;
            }

            // Check if position is inside this meta-block
            if (absolutePosition >= metaStart + 3 && absolutePosition < metaEnd)
            {
                // We're inside the meta-block - check if we're in a quasi-quote or splice
                return GetContextInsideMetaBlock(metaStart + 3, metaEnd, absolutePosition);
            }

            // Move past this meta-block
            pos = metaEnd + 2;
        }

        return BlakeContext.CSharpPassthrough;
    }

    private int FindMetaBlockEnd(int searchStart)
    {
        int depth = 1;
        int i = searchStart;
        bool inQuasiQuote = false;

        while (i < _text.Length - 1)
        {
            // Check for backtick (quasi-quote delimiter)
            if (_text[i] == '`' && (i == 0 || _text[i - 1] != '`'))
            {
                inQuasiQuote = !inQuasiQuote;
            }

            // Only count meta-block delimiters outside of quasi-quotes
            if (!inQuasiQuote)
            {
                if (_text[i] == '@' && i + 2 < _text.Length && _text[i + 1] == '{' && _text[i + 2] == '|')
                {
                    depth++;
                    i += 2;
                }
                else if (_text[i] == '|' && i + 1 < _text.Length && _text[i + 1] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                    i++;
                }
            }

            i++;
        }

        return -1; // Unclosed
    }

    private BlakeContext GetContextInsideMetaBlock(int metaStart, int metaEnd, int position)
    {
        // Scan through the meta-block content to find quasi-quotes and splices
        int i = metaStart;
        bool inQuasiQuote = false;

        while (i < metaEnd && i < _text.Length)
        {
            // Check for backtick (quasi-quote delimiter)
            if (_text[i] == '`' && (i == 0 || _text[i - 1] != '`'))
            {
                // Check if cursor is before this backtick
                if (i >= position)
                {
                    // We've reached a backtick at or past the cursor position
                    // Return the state BEFORE toggling (cursor is before the backtick)
                    return inQuasiQuote ? BlakeContext.QuasiQuoteText : BlakeContext.MetaBlockCode;
                }

                // Toggle for the next iteration
                inQuasiQuote = !inQuasiQuote;
            }

            // Check for splice start @(
            if (inQuasiQuote && _text[i] == '@' && i + 1 < _text.Length && _text[i + 1] == '(')
            {
                // Find the matching closing paren
                int spliceEnd = FindMatchingParen(i + 2);
                if (spliceEnd != -1 && position >= i + 2 && position < spliceEnd)
                {
                    // We're inside a splice
                    return BlakeContext.QuasiQuoteSplice;
                }

                // Skip past the splice
                if (spliceEnd != -1)
                {
                    i = spliceEnd + 1;
                    continue;
                }
            }

            i++;
        }

        // Default: we're in meta-block code
        return inQuasiQuote ? BlakeContext.QuasiQuoteText : BlakeContext.MetaBlockCode;
    }

    private int FindMatchingParen(int start)
    {
        int depth = 1;
        int i = start;

        while (i < _text.Length)
        {
            if (_text[i] == '(')
            {
                depth++;
            }
            else if (_text[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }

            i++;
        }

        return -1; // No matching paren
    }

    /// <summary>
    /// Gets a Roslyn SemanticModel for the generated .g.cs file.
    /// This provides real C# type information for the compiled code.
    /// </summary>
    public SemanticModel? GetGeneratedSemanticModel()
    {
        lock (_lock)
        {
            if (!_semanticModelInvalid && _cachedSemanticModel != null)
            {
                return _cachedSemanticModel;
            }

            try
            {
                // Try to find the generated .g.cs file
                var generatedFilePath = FindGeneratedFile();
                if (generatedFilePath == null || !File.Exists(generatedFilePath))
                {
                    Console.Error.WriteLine($"Blake LSP: Generated file not found for {Uri}");
                    return null;
                }

                Console.Error.WriteLine($"Blake LSP: Found generated file: {generatedFilePath}");

                // Read the generated C# code
                var generatedCode = File.ReadAllText(generatedFilePath);

                // Parse it with Roslyn
                var syntaxTree = CSharpSyntaxTree.ParseText(generatedCode, path: generatedFilePath);

                // Create a compilation with common references
                var references = GetBasicReferences();
                var compilation = CSharpCompilation.Create(
                    "BlakeGeneratedAnalysis",
                    new[] { syntaxTree },
                    references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                );

                _cachedSemanticModel = compilation.GetSemanticModel(syntaxTree);
                _semanticModelInvalid = false;
                return _cachedSemanticModel;
            }
            catch (System.Exception ex)
            {
                Console.Error.WriteLine($"Blake LSP: Error getting semantic model: {ex.Message}");
                return null;
            }
        }
    }

    private string? FindGeneratedFile()
    {
        // Convert file:// URI to local path
        var blakeFilePath = Uri.GetFileSystemPath();
        if (string.IsNullOrEmpty(blakeFilePath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(blakeFilePath);
        var fileName = Path.GetFileNameWithoutExtension(blakeFilePath);

        if (directory == null || fileName == null)
        {
            return null;
        }

        // Common locations for generated files
        var possiblePaths = new[]
        {
            // obj/Generated/Blake.SourceGenerator/*.g.cs (standard location)
            Path.Combine(directory, "obj", "Generated"),
            // obj/Debug/net*/generated/Blake.SourceGenerator/*.g.cs
            Path.Combine(directory, "obj", "Debug"),
            Path.Combine(directory, "obj", "Release"),
            // Go up to project root and check obj
            Path.Combine(Directory.GetParent(directory)?.FullName ?? directory, "obj", "Generated"),
            Path.Combine(Directory.GetParent(directory)?.FullName ?? directory, "obj", "Debug"),
            Path.Combine(Directory.GetParent(directory)?.FullName ?? directory, "obj", "Release"),
        };

        foreach (var basePath in possiblePaths)
        {
            if (!Directory.Exists(basePath)) continue;

            // Search recursively for the .blake.g.cs file
            var pattern = $"{fileName}.blake.g.cs";
            var files = Directory.GetFiles(basePath, pattern, SearchOption.AllDirectories);

            if (files.Length > 0)
            {
                // Return the most recently modified one
                return files.OrderByDescending(f => File.GetLastWriteTime(f)).FirstOrDefault();
            }
        }

        return null;
    }

    private MetadataReference[] GetBasicReferences()
    {
        // Add core runtime references for basic type resolution
        return new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
        };
    }

    /// <summary>
    /// Maps a position in the .blake file to the corresponding position in the .g.cs file.
    /// Uses #blake-line comments emitted by the source generator.
    /// </summary>
    public (int line, int character) MapToGeneratedPosition(int blakeLine, int blakeCharacter)
    {
        var generatedFilePath = FindGeneratedFile();
        if (generatedFilePath == null || !File.Exists(generatedFilePath))
        {
            return (blakeLine, blakeCharacter);
        }

        var generatedLines = File.ReadAllLines(generatedFilePath);

        // Find the #blake-line comment closest to (but not exceeding) the target line
        int bestGeneratedLine = -1;
        int bestBlakeLine = -1;

        for (int i = 0; i < generatedLines.Length; i++)
        {
            var line = generatedLines[i].Trim();
            if (line.StartsWith("// #blake-line "))
            {
                if (int.TryParse(line.Substring(15).Trim(), out int sourceBlakeLine))
                {
                    if (sourceBlakeLine <= blakeLine && sourceBlakeLine > bestBlakeLine)
                    {
                        bestBlakeLine = sourceBlakeLine;
                        bestGeneratedLine = i + 1; // The actual code is on the next line after the comment
                    }
                }
            }
        }

        if (bestGeneratedLine >= 0 && bestBlakeLine >= 0)
        {
            // Found a mapping - calculate offset within the section
            int lineOffset = blakeLine - bestBlakeLine;
            int targetLine = bestGeneratedLine + lineOffset;

            Console.Error.WriteLine($"Blake LSP: Mapped .blake line {blakeLine} to .g.cs line {targetLine} (section starts at blake:{bestBlakeLine} -> gen:{bestGeneratedLine})");
            return (targetLine, blakeCharacter);
        }

        // Fallback: no mapping found
        Console.Error.WriteLine($"Blake LSP: No line mapping found for .blake line {blakeLine}");
        return (blakeLine, blakeCharacter);
    }

    /// <summary>
    /// Extracts the splice expression at the given position (assumes position is inside a splice).
    /// Returns the splice expression text.
    /// </summary>
    public string? ExtractSpliceExpression(int line, int character)
    {
        lock (_lock)
        {
            // Convert line/character to absolute position
            int absolutePosition = 0;
            int currentLine = 0;
            int i = 0;

            while (i < _text.Length && currentLine < line)
            {
                if (_text[i] == '\n')
                {
                    currentLine++;
                }
                i++;
            }

            absolutePosition = i + character;

            // Look backwards to find the start of the splice @(
            int spliceStart = -1;
            for (int j = absolutePosition; j >= 1; j--)
            {
                if (_text[j] == '(' && _text[j - 1] == '@')
                {
                    spliceStart = j + 1; // Position after @(
                    break;
                }
            }

            if (spliceStart == -1) return null;

            // Find the matching closing paren
            int spliceEnd = FindMatchingParen(spliceStart);
            if (spliceEnd == -1) return null;

            // Extract the expression
            return _text.Substring(spliceStart, spliceEnd - spliceStart);
        }
    }

    /// <summary>
    /// Maps a position in the .blake file to the corresponding position in the concatenated meta-block code.
    /// Returns the position and the concatenated meta-block code string.
    /// </summary>
    public (int position, string metaCode)? MapBlakePositionToMetaBlockCode(int line, int character)
    {
        lock (_lock)
        {
            var ast = GetOrParseAst();
            if (ast == null) return null;

            var metaBlocks = ast.Children.OfType<Blake.SourceGenerator.MetaBlock>().ToList();
            if (metaBlocks.Count == 0) return null;

            // Convert line/character to absolute position in .blake file
            // Handle both \n and \r\n line endings correctly
            int absolutePosition = 0;
            int currentLine = 0;
            int i = 0;

            while (i < _text.Length && currentLine < line)
            {
                if (_text[i] == '\n')
                {
                    currentLine++;
                }
                i++;
            }

            absolutePosition = i + character;

            // Build the full concatenated meta-block code first (all blocks share context)
            var metaCodeBuilder = new System.Text.StringBuilder();
            foreach (var block in metaBlocks)
            {
                metaCodeBuilder.AppendLine(block.Code);
            }
            var fullMetaCode = metaCodeBuilder.ToString();

            // Find which meta-block contains this position
            int currentOffset = 0;
            int metaCodePosition = 0;

            foreach (var metaBlock in metaBlocks)
            {
                // Find this meta-block's position in the original text
                int metaStart = _text.IndexOf("@{|", currentOffset);
                if (metaStart == -1) break;

                int metaContentStart = metaStart + 3; // After @{|
                int metaEnd = FindMetaBlockEnd(metaContentStart);
                if (metaEnd == -1) break;

                // Check if the cursor position is inside this meta-block
                if (absolutePosition >= metaContentStart && absolutePosition < metaEnd)
                {
                    // Position is inside this meta-block
                    // Offset within this meta-block's code
                    int offsetInBlock = absolutePosition - metaContentStart;

                    // Return the position in concatenated code
                    return (metaCodePosition + offsetInBlock, fullMetaCode);
                }

                // Track cumulative offset in the concatenated code
                metaCodePosition += metaBlock.Code.Length + Environment.NewLine.Length;
                currentOffset = metaEnd + 2; // Move past |}
            }

            // If we didn't find the position in any meta-block, return null
            return null;
        }
    }
}

public enum BlakeContext
{
    Unknown,
    CSharpPassthrough,
    MetaBlockCode,
    QuasiQuoteText,
    QuasiQuoteSplice
}
