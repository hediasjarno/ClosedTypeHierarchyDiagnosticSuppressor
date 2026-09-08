using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace SvSoft.Analyzers.ClosedTypeHerarchyDiagnosticSuppression;

public static class TypeHierarchyHelper
{
    public static IEnumerable<INamedTypeSymbol>? InterpretAsClosedTypeHierarchy(INamedTypeSymbol typeSymbol, bool allowRecords, Compilation compilation)
    {
        if (!IsPartOfClosedHierarchy(typeSymbol))
        {
            return null;
        }

        return GetConcreteSubtypes(typeSymbol);

        bool IsPartOfClosedHierarchy(INamedTypeSymbol typeSymbol)
        {
            if (CanBeClosedHierarchyRoot(typeSymbol))
            {
                var subtypes = GetDirectSubtypes(typeSymbol);

                return subtypes.All(IsPartOfClosedHierarchy);
            }

            return CanBeClosedHierarchyLeaf(typeSymbol);
        }

        IEnumerable<INamedTypeSymbol> GetConcreteSubtypes(INamedTypeSymbol typeSymbol)
        {
            if (CanBeClosedHierarchyRoot(typeSymbol))
            {
                foreach (var subtype in GetDirectSubtypes(typeSymbol))
                {
                    foreach (var concreteType in GetConcreteSubtypes(subtype))
                    {
                        yield return concreteType;
                    }
                }
            }

            if (CanBeClosedHierarchyLeaf(typeSymbol))
            {
                yield return typeSymbol;
            }
        }

        IEnumerable<INamedTypeSymbol> GetDirectSubtypes(INamedTypeSymbol baseType)
        {
            var nestedSubtypes = baseType.GetMembers().OfType<INamedTypeSymbol>()
                .Where(t => baseType.Equals(t.BaseType, SymbolEqualityComparer.Default))
                .ToArray();

            if (baseType.IsGenericType)
            {
                return nestedSubtypes;
            }

            var siblingSubtypes = GetAllNamedTypes(compilation).Where(t =>
                baseType.Equals(t.BaseType, SymbolEqualityComparer.Default) &&
                !nestedSubtypes.Any(n => SymbolEqualityComparer.Default.Equals(n, t)));

            return nestedSubtypes.Concat(siblingSubtypes);
        }

        bool CanBeClosedHierarchyRoot(INamedTypeSymbol rootCandidate) =>
            rootCandidate.IsAbstract &&
            (allowRecords
                ? HasOnlyClosingConstructorsAndProtectedCopyCtors(rootCandidate)
                : HasOnlyClosingConstructors(rootCandidate));

        static bool HasOnlyClosingConstructors(INamedTypeSymbol rootCandidate) =>
            rootCandidate.Constructors.All(c => IsClosingAccessibility(c.DeclaredAccessibility));

        static bool IsClosingAccessibility(Accessibility accessibility) =>
            accessibility is Accessibility.Private or Accessibility.ProtectedAndInternal;

        static bool HasOnlyClosingConstructorsAndProtectedCopyCtors(INamedTypeSymbol rootCandidate) =>
            HasOnlyClosingConstructors(rootCandidate) ||
            (IsRecord(rootCandidate) &&
            rootCandidate.Constructors.All(c => IsClosingAccessibility(c.DeclaredAccessibility) || MatchesImplicitlyCreatedRecordCopyCtor(rootCandidate, c)));

#pragma warning disable CS0162 // Unreachable code detected, FP, see https://github.com/dotnet/roslyn/issues/41429
        const string CompilerCreatedCloneMethodNameOnRecordTypes = "<Clone>$";
#pragma warning restore CS0162 // Unreachable code detected

        static bool IsRecord(INamedTypeSymbol recordCandidate) =>
            recordCandidate.MemberNames.Contains(CompilerCreatedCloneMethodNameOnRecordTypes);

        static bool MatchesImplicitlyCreatedRecordCopyCtor(INamedTypeSymbol constructedType, IMethodSymbol ctor) =>
            ctor.DeclaredAccessibility == Accessibility.Protected &&
            ctor.Parameters.Length == 1 &&
            ctor.Parameters[0].Type.Equals(constructedType, SymbolEqualityComparer.Default);

        static bool CanBeClosedHierarchyLeaf(INamedTypeSymbol typeSymbol) => typeSymbol.IsSealed;
    }

    static readonly ConditionalWeakTable<Compilation, IReadOnlyCollection<INamedTypeSymbol>> AllNamedTypesByCompilation = new();

    static IReadOnlyCollection<INamedTypeSymbol> GetAllNamedTypes(Compilation compilation) =>
        AllNamedTypesByCompilation.GetValue(compilation, ComputeAllNamedTypes);

    static IReadOnlyCollection<INamedTypeSymbol> ComputeAllNamedTypes(Compilation compilation)
    {
        var result = new List<INamedTypeSymbol>();

        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            SemanticModel model = compilation.GetSemanticModel(tree);

            foreach (TypeDeclarationSyntax typeDeclaration in tree.GetRoot().DescendantNodesAndSelf().OfType<TypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(typeDeclaration) is INamedTypeSymbol symbol)
                {
                    result.Add(symbol);
                }
            }
        }

        return result;
    }
}
