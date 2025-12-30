// This template is embedded as a resource and injected into meta-block wrappers
// The usings and namespace context are provided by the wrapper

/// <summary>
/// Blake metaprogramming API - user code calls functions like Blake.FindTypesImplementing()
/// </summary>
public static class Blake
{
    private static Compilation? _compilation;

    public static void Initialize(Compilation? compilation)
    {
        _compilation = compilation;
    }

    public static IEnumerable<INamedTypeSymbol> FindTypesImplementing(string fullTypeName)
    {
        if (_compilation == null)
            throw new InvalidOperationException("Compilation not available in Blake context");

        return RoslynAssemblySearcher.FindTypesImplementing(_compilation, fullTypeName, null);
    }

    // Internal helper for type discovery
    internal static class RoslynAssemblySearcher
    {
        public static IEnumerable<INamedTypeSymbol> FindTypesImplementing(Compilation compilation, string fullTypeName, string? assemblySimpleName = null)
        {
            if (compilation is null) throw new ArgumentNullException(nameof(compilation));
            if (string.IsNullOrWhiteSpace(fullTypeName)) throw new ArgumentException("fullTypeName is required", nameof(fullTypeName));

            var target = compilation.GetTypeByMetadataName(fullTypeName) as INamedTypeSymbol;
            IAssemblySymbol? assemblyToSearch = null;
            if (!string.IsNullOrEmpty(assemblySimpleName))
            {
                assemblyToSearch = GetAssemblyBySimpleName(compilation, assemblySimpleName);
                if (target == null && assemblyToSearch != null)
                    target = FindTypeInAssemblyByMetadataName(assemblyToSearch, fullTypeName);
            }

            if (target == null) yield break;

            IEnumerable<IAssemblySymbol> assembliesToScan;
            if (!string.IsNullOrEmpty(assemblySimpleName) && assemblyToSearch != null)
                assembliesToScan = new[] { assemblyToSearch };
            else if (!string.IsNullOrEmpty(assemblySimpleName) && assemblyToSearch == null)
                yield break;
            else
            {
                var set = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default) { compilation.Assembly };
                foreach (var reference in compilation.References)
                {
                    var asm = compilation.GetAssemblyOrModuleSymbol(reference) as IAssemblySymbol;
                    if (asm != null) set.Add(asm);
                }
                assembliesToScan = set;
            }

            foreach (var asm in assembliesToScan)
            {
                foreach (var named in GetAllNamedTypes(asm.GlobalNamespace))
                {
                    if (SymbolEqualityComparer.Default.Equals(named, target)) continue;
                    if (IsAssignableTo(named, target)) yield return named;
                }
            }
        }

        private static IAssemblySymbol? GetAssemblyBySimpleName(Compilation comp, string simpleName)
        {
            if (comp.Assembly?.Identity?.Name != null && string.Equals(comp.Assembly.Identity.Name, simpleName, StringComparison.Ordinal))
                return comp.Assembly;

            foreach (var reference in comp.References)
            {
                var asm = comp.GetAssemblyOrModuleSymbol(reference) as IAssemblySymbol;
                if (asm != null && string.Equals(asm.Identity?.Name, simpleName, StringComparison.Ordinal))
                    return asm;
            }
            return null;
        }

        private static INamedTypeSymbol? FindTypeInAssemblyByMetadataName(IAssemblySymbol asm, string metadataName)
        {
            foreach (var t in GetAllNamedTypes(asm.GlobalNamespace))
            {
                if (SymbolMatchesMetadataName(t, metadataName))
                    return t;
            }
            return null;
        }

        private static bool SymbolMatchesMetadataName(INamedTypeSymbol t, string metadataName)
        {
            if (t.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat).Equals(metadataName, StringComparison.Ordinal))
                return true;

            var ns = t.ContainingNamespace?.ToDisplayString();
            if (!string.IsNullOrEmpty(ns) && (ns + "." + t.MetadataName).Equals(metadataName, StringComparison.Ordinal))
                return true;

            if (t.MetadataName.Equals(metadataName, StringComparison.Ordinal))
                return true;

            return false;
        }

        private static bool IsAssignableTo(INamedTypeSymbol candidate, INamedTypeSymbol targetType)
        {
            if (targetType.TypeKind == TypeKind.Interface)
            {
                foreach (var iface in candidate.AllInterfaces)
                {
                    if (SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, targetType.OriginalDefinition))
                        return true;
                }
                return false;
            }
            else
            {
                var t = candidate;
                while (t != null)
                {
                    if (SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, targetType.OriginalDefinition))
                        return true;
                    t = t.BaseType;
                }
                return false;
            }
        }

        private static IEnumerable<INamedTypeSymbol> GetAllNamedTypes(INamespaceSymbol ns)
        {
            foreach (var member in ns.GetMembers())
            {
                if (member is INamespaceSymbol childNs)
                {
                    foreach (var t in GetAllNamedTypes(childNs))
                        yield return t;
                }
                else if (member is INamedTypeSymbol named)
                {
                    foreach (var t in GetAllNamedTypes(named))
                        yield return t;
                }
            }
        }

        private static IEnumerable<INamedTypeSymbol> GetAllNamedTypes(INamedTypeSymbol namedType)
        {
            yield return namedType;
            foreach (var nested in namedType.GetTypeMembers())
            {
                foreach (var t in GetAllNamedTypes(nested))
                    yield return t;
            }
        }
    }
}
