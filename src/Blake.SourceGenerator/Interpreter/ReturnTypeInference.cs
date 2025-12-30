using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Blake.SourceGenerator.Interpreter;

/// <summary>
/// Handles automatic return type inference for methods with 'var' return types.
/// Transforms 'public var GetName() { return "Blake"; }' into 'public string GetName() { return "Blake"; }'.
/// </summary>
public static class ReturnTypeInference
{
    /// <summary>
    /// Applies var return type inference transformation to complete C# source code.
    /// </summary>
    public static string ApplyTransformation(string completeSource, Compilation? compilation)
    {
        try
        {
            return TransformVarReturnTypes(completeSource, compilation);
        }
        catch (Exception ex) when (ex is not BlakeExecutionException)
        {
            throw new BlakeExecutionException(
                $"Error transforming var return types: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Transformation: Infers 'var' return types for methods/local functions.
    /// </summary>
    private static string TransformVarReturnTypes(string code, Compilation? compilation)
    {
        // Parse complete C# source with Roslyn
        // Roslyn's parser is resilient - it will create syntax nodes even if 'var'
        // is invalid as a return type (error recovery)
        var syntaxTree = CSharpSyntaxTree.ParseText(code,
            new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.None));
        var root = syntaxTree.GetRoot();

        // Check if any methods/local functions have 'var' return type
        var methodsWithVar = root.DescendantNodes()
            .Where(n => IsMethodWithVarReturnType(n))
            .ToList();

        if (methodsWithVar.Count == 0)
        {
            return code; // Fast path: no var return types found
        }

        // Now do semantic analysis and type inference
        return InferAndReplaceVarReturnTypes(syntaxTree, methodsWithVar, compilation);
    }

    /// <summary>
    /// Infers and replaces 'var' return types in the given syntax tree.
    /// Uses Roslyn's ReplaceNodes API for efficient, clean replacement.
    /// </summary>
    private static string InferAndReplaceVarReturnTypes(SyntaxTree syntaxTree, List<SyntaxNode> methodsWithVar, Compilation? compilation)
    {
        // Create compilation for semantic analysis
        var tempCompilation = CreateTemporaryCompilation(syntaxTree, compilation);
        var semanticModel = tempCompilation.GetSemanticModel(syntaxTree);

        // Build replacement map: old method node → new method node with inferred type
        var replacements = new Dictionary<SyntaxNode, SyntaxNode>();

        foreach (var methodNode in methodsWithVar)
        {
            // Infer the return type
            var inferredType = InferReturnType(methodNode, semanticModel);

            // Create new return type syntax
            var newReturnType = SyntaxFactory.ParseTypeName(inferredType)
                .WithTriviaFrom(GetReturnTypeNode(methodNode));

            // Replace the 'var' return type with the inferred type
            SyntaxNode newMethodNode;
            if (methodNode is MethodDeclarationSyntax method)
            {
                newMethodNode = method.WithReturnType(newReturnType);
            }
            else if (methodNode is LocalFunctionStatementSyntax localFunc)
            {
                newMethodNode = localFunc.WithReturnType(newReturnType);
            }
            else
            {
                continue; // Shouldn't happen
            }

            replacements[methodNode] = newMethodNode;
        }

        // Apply all replacements at once using Roslyn's ReplaceNodes API
        var root = syntaxTree.GetRoot();
        var newRoot = root.ReplaceNodes(replacements.Keys, (oldNode, _) => replacements[oldNode]);

        return newRoot.ToFullString();
    }

    /// <summary>
    /// Infers the return type of a method by analyzing its return statements.
    /// </summary>
    private static string InferReturnType(SyntaxNode methodNode, SemanticModel semanticModel)
    {
        var methodSignature = GetMethodSignature(methodNode);
        var body = GetMethodBody(methodNode);

        if (body == null)
        {
            throw new BlakeExecutionException(
                $"Cannot infer return type for method {methodSignature}: method has no body (abstract/interface methods not supported)");
        }

        var isAsync = IsAsyncMethod(methodNode);

        // Handle expression-bodied members (e.g., public var Get() => 42;)
        if (body is ArrowExpressionClauseSyntax arrowExpr)
        {
            var typeInfo = semanticModel.GetTypeInfo(arrowExpr.Expression);
            if (typeInfo.Type == null)
            {
                throw new BlakeExecutionException(
                    $"Cannot infer return type for method {methodSignature}: unable to determine type of expression");
            }

            var exprTypeString = typeInfo.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

            // Wrap in Task<T> if async
            if (isAsync)
            {
                return $"Task<{exprTypeString}>";
            }

            return exprTypeString;
        }

        // Check for iterator (yield return)
        if (HasYieldStatement(body))
        {
            var elementType = InferYieldReturnType(body, semanticModel, methodSignature);
            return isAsync ? $"IAsyncEnumerable<{elementType}>" : $"IEnumerable<{elementType}>";
        }

        // Find all return statements with expressions
        var returnStatements = body.DescendantNodes()
            .OfType<ReturnStatementSyntax>()
            .Where(r => r.Expression != null)
            .ToList();

        if (returnStatements.Count == 0)
        {
            // No return statements with values → void or Task
            return isAsync ? "Task" : "void";
        }

        // Infer types from all return expressions
        var returnTypes = new List<ITypeSymbol>();
        foreach (var returnStmt in returnStatements)
        {
            if (returnStmt.Expression != null)
            {
                var typeInfo = semanticModel.GetTypeInfo(returnStmt.Expression);
                if (typeInfo.Type != null)
                {
                    returnTypes.Add(typeInfo.Type);
                }
            }
        }

        // Verify all return types are exactly equal
        var returnType = VerifyExactTypeMatch(returnTypes, methodSignature);

        var typeString = returnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        // Wrap in Task<T> if async
        if (isAsync)
        {
            return $"Task<{typeString}>";
        }

        return typeString;
    }

    /// <summary>
    /// Infers the element type for iterator methods (methods with yield return).
    /// </summary>
    private static string InferYieldReturnType(SyntaxNode body, SemanticModel semanticModel, string methodSignature)
    {
        var yieldReturns = body.DescendantNodes()
            .OfType<YieldStatementSyntax>()
            .Where(y => y.Expression != null)
            .ToList();

        if (yieldReturns.Count == 0)
        {
            // Only yield break - return object
            return "object";
        }

        // Get types of all yield return expressions
        var types = new List<ITypeSymbol>();
        foreach (var yieldReturn in yieldReturns)
        {
            if (yieldReturn.Expression != null)
            {
                var typeInfo = semanticModel.GetTypeInfo(yieldReturn.Expression);
                if (typeInfo.Type != null)
                {
                    types.Add(typeInfo.Type);
                }
            }
        }

        // Verify all yield return types are exactly equal
        var elementType = VerifyExactTypeMatch(types, methodSignature);

        return elementType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
    }

    /// <summary>
    /// Verifies that all types in the list are exactly equal.
    /// Throws BlakeExecutionException if types don't match exactly.
    /// </summary>
    private static ITypeSymbol VerifyExactTypeMatch(List<ITypeSymbol> types, string methodSignature)
    {
        if (types.Count == 0)
        {
            throw new BlakeExecutionException(
                $"Cannot infer return type for method {methodSignature}: no return statements with values found");
        }

        var firstType = types[0];

        // Check that all types are exactly equal to the first type
        for (int i = 1; i < types.Count; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(firstType, types[i]))
            {
                var firstTypeName = firstType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                var otherTypeName = types[i].ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

                throw new BlakeExecutionException(
                    $"Cannot infer return type for method {methodSignature}: return statements have conflicting types '{firstTypeName}' and '{otherTypeName}'. All return types must be exactly equal.");
            }
        }

        return firstType;
    }

    /// <summary>
    /// Creates a temporary compilation for semantic analysis of the generated code.
    /// </summary>
    private static CSharpCompilation CreateTemporaryCompilation(SyntaxTree syntaxTree, Compilation? compilation)
    {
        // Reuse references from main compilation if available
        var references = compilation?.References.ToList() ?? GetDefaultReferences();

        return CSharpCompilation.Create(
            assemblyName: $"TempInference_{Guid.NewGuid():N}",
            syntaxTrees: new[] { syntaxTree },
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    /// <summary>
    /// Gets default metadata references for compilation when no compilation context is available.
    /// </summary>
    private static List<MetadataReference> GetDefaultReferences()
    {
        var refs = new List<MetadataReference>();

        // System runtime assemblies
        refs.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location));

        // Try to add System.Runtime if available
        var runtimePath = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimePath != null)
        {
            var systemRuntime = Path.Combine(runtimePath, "System.Runtime.dll");
            if (File.Exists(systemRuntime))
            {
                refs.Add(MetadataReference.CreateFromFile(systemRuntime));
            }
        }

        return refs;
    }

