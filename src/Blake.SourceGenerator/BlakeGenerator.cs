using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Blake.SourceGenerator;

/// <summary>
/// Roslyn Incremental Source Generator that processes .blake files.
/// Blake is a C# superset that adds Boo-style metaprogramming via:
///   - @{| ... |} meta-blocks (code executed at compile time)
///   - @(expr) splices (interpolates meta-expression results into output)
/// </summary>
[Generator]
public class BlakeGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Find all .blake additional files
        var blakeFiles = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(".blake", StringComparison.OrdinalIgnoreCase));

        // Combine with compilation for access to referenced types if needed
        var compilationAndFiles = context.CompilationProvider.Combine(blakeFiles.Collect());

        // Register the generation output
        context.RegisterSourceOutput(compilationAndFiles, static (spc, source) =>
        {
            var (compilation, files) = source;
            
            foreach (var file in files)
            {
                ProcessBlakeFile(spc, file, compilation);
            }
        });
    }

    private static void ProcessBlakeFile(
        SourceProductionContext context, 
        AdditionalText file,
        Compilation compilation)
    {
        var sourceText = file.GetText(context.CancellationToken);
        if (sourceText is null)
            return;

        var fileName = Path.GetFileNameWithoutExtension(file.Path);
        var source = sourceText.ToString();

        try
        {
            // Parse the .blake file
            var parser = new BlakeParser(source, file.Path);
            var ast = parser.Parse();

            // Execute meta-blocks and expand splices
            var interpreter = new BlakeInterpreter(file.Path, compilation);
            var expandedCSharp = interpreter.Expand(ast);

            // Add the generated source
            var hintName = $"{fileName}.blake.g.cs";
            context.AddSource(hintName, SourceText.From(expandedCSharp, Encoding.UTF8));
        }
        catch (Exception ex) when (ex is MissingMethodException)
        {
            // Report Roslyn version conflict errors with full details
            var descriptor = new DiagnosticDescriptor(
                id: "BLAKE003",
                title: "Blake Roslyn Version Conflict",
                messageFormat: "Roslyn version conflict: {0}\nStack trace: {1}",
                category: "Blake",
                DiagnosticSeverity.Error,
                isEnabledByDefault: true);

            context.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None, ex.Message, ex.StackTrace));
        }
        catch (BlakeParseException ex)
        {
            // Report parsing errors as diagnostics
            var descriptor = new DiagnosticDescriptor(
                id: "BLAKE001",
                title: "Blake Parse Error",
                messageFormat: "{0}",
                category: "Blake",
                DiagnosticSeverity.Error,
                isEnabledByDefault: true);

            var location = Location.Create(
                file.Path,
                TextSpan.FromBounds(ex.Position, ex.Position + 1),
                new LinePositionSpan(
                    new LinePosition(ex.Line - 1, ex.Column - 1),
                    new LinePosition(ex.Line - 1, ex.Column)));

            context.ReportDiagnostic(Diagnostic.Create(descriptor, location, ex.Message));
        }
        catch (BlakeExecutionException ex)
        {
            // Report meta-block execution errors
            var descriptor = new DiagnosticDescriptor(
                id: "BLAKE002",
                title: "Blake Execution Error",
                messageFormat: "Error executing meta-block: {0}",
                category: "Blake",
                DiagnosticSeverity.Error,
                isEnabledByDefault: true);

            context.ReportDiagnostic(Diagnostic.Create(descriptor, Location.None, ex.Message));
        }
    }
}

/// <summary>
/// Exception thrown when parsing a .blake file fails.
/// </summary>
public class BlakeParseException : Exception
{
    public int Position { get; }
    public int Line { get; }
    public int Column { get; }

    public BlakeParseException(string message, int position, int line, int column)
        : base(message)
    {
        Position = position;
        Line = line;
        Column = column;
    }
}

/// <summary>
/// Exception thrown when executing a meta-block fails.
/// </summary>
public class BlakeExecutionException : Exception
{
    public BlakeExecutionException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
