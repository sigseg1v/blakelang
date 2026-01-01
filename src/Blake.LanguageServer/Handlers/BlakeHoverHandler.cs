using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Blake.LanguageServer.Handlers;

public class BlakeHoverHandler : HoverHandlerBase
{
    private readonly BlakeWorkspace _workspace;

    public BlakeHoverHandler(BlakeWorkspace workspace)
    {
        _workspace = workspace;
    }

    public override async Task<Hover?> Handle(HoverParams request, CancellationToken cancellationToken)
    {
        var document = _workspace.GetDocument(request.TextDocument.Uri);
        if (document == null)
        {
            await Console.Error.WriteLineAsync($"Blake LSP: Document not found for hover request");
            return null;
        }

        // Check context
        var context = document.GetContextAtPosition(
            request.Position.Line,
            request.Position.Character
        );

        await Console.Error.WriteLineAsync($"Blake LSP: Hover request at {request.Position.Line}:{request.Position.Character}, context: {context}");

        if (context == BlakeContext.CSharpPassthrough)
        {
            // Use generated .g.cs file with #blake-line comments for position mapping
            return GetHoverFromGeneratedFile(document, request.Position);
        }
        else if (context == BlakeContext.QuasiQuoteSplice)
        {
            // Splices need special handling - extract and analyze the splice expression
            return GetHoverFromSplice(document, request.Position);
        }
        else if (context == BlakeContext.MetaBlockCode)
        {
            // Use meta-block code analysis
            return GetHoverFromMetaBlock(document, request.Position);
        }
        else if (context == BlakeContext.QuasiQuoteText)
        {
            // Quasi-quote text is template content - just show basic info
            return new Hover
            {
                Contents = new MarkedStringsOrMarkupContent(
                    new MarkupContent
                    {
                        Kind = MarkupKind.Markdown,
                        Value = "**Quasi-quote text**\n\nLiteral statements that will be emit to the output as-is."
                    }
                )
            };
        }

        return null;
    }

    private Hover? GetHoverFromGeneratedFile(BlakeDocument document, Position position)
    {
        try
        {
            var semanticModel = document.GetGeneratedSemanticModel();
            if (semanticModel == null)
            {
                Console.Error.WriteLine($"Blake LSP: No semantic model found for {document.Uri}");
                return null;
            }

            Console.Error.WriteLine($"Blake LSP: Got semantic model for hover at {position.Line}:{position.Character}");

            // Map position to generated file (for now, simple 1:1 mapping)
            var (line, character) = document.MapToGeneratedPosition(position.Line, position.Character);

            // Get the syntax tree and find the token at this position
            var syntaxTree = semanticModel.SyntaxTree;
            var sourceText = syntaxTree.GetText();

            // Convert line/character to absolute position
            if (line >= sourceText.Lines.Count)
            {
                Console.Error.WriteLine($"Blake LSP: Line {line} out of bounds (max: {sourceText.Lines.Count - 1})");
                return null;
            }

            var textLine = sourceText.Lines[line];
            var absolutePosition = textLine.Start + character;

            Console.Error.WriteLine($"Blake LSP: Looking for token at absolute position {absolutePosition}");

            // Get the token at this position
            var root = syntaxTree.GetRoot();
            var token = root.FindToken(absolutePosition);

            Console.Error.WriteLine($"Blake LSP: Found token '{token}' kind: {token.Kind()}");

            if (token.Parent == null)
            {
                Console.Error.WriteLine($"Blake LSP: Token has no parent");
                return null;
            }

            Console.Error.WriteLine($"Blake LSP: Token parent: {token.Parent.GetType().Name}");

            // Try to get symbol - for declarations, use GetDeclaredSymbol
            ISymbol? symbol = null;

            // First try declared symbol (for definitions)
            symbol = semanticModel.GetDeclaredSymbol(token.Parent);

            // If that fails, try symbol info (for usages)
            if (symbol == null)
            {
                var symbolInfo = semanticModel.GetSymbolInfo(token.Parent);
                symbol = symbolInfo.Symbol;
            }

            Console.Error.WriteLine($"Blake LSP: Symbol: {symbol?.ToDisplayString() ?? "null"}");

            if (symbol == null)
            {
                // Try to get type info for expressions
                var typeInfo = semanticModel.GetTypeInfo(token.Parent);
                Console.Error.WriteLine($"Blake LSP: Type info: {typeInfo.Type?.ToDisplayString() ?? "null"}");

                if (typeInfo.Type != null)
                {
                    return new Hover
                    {
                        Contents = new MarkedStringsOrMarkupContent(
                            new MarkupContent
                            {
                                Kind = MarkupKind.Markdown,
                                Value = $"```csharp\n{typeInfo.Type.ToDisplayString()}\n```"
                            }
                        )
                    };
                }
            }
            else
            {
                // Show symbol information
                var displayString = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                var documentation = symbol.GetDocumentationCommentXml();

                var markdown = $"```csharp\n{displayString}\n```";
                if (!string.IsNullOrEmpty(documentation))
                {
                    markdown += $"\n\n{documentation}";
                }

                return new Hover
                {
                    Contents = new MarkedStringsOrMarkupContent(
                        new MarkupContent
                        {
                            Kind = MarkupKind.Markdown,
                            Value = markdown
                        }
                    )
                };
            }
        }
        catch (System.Exception ex)
        {
            // Log and fall back to basic hover
            Console.Error.WriteLine($"Blake LSP: Error in GetHoverFromGeneratedFile: {ex.Message}");
            Console.Error.WriteLine($"Blake LSP: Stack trace: {ex.StackTrace}");
        }

        return null;
    }

