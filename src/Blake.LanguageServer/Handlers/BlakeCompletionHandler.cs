using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Blake.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Recommendations;
using Microsoft.CodeAnalysis.Text;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Blake.LanguageServer.Handlers;

public class BlakeCompletionHandler : CompletionHandlerBase
{
    private readonly BlakeWorkspace _workspace;

    public BlakeCompletionHandler(BlakeWorkspace workspace)
    {
        _workspace = workspace;
    }

    public override Task<CompletionList> Handle(CompletionParams request, CancellationToken cancellationToken)
    {
        Console.Error.WriteLine($"Blake LSP: Completion request at {request.Position.Line}:{request.Position.Character}");

        var document = _workspace.GetDocument(request.TextDocument.Uri);
        if (document == null)
        {
            Console.Error.WriteLine($"Blake LSP: Document not found for completion");
            return Task.FromResult(new CompletionList());
        }

        // Check if we're in a context where completions are relevant
        var context = document.GetContextAtPosition(
            request.Position.Line,
            request.Position.Character
        );

        Console.Error.WriteLine($"Blake LSP: Completion context: {context}");

        if (context != BlakeContext.MetaBlockCode && context != BlakeContext.QuasiQuoteSplice)
        {
            // Don't provide completions in C# passthrough or quasi-quote text
            return Task.FromResult(new CompletionList());
        }

        try
        {
            var completions = GetCompletionsForPosition(document, request.Position);
            return Task.FromResult(new CompletionList(completions));
        }
        catch
        {
            return Task.FromResult(new CompletionList());
        }
    }

    public override Task<CompletionItem> Handle(CompletionItem request, CancellationToken cancellationToken)
    {
        // No additional resolution needed for now
        return Task.FromResult(request);
    }

