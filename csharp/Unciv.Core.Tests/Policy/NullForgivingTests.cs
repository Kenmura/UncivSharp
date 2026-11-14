using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Unciv.Core.Tests.Policy;

// T119 / T425: Kotlin `x!!` throws at once; the C# null-forgiving `x!` only silences the compiler, so the failure
// shows up later or never. Production code must have zero null-forgiving operators: use `.NotNull()` (NullExtensions),
// required/nullable-safe initialization, MaybeNullWhen(false), or a real null check.
public class NullForgivingTests
{
    private static readonly string[] ScannedProjects =
    [
        "Unciv.Core",
        "Unciv.Harness",
        "Unciv.Godot.Harness",
        "Unciv.Godot",
        "Unciv.Assets",
        "Unciv.Server",
        "Unciv.Client.Net",
        "Unciv.Relay",
        "Unciv.Cli",
    ];

    [Fact]
    public void ProductionCodeHasNoNullForgivingOperator()
    {
        var root = FindRoot();
        var offenders = new List<string>();
        foreach (var project in ScannedProjects)
        {
            var dir = Path.Combine(root, project);
            Assert.True(Directory.Exists(dir), $"Expected project directory not found: {dir}");
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var sep = Path.DirectorySeparatorChar;
                if (file.Contains(sep + "obj" + sep, StringComparison.Ordinal) || file.Contains(sep + "bin" + sep, StringComparison.Ordinal)) continue;
                foreach (var line in FindNullForgivingLines(File.ReadAllText(file)))
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{line}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Null-forgiving '!' hides a null (Kotlin '!!' throws). Use .NotNull() or a null-safe rewrite: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void ScannerFindsOnlyRealNullForgivingOperators()
    {
        const string source = """
            var a = b!;
            var c = d!.e;
            var f = g![0];
            var h = (i)!;
            var j = k!!;
            var l = m<int>()!;
            var n = o! + 1;
            """;
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], FindNullForgivingLines(source).Distinct());

        const string benign = """"
            if (a != b && !c) { }
            var s = "x!";
            var v = @"x!""y";
            var i = $"hello {a}!";
            var r = """raw!""";
            var ch = '!';
            // comment!
            /* block! */
            var t = x ? !y : !z;
            var f = (Func<bool>)(() => !q);
            var g = !(a);
            var h = (bool)!x;
            var j = a >!b;
            var k = a > !b;
            var m = (bool)(!x);
            var p = (bool)!x.Foo();
            """";
        Assert.Empty(FindNullForgivingLines(benign));
    }

    [Fact]
    public void ScannerFindsNullForgivingInsideInterpolationHoles()
    {
        const string source = """"
            var s = $"hello {name!}";
            var raw = $$"""multi {{"inner" + value!}}""";
            var nested = $"outer {x ?? $"inner {y!}"}";
            var formatted = $"{z!,10:format}";
            """";
        Assert.Equal([1, 2, 3, 4], FindNullForgivingLines(source));
    }

    [Fact]
    public void ScannerFindsNullForgivingAtLineStartAfterNewline()
    {
        const string source = """
            var a = b
                !;
            """;
        Assert.Equal([2], FindNullForgivingLines(source));
    }

    internal static IEnumerable<int> FindNullForgivingLines(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            if (node is PostfixUnaryExpressionSyntax postfix && postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression))
            {
                yield return postfix.OperatorToken.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            }
            else if (node is CastExpressionSyntax cast
                     && cast.Expression is PrefixUnaryExpressionSyntax prefix
                     && prefix.IsKind(SyntaxKind.LogicalNotExpression)
                     && prefix.Operand.IsMissing)
            {
                yield return prefix.OperatorToken.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            }
        }
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Unciv.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Unciv.sln not found");
    }
}