    private Hover? GetHoverFromSplice(BlakeDocument document, Position position)
    {
        try
        {
            // Extract the splice expression from the text
            var spliceExpr = document.ExtractSpliceExpression(position.Line, position.Character);
            if (string.IsNullOrWhiteSpace(spliceExpr))
            {
                Console.Error.WriteLine($"Blake LSP: Failed to extract splice expression");
                return null;
            }

            Console.Error.WriteLine($"Blake LSP: Extracted splice expression: '{spliceExpr}'");

            // Get all meta-blocks to build the context
            var ast = document.GetOrParseAst();
            if (ast == null) return null;

            var metaBlocks = ast.Children.OfType<Blake.SourceGenerator.MetaBlock>().ToList();
            if (metaBlocks.Count == 0) return null;

            // Find which meta-block and which quasi-quote (within that block) contains this splice
            // Quasi-quotes are numbered per-meta-block, not globally
            int targetBlockIndex = -1;
            int targetEmitIndex = -1;

            for (int blockIdx = 0; blockIdx < metaBlocks.Count; blockIdx++)
            {
                var block = metaBlocks[blockIdx];
                for (int qqIdx = 0; qqIdx < block.QuasiQuotes.Count; qqIdx++)
                {
                    var qq = block.QuasiQuotes[qqIdx];
                    // Check if this quasi-quote contains a splice with matching expression
                    foreach (var part in qq.Parts)
                    {
                        if (part is Blake.SourceGenerator.QuasiQuoteSplice splice)
                        {
                            if (splice.Expression.Trim() == spliceExpr.Trim())
                            {
                                targetBlockIndex = blockIdx;
                                targetEmitIndex = qqIdx;
                                goto found;
                            }
                        }
                    }
                }
            }
            found:

            if (targetEmitIndex == -1)
            {
                Console.Error.WriteLine($"Blake LSP: Could not find quasi-quote containing splice '{spliceExpr}'");
                return null;
            }

            Console.Error.WriteLine($"Blake LSP: Found splice in meta-block {targetBlockIndex}, quasi-quote {targetEmitIndex} (__emit_{targetEmitIndex}__)");

            // Concatenate all meta-block code, tracking positions of each block
            var metaCodeOnly = new System.Text.StringBuilder();
            var blockStartPositions = new List<int>();

            foreach (var block in metaBlocks)
            {
                blockStartPositions.Add(metaCodeOnly.Length);
                metaCodeOnly.AppendLine(block.Code);
            }
            var metaCodeText = metaCodeOnly.ToString();

            // Find all __emit_N__ placeholders using regex
            var emitPattern = new System.Text.RegularExpressions.Regex(@"__emit_(\d+)__");
            var matches = emitPattern.Matches(metaCodeText);
            var emitIndices = matches.Select(m => int.Parse(m.Groups[1].Value)).Distinct().ToList();

            // Build the full meta-block code context wrapped in a class
            var metaCodeBuilder = new System.Text.StringBuilder();

            // Add using directives
            metaCodeBuilder.AppendLine("using System;");
            metaCodeBuilder.AppendLine("using System.Collections.Generic;");
            metaCodeBuilder.AppendLine("using System.Linq;");
            metaCodeBuilder.AppendLine("using Microsoft.CodeAnalysis;");
            metaCodeBuilder.AppendLine();

            // Wrap in a class with a method to avoid top-level statement issues
            metaCodeBuilder.AppendLine("public class MetaBlockAnalysis {");
            metaCodeBuilder.AppendLine("    public static void Emit(string s) { }");

            // Define __emit_N__() methods for quasi-quote placeholders
            foreach (var index in emitIndices)
            {
                metaCodeBuilder.AppendLine($"    public void __emit_{index}__() {{ }}");
            }

            metaCodeBuilder.AppendLine("    public void Analyze() {");

            // Add all meta-block code with proper indentation
            // Replace the specific __emit_N__() call that contains the splice we're analyzing
            var codeWithAnalysis = metaCodeText;

            // Replace the target __emit_N__() call with analysis block containing the splice expression
            // Only replace the occurrence within the target meta-block
            var targetPattern = $"__emit_{targetEmitIndex}__()";
            var replacement = $"{{ var __splice_expr__ = {spliceExpr}; }}";

            // Find the occurrence within the target meta-block's range
            var searchStart = blockStartPositions[targetBlockIndex];
            var searchEnd = targetBlockIndex + 1 < blockStartPositions.Count
                ? blockStartPositions[targetBlockIndex + 1]
                : codeWithAnalysis.Length;

            var firstOccurrence = codeWithAnalysis.IndexOf(targetPattern, searchStart, searchEnd - searchStart);
            if (firstOccurrence >= 0)
            {
                codeWithAnalysis = codeWithAnalysis.Substring(0, firstOccurrence) +
                                  replacement +
                                  codeWithAnalysis.Substring(firstOccurrence + targetPattern.Length);
                Console.Error.WriteLine($"Blake LSP: Replaced {targetPattern} at position {firstOccurrence} (within block {targetBlockIndex})");
            }
            else
            {
                Console.Error.WriteLine($"Blake LSP: WARNING - Could not find {targetPattern} in meta-block {targetBlockIndex}!");
            }

            // Now replace ALL __emit_N__() calls (including others) with empty blocks to avoid errors
            foreach (var index in emitIndices)
            {
                codeWithAnalysis = codeWithAnalysis.Replace($"__emit_{index}__()", "{ }");
            }

            var lines = codeWithAnalysis.Split(new[] { "\r\n", "\r", "\n" }, System.StringSplitOptions.None);
            foreach (var line in lines)
            {
                // Keep empty lines to preserve structure
                metaCodeBuilder.Append("        ");
                metaCodeBuilder.AppendLine(line);
            }

            // Close method and class
            metaCodeBuilder.AppendLine("    }");
            metaCodeBuilder.AppendLine("}");

            var fullCode = metaCodeBuilder.ToString();

            Console.Error.WriteLine($"Blake LSP: Analyzing splice in meta-block context ({fullCode.Length} chars)");
            // Print more of the code to see the replacement
            var previewLength = Math.Min(1500, fullCode.Length);
            Console.Error.WriteLine($"Blake LSP: Generated code preview:\n{fullCode.Substring(0, previewLength)}");

            // Parse with Roslyn
            var syntaxTree = CSharpSyntaxTree.ParseText(fullCode);

            // Create a compilation with Blake API references
            var references = new List<MetadataReference>
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Console).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(ISymbol).Assembly.Location), // Microsoft.CodeAnalysis for Blake API
            };

            // Add System.Runtime for IEnumerable<> and ValueType
            var systemRuntimePath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!,
                "System.Runtime.dll"
            );
            if (System.IO.File.Exists(systemRuntimePath))
            {
                references.Add(MetadataReference.CreateFromFile(systemRuntimePath));
            }

            var compilation = CSharpCompilation.Create(
                "SpliceAnalysis",
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            );

            // Check for compilation errors
            var diagnostics = compilation.GetDiagnostics();
            var errors = diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToArray();
            if (errors.Length > 0)
            {
                Console.Error.WriteLine($"Blake LSP: Splice compilation has {errors.Length} errors:");
                foreach (var error in errors.Take(5))
                {
                    Console.Error.WriteLine($"  {error.GetMessage()}");
                }
            }

            var semanticModel = compilation.GetSemanticModel(syntaxTree);

            // Find the __splice_expr__ variable declaration
            var root = syntaxTree.GetRoot();
            var spliceVar = root.DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax>()
                .FirstOrDefault(v => v.Identifier.Text == "__splice_expr__");

            if (spliceVar == null)
            {
                Console.Error.WriteLine($"Blake LSP: Failed to find splice variable in tree");
                return null;
            }

            // Get the initializer expression
            var initializer = spliceVar.Initializer?.Value;
            if (initializer == null)
            {
                Console.Error.WriteLine($"Blake LSP: Splice variable has no initializer");
                return null;
            }

            // Get type info for the expression
            var typeInfo = semanticModel.GetTypeInfo(initializer);
            var symbolInfo = semanticModel.GetSymbolInfo(initializer);

            Console.Error.WriteLine($"Blake LSP: Splice type: {typeInfo.Type?.ToDisplayString() ?? "null"}");
            Console.Error.WriteLine($"Blake LSP: Splice symbol: {symbolInfo.Symbol?.ToDisplayString() ?? "null"}");

            if (symbolInfo.Symbol != null)
            {
                var displayString = symbolInfo.Symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                var documentation = symbolInfo.Symbol.GetDocumentationCommentXml();

                var markdown = $"```csharp\n{displayString}\n```";
                if (!string.IsNullOrEmpty(documentation))
                {
                    markdown += $"\n\n{documentation}";
                }

                return new Hover
                {
                    Contents = new MarkedStringsOrMarkupContent(
                        new MarkupContent
                        {
                            Kind = MarkupKind.Markdown,
                            Value = markdown
                        }
                    )
                };
            }
            else if (typeInfo.Type != null)
            {
                return new Hover
                {
                    Contents = new MarkedStringsOrMarkupContent(
                        new MarkupContent
                        {
                            Kind = MarkupKind.Markdown,
                            Value = $"```csharp\n{typeInfo.Type.ToDisplayString()}\n```"
                        }
                    )
                };
            }
        }
        catch (System.Exception ex)
        {
            Console.Error.WriteLine($"Blake LSP: Error in GetHoverFromSplice: {ex.Message}");
            Console.Error.WriteLine($"Blake LSP: Stack trace: {ex.StackTrace}");
        }

        return null;
    }

    private Hover? GetHoverFromMetaBlock(BlakeDocument document, Position position)
    {
        try
        {
            // Map the .blake position to the concatenated meta-block code position
            var mapping = document.MapBlakePositionToMetaBlockCode(position.Line, position.Character);
            if (mapping == null)
            {
                Console.Error.WriteLine($"Blake LSP: Failed to map position to meta-block code");
                return null;
            }

            var (absolutePosition, metaCode) = mapping.Value;

            if (string.IsNullOrWhiteSpace(metaCode))
            {
                Console.Error.WriteLine($"Blake LSP: No meta-block code found");
                return null;
            }

            Console.Error.WriteLine($"Blake LSP: Mapped to meta-block position {absolutePosition} in code of length {metaCode.Length}");

            // Parse with Roslyn
            var syntaxTree = CSharpSyntaxTree.ParseText(metaCode);

            // Create a basic compilation with Blake API references
            var references = new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Console).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(ISymbol).Assembly.Location), // Microsoft.CodeAnalysis for Blake API
            };

            var compilation = CSharpCompilation.Create(
                "MetaBlockAnalysis",
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            );

            var semanticModel = compilation.GetSemanticModel(syntaxTree);

            // Get the token at the mapped position
            var root = syntaxTree.GetRoot();
            var token = root.FindToken(absolutePosition);

            Console.Error.WriteLine($"Blake LSP: Found token '{token}' kind: {token.Kind()}");

            if (token.Parent == null)
            {
                Console.Error.WriteLine($"Blake LSP: Token has no parent");
                return null;
            }

            Console.Error.WriteLine($"Blake LSP: Token parent: {token.Parent.GetType().Name}");

            // Try to get symbol - for declarations, use GetDeclaredSymbol
            ISymbol? symbol = null;

            // First try declared symbol (for definitions)
            symbol = semanticModel.GetDeclaredSymbol(token.Parent);

            // If that fails, try symbol info (for usages)
            if (symbol == null)
            {
                var symbolInfo = semanticModel.GetSymbolInfo(token.Parent);
                symbol = symbolInfo.Symbol;
            }

            Console.Error.WriteLine($"Blake LSP: Symbol: {symbol?.ToDisplayString() ?? "null"}");

            if (symbol == null)
            {
                // Try to get type info for expressions
                var typeInfo = semanticModel.GetTypeInfo(token.Parent);
                Console.Error.WriteLine($"Blake LSP: Type info: {typeInfo.Type?.ToDisplayString() ?? "null"}");

                if (typeInfo.Type != null)
                {
                    return new Hover
                    {
                        Contents = new MarkedStringsOrMarkupContent(
                            new MarkupContent
                            {
                                Kind = MarkupKind.Markdown,
                                Value = $"```csharp\n{typeInfo.Type.ToDisplayString()}\n```"
                            }
                        )
                    };
                }
            }
            else
            {
                // Show symbol information
                var displayString = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                var documentation = symbol.GetDocumentationCommentXml();

                var markdown = $"```csharp\n{displayString}\n```";
                if (!string.IsNullOrEmpty(documentation))
                {
                    markdown += $"\n\n{documentation}";
                }

                return new Hover
                {
                    Contents = new MarkedStringsOrMarkupContent(
                        new MarkupContent
                        {
                            Kind = MarkupKind.Markdown,
                            Value = markdown
                        }
                    )
                };
            }
        }
        catch (System.Exception ex)
        {
            Console.Error.WriteLine($"Blake LSP: Error in GetHoverFromMetaBlock: {ex.Message}");
            Console.Error.WriteLine($"Blake LSP: Stack trace: {ex.StackTrace}");
        }

        return null;
    }

    protected override HoverRegistrationOptions CreateRegistrationOptions(
        HoverCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new HoverRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("blake")
        };
    }
}