    /// <summary>
    /// Checks if a syntax node is a method or local function with 'var' return type.
    /// </summary>
    private static bool IsMethodWithVarReturnType(SyntaxNode node)
    {
        if (node is MethodDeclarationSyntax method)
        {
            return method.ReturnType is IdentifierNameSyntax id && id.Identifier.Text == "var";
        }

        if (node is LocalFunctionStatementSyntax localFunc)
        {
            return localFunc.ReturnType is IdentifierNameSyntax id && id.Identifier.Text == "var";
        }

        return false;
    }

    /// <summary>
    /// Gets the body of a method or local function (either block body or expression body).
    /// </summary>
    private static SyntaxNode? GetMethodBody(SyntaxNode methodNode)
    {
        if (methodNode is MethodDeclarationSyntax method)
        {
            return (SyntaxNode?)method.Body ?? method.ExpressionBody;
        }

        if (methodNode is LocalFunctionStatementSyntax localFunc)
        {
            return (SyntaxNode?)localFunc.Body ?? localFunc.ExpressionBody;
        }

        return null;
    }

    /// <summary>
    /// Checks if a method or local function has the async modifier.
    /// </summary>
    private static bool IsAsyncMethod(SyntaxNode methodNode)
    {
        if (methodNode is MethodDeclarationSyntax method)
        {
            return method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword));
        }

        if (methodNode is LocalFunctionStatementSyntax localFunc)
        {
            return localFunc.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword));
        }

        return false;
    }

    /// <summary>
    /// Checks if a method body contains yield return or yield break statements.
    /// </summary>
    private static bool HasYieldStatement(SyntaxNode body)
    {
        return body.DescendantNodes().Any(n =>
            n.IsKind(SyntaxKind.YieldReturnStatement) ||
            n.IsKind(SyntaxKind.YieldBreakStatement));
    }

    /// <summary>
    /// Gets a readable method signature for error messages.
    /// </summary>
    private static string GetMethodSignature(SyntaxNode methodNode)
    {
        if (methodNode is MethodDeclarationSyntax method)
        {
            return $"{method.Identifier.Text}({string.Join(", ", method.ParameterList.Parameters.Select(p => p.Type + " " + p.Identifier))})";
        }

        if (methodNode is LocalFunctionStatementSyntax localFunc)
        {
            return $"{localFunc.Identifier.Text}({string.Join(", ", localFunc.ParameterList.Parameters.Select(p => p.Type + " " + p.Identifier))})";
        }

        return methodNode.ToString();
    }

    /// <summary>
    /// Gets the return type syntax node from a method or local function.
    /// </summary>
    private static TypeSyntax GetReturnTypeNode(SyntaxNode methodNode)
    {
        if (methodNode is MethodDeclarationSyntax method)
        {
            return method.ReturnType;
        }

        if (methodNode is LocalFunctionStatementSyntax localFunc)
        {
            return localFunc.ReturnType;
        }

        throw new InvalidOperationException("Node is not a method or local function");
    }
}
