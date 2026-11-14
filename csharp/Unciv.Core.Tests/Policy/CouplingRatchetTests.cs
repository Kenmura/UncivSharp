using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Unciv.Core.Tests.Policy;

// T121/T423: coupling ratchet. Counts the distinct `Unciv.*` namespaces a file depends on (`using` directives plus fully
// qualified references in code, minus the file's own namespace). Every file in Unciv.Core must stay at or below MaxNamespaces = 12.
// Also enforces layering: Unciv.Core must not reference Godot, harness assemblies, or any `Unciv.UI.*` namespace.
public class CouplingRatchetTests
{
    private const int MaxNamespaces = 12;
    private const string Core = "Unciv.Core";

    [Fact]
    public void FilesStayWithinNamespaceLimit()
    {
        var root = FindRoot();
        var current = Measure(root).ToDictionary(kv => kv.Key, kv => kv.Value.Count, StringComparer.Ordinal);

        var problems = new List<string>();
        foreach (var (key, count) in current.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (count > MaxNamespaces)
                problems.Add($"{key} depends on {count} Unciv namespaces (limit {MaxNamespaces}): decouple it");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void CoreDoesNotReferenceGodotHarnessOrUiNamespaces()
    {
        var root = FindRoot();
        var problems = new List<string>();
        foreach (var (file, namespaces) in Measure(root))
        {
            foreach (var ns in namespaces)
            {
                if (ns == "Godot" || ns.StartsWith("Godot.", StringComparison.Ordinal)
                    || ns == "Unciv.Harness" || ns.StartsWith("Unciv.Harness.", StringComparison.Ordinal)
                    || ns == "Unciv.Godot" || ns.StartsWith("Unciv.Godot.", StringComparison.Ordinal))
                    problems.Add($"{file} references {ns}: Unciv.Core must not depend on Godot or harness code");
                else if (ns == "Unciv.UI" || ns.StartsWith("Unciv.UI.", StringComparison.Ordinal))
                    problems.Add($"{file} references {ns}: Unciv.Core must not depend on UI namespaces");
            }
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void ScannerCountsUsingsAndQualifiedReferencesButNotOwnNamespace()
    {
        const string source = """
            using Unciv.Logic;
            using static Unciv.Utils.Foo;
            using Alias = Unciv.Models.Ruleset.Thing;
            using System.Text;
            namespace Unciv.Logic.Map;
            // Unciv.Comment.Nope x;
            class A
            {
                Unciv.Logic.Civilization.Civ c;
                Unciv.Logic.Map.Self s;
                string t = "Unciv.String.Nope x";
                void F() { var x = Unciv.Logic.City.City.Create(); }
            }
            """;
        Assert.Equal(
            ["Unciv.Logic", "Unciv.Logic.City", "Unciv.Logic.Civilization", "Unciv.Models.Ruleset", "Unciv.Utils"],
            Scan(source).Order(StringComparer.Ordinal));
    }

    // Namespace of a type reference = the dotted chain minus its last segment (the type); for a `using static` the
    // chain minus the type as well. Heuristic (syntax only, no binding), which is enough for a ratchet.
    internal static HashSet<string> Scan(string text)
    {
        var tree = CSharpSyntaxTree.ParseText(text);
        var rootNode = tree.GetRoot();
        var own = rootNode.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Select(n => n.Name.ToString()).ToHashSet(StringComparer.Ordinal);
        var result = new HashSet<string>(StringComparer.Ordinal);

        foreach (var u in rootNode.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            if (u.Name == null) continue;
            var name = u.Name.ToString();
            if (u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) || u.Alias != null) name = Parent(name);
            Add(result, own, name);
        }

        foreach (var node in rootNode.DescendantNodes().Where(n => n is QualifiedNameSyntax or MemberAccessExpressionSyntax))
        {
            if (node.Ancestors().Any(a => a is UsingDirectiveSyntax || (a is BaseNamespaceDeclarationSyntax ns && ns.Name.Span.Contains(node.Span)))) continue;
            if (node.Parent is QualifiedNameSyntax or MemberAccessExpressionSyntax) continue; // only maximal chains
            var chain = node.ToString();
            if (!chain.StartsWith("Unciv.", StringComparison.Ordinal) && chain != "Unciv") { AddForeign(result, chain); continue; }
            var segments = chain.Split('.').Select(s => s.Trim()).TakeWhile(s => s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_')).ToList();
            // Namespace = leading segments up to (not including) the first member that starts a type chain: we take all
            // but the last identifier, and for member accesses also drop the called member.
            var drop = node is MemberAccessExpressionSyntax ? 2 : 1;
            if (segments.Count > drop) Add(result, own, string.Join('.', segments.Take(segments.Count - drop)));
        }
        return result;
    }

    private static void AddForeign(HashSet<string> result, string chain)
    {
        if (chain.StartsWith("Godot.", StringComparison.Ordinal)) result.Add("Godot");
    }

    private static string Parent(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot < 0 ? name : name[..dot];
    }

    private static void Add(HashSet<string> result, HashSet<string> own, string ns)
    {
        if (ns != "Unciv" && !ns.StartsWith("Unciv.", StringComparison.Ordinal) && ns != "Godot" && !ns.StartsWith("Godot.", StringComparison.Ordinal)) return;
        if (own.Contains(ns)) return;
        result.Add(ns);
    }

    private static Dictionary<string, HashSet<string>> Measure(string root)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var dir = Path.Combine(root, Core);
        var sep = Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (file.Contains(sep + "obj" + sep, StringComparison.Ordinal) || file.Contains(sep + "bin" + sep, StringComparison.Ordinal)) continue;
            result[Path.GetRelativePath(root, file).Replace('\\', '/')] = Scan(File.ReadAllText(file));
        }
        return result;
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Unciv.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Unciv.sln not found");
    }
}