    private List<CompletionItem> GetCompletionsForPosition(BlakeDocument document, Position position)
    {
        var completions = new List<CompletionItem>();

        try
        {
            // Map the .blake position to the concatenated meta-block code position
            var mapping = document.MapBlakePositionToMetaBlockCode(position.Line, position.Character);
            if (mapping == null)
            {
                Console.Error.WriteLine($"Blake LSP: Failed to map position to meta-block code for completion");
                return GetFallbackCompletions();
            }

            var (absolutePosition, metaCode) = mapping.Value;

            if (string.IsNullOrWhiteSpace(metaCode))
            {
                Console.Error.WriteLine($"Blake LSP: No meta-block code found for completion");
                return GetFallbackCompletions();
            }

            Console.Error.WriteLine($"Blake LSP: Completion at meta-block position {absolutePosition} in code of length {metaCode.Length}");

            // Create a Roslyn compilation for semantic analysis
            var references = GetCompilationReferences();
            var blakeApiCode = GetBlakeApiCode();

            var blakeApiTree = CSharpSyntaxTree.ParseText(blakeApiCode, path: "Blake.Runtime.cs");
            var metaCodeTree = CSharpSyntaxTree.ParseText(metaCode, path: "MetaCode.cs");

            var compilation = CSharpCompilation.Create(
                "BlakeMetaAnalysis",
                new[] { blakeApiTree, metaCodeTree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            );

            var semanticModel = compilation.GetSemanticModel(metaCodeTree);

            // Add documents to workspace for Recommender API
            var workspace = new AdhocWorkspace();
            var projectInfo = ProjectInfo.Create(
                ProjectId.CreateNewId(),
                VersionStamp.Default,
                "BlakeMetaProject",
                "BlakeMetaProject",
                LanguageNames.CSharp,
                metadataReferences: references,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            );
            var project = workspace.AddProject(projectInfo);
            workspace.AddDocument(project.Id, "Blake.Runtime.cs", SourceText.From(blakeApiCode));
            var metaDocument = workspace.AddDocument(project.Id, "MetaCode.cs", SourceText.From(metaCode));

            // Get the semantic model from the workspace document
            var docSemanticModel = metaDocument.GetSemanticModelAsync().Result;
            if (docSemanticModel == null)
            {
                Console.Error.WriteLine($"Blake LSP: Failed to get semantic model from document");
                workspace.Dispose();
                return GetFallbackCompletions();
            }

            var recommendedSymbols = Recommender.GetRecommendedSymbolsAtPosition(
                docSemanticModel,
                absolutePosition,
                workspace
            );

            Console.Error.WriteLine($"Blake LSP: Found {recommendedSymbols.Count()} recommended symbols");

            foreach (var symbol in recommendedSymbols.Take(100)) // Limit to 100 for performance
            {
                // Filter out noise symbols
                if (ShouldExcludeSymbol(symbol))
                    continue;

                var kind = GetCompletionItemKind(symbol);
                var detail = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

                completions.Add(new CompletionItem
                {
                    Label = symbol.Name,
                    Kind = kind,
                    Detail = detail,
                    Documentation = symbol.GetDocumentationCommentXml()
                });
            }

            workspace.Dispose();

            // If we got results, return them
            if (completions.Count > 0)
            {
                return completions;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Blake LSP: Error getting completions: {ex.Message}");
        }

        // Fallback to basic completions
        return GetFallbackCompletions();
    }

    private List<CompletionItem> GetFallbackCompletions()
    {
        var completions = new List<CompletionItem>();

        // Add Blake API completions
        completions.Add(new CompletionItem
        {
            Label = "Blake",
            Kind = CompletionItemKind.Class,
            Detail = "Blake metaprogramming API",
            Documentation = "Access Blake compile-time API methods"
        });

        completions.Add(new CompletionItem
        {
            Label = "FindTypesImplementing",
            Kind = CompletionItemKind.Method,
            Detail = "IEnumerable<INamedTypeSymbol> FindTypesImplementing(string fullTypeName)",
            Documentation = "Find all types that implement a given interface or base class"
        });

        return completions;
    }

    private bool ShouldExcludeSymbol(ISymbol symbol)
    {
        // Exclude C# keywords - Roslyn sometimes returns these as "alias" symbols
        var csharpKeywords = new HashSet<string>
        {
            "class", "struct", "interface", "enum", "delegate",
            "if", "else", "while", "for", "foreach", "do", "switch", "case",
            "return", "break", "continue", "goto", "throw", "try", "catch", "finally",
            "public", "private", "protected", "internal", "static", "readonly", "const",
            "virtual", "override", "abstract", "sealed", "new", "partial",
            "void", "int", "string", "bool", "double", "float", "decimal", "long", "short",
            "byte", "char", "object", "dynamic", "var"
        };

        if (csharpKeywords.Contains(symbol.Name))
            return true;

        // Keep Blake API symbols (highest priority)
        if (symbol.ContainingType?.Name == "Blake")
            return false;

        // Keep local variables and parameters (user-defined in meta-block)
        if (symbol.Kind == Microsoft.CodeAnalysis.SymbolKind.Local ||
            symbol.Kind == Microsoft.CodeAnalysis.SymbolKind.Parameter)
            return false;

        // Exclude special/compiler-generated symbols
        if (symbol.Name.StartsWith("_") || symbol.Name.StartsWith("<"))
            return true;

        // Exclude most System.* namespace types to reduce noise
        if (symbol is INamedTypeSymbol namedType)
        {
            var ns = namedType.ContainingNamespace?.ToDisplayString();
            if (ns?.StartsWith("System") == true || ns?.StartsWith("Microsoft") == true)
                return true;
        }

        // Keep everything else (user types, methods, etc.)
        return false;
    }

    private CompletionItemKind GetCompletionItemKind(ISymbol symbol)
    {
        return symbol.Kind switch
        {
            Microsoft.CodeAnalysis.SymbolKind.Method => CompletionItemKind.Method,
            Microsoft.CodeAnalysis.SymbolKind.Property => CompletionItemKind.Property,
            Microsoft.CodeAnalysis.SymbolKind.Field => CompletionItemKind.Field,
            Microsoft.CodeAnalysis.SymbolKind.Local => CompletionItemKind.Variable,
            Microsoft.CodeAnalysis.SymbolKind.Parameter => CompletionItemKind.Variable,
            Microsoft.CodeAnalysis.SymbolKind.NamedType => CompletionItemKind.Class,
            Microsoft.CodeAnalysis.SymbolKind.Namespace => CompletionItemKind.Module,
            Microsoft.CodeAnalysis.SymbolKind.Event => CompletionItemKind.Event,
            _ => CompletionItemKind.Text
        };
    }

    private List<MetadataReference> GetCompilationReferences()
    {
        var references = new List<MetadataReference>();

        // Add core runtime references
        var runtimePath = typeof(object).Assembly.Location;
        references.Add(MetadataReference.CreateFromFile(runtimePath));

        // Add System.Runtime
        var systemRuntimePath = typeof(System.Runtime.GCSettings).Assembly.Location;
        references.Add(MetadataReference.CreateFromFile(systemRuntimePath));

        // Add System.Collections
        var collectionsPath = typeof(System.Collections.Generic.List<>).Assembly.Location;
        references.Add(MetadataReference.CreateFromFile(collectionsPath));

        // Add System.Linq
        var linqPath = typeof(System.Linq.Enumerable).Assembly.Location;
        references.Add(MetadataReference.CreateFromFile(linqPath));

        // Add Microsoft.CodeAnalysis (for Blake API)
        var codeAnalysisPath = typeof(INamedTypeSymbol).Assembly.Location;
        references.Add(MetadataReference.CreateFromFile(codeAnalysisPath));

        return references;
    }

    private string GetBlakeApiCode()
    {
        // Simplified Blake API for IntelliSense
        // In a full implementation, we'd extract this from BlakeAPITemplate.cs
        return @"
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

public static class Blake
{
    public static IEnumerable<INamedTypeSymbol> FindTypesImplementing(string fullTypeName)
    {
        return Enumerable.Empty<INamedTypeSymbol>();
    }
}
";
    }

    protected override CompletionRegistrationOptions CreateRegistrationOptions(
        CompletionCapability capability,
        ClientCapabilities clientCapabilities)
    {
        return new CompletionRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("blake"),
            TriggerCharacters = new[] { ".", "@" },
            ResolveProvider = false
        };
    }
}
