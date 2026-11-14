using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Unciv.Mathematics;

namespace Unciv.Core.Tests.Policy;

// Policy (T117, T424): Unciv.Core and Unciv.Assets must not hold shared mutable static state; it breaks determinism and makes parallel tests
// flaky. Flagged: non-readonly/non-const static fields, public static properties with setters, and static readonly
// fields of mutable collection types.
public class MutableStaticStateTests
{
    private static readonly Assembly CoreAssembly = typeof(Vector2).Assembly;
    private static readonly Assembly AssetsAssembly = typeof(Unciv.Assets.Localization.ClientLocalization).Assembly;

    private const BindingFlags AllStatics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    [Fact]
    public void CoreHasNoMutableStaticState()
    {
        var violations = FindViolations(CoreAssembly).Select(v => $"{v.Key}: {v.Reason}").ToList();
        Assert.True(violations.Count == 0,
            "Mutable static state in Unciv.Core (make it instance state or immutable):\n" + string.Join("\n", violations));
    }

    [Fact]
    public void AssetsHasNoMutableStaticState()
    {
        var violations = FindViolations(AssetsAssembly).Select(v => $"{v.Key}: {v.Reason}").ToList();
        Assert.True(violations.Count == 0,
            "Mutable static state in Unciv.Assets (make it instance state or immutable):\n" + string.Join("\n", violations));
    }

    public static List<(string Key, string Reason)> FindViolations(Assembly assembly)
    {
        var result = new List<(string, string)>();
        foreach (var type in assembly.GetTypes())
        {
            if (type.GetCustomAttribute<CompilerGeneratedAttribute>() != null || type.FullName?.StartsWith('<') == true) continue;
            if (IsInsideCompilerGeneratedType(type)) continue;
            if (type.FullName?.StartsWith("Coverlet.Core.Instrumentation.Tracker", StringComparison.Ordinal) == true) continue; // injected by coverage runs (tools/coverage.py)
            var typeName = type.FullName!;

            foreach (var field in type.GetFields(AllStatics))
            {
                if (field.IsLiteral) continue; // const
                if (field.GetCustomAttribute<CompilerGeneratedAttribute>() != null && field.Name.StartsWith('<') == false) continue;
                var name = BackingPropertyName(field) ?? field.Name;
                if (name.StartsWith('<')) continue; // compiler cache fields
                var key = $"{typeName}.{name}";
                if (!field.IsInitOnly)
                    result.Add((key, "static field is not readonly"));
                else if (IsMutableCollectionType(field.FieldType))
                    result.Add((key, $"static readonly field of mutable type {field.FieldType.Name}"));
            }

            foreach (var property in type.GetProperties(AllStatics))
            {
                var setter = property.SetMethod;
                if (setter == null || !setter.IsPublic) continue;
                result.Add(($"{typeName}.{property.Name}", "public static property has a setter"));
            }
        }
        return result.Distinct().OrderBy(r => r.Item1, StringComparer.Ordinal).ToList();
    }

    private static bool IsInsideCompilerGeneratedType(Type type)
    {
        for (var t = type.DeclaringType; t != null; t = t.DeclaringType)
            if (t.GetCustomAttribute<CompilerGeneratedAttribute>() != null) return true;
        return false;
    }

    private static string? BackingPropertyName(FieldInfo field)
    {
        const string suffix = ">k__BackingField";
        return field.Name.StartsWith('<') && field.Name.EndsWith(suffix, StringComparison.Ordinal) ? field.Name[1..^suffix.Length] : null;
    }

    // Mutable if it is an array or exposes collection mutation (ICollection/IList/IDictionary, generic or not, also when
    // declared as an interface type). Read-only wrappers, frozen and immutable collections are fine.
    internal static bool IsMutableCollectionType(Type type)
    {
        if (type.IsArray) return true;
        if (type.Namespace is "System.Collections.Immutable" or "System.Collections.Frozen") return false;
        if (type.Name.StartsWith("ReadOnly", StringComparison.Ordinal)) return false;
        return type.GetInterfaces().Append(type).Any(i => i == typeof(ICollection) || i == typeof(IList) || i == typeof(IDictionary) ||
            (i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(ICollection<>) || i.GetGenericTypeDefinition() == typeof(ISet<>))));
    }

    [Theory]
    [InlineData(typeof(Queue<int>), true)]
    [InlineData(typeof(Stack<int>), true)]
    [InlineData(typeof(System.Collections.Concurrent.ConcurrentQueue<int>), true)]
    [InlineData(typeof(System.Collections.Concurrent.ConcurrentDictionary<int, int>), true)]
    [InlineData(typeof(IList<int>), true)]
    [InlineData(typeof(IDictionary<int, int>), true)]
    [InlineData(typeof(ISet<int>), true)]
    [InlineData(typeof(HashSet<int>), true)]
    [InlineData(typeof(int[]), true)]
    [InlineData(typeof(IReadOnlyList<int>), false)]
    [InlineData(typeof(IReadOnlyDictionary<int, int>), false)]
    [InlineData(typeof(System.Collections.ObjectModel.ReadOnlyCollection<int>), false)]
    [InlineData(typeof(System.Collections.Frozen.FrozenDictionary<int, int>), false)]
    [InlineData(typeof(System.Collections.Immutable.ImmutableArray<int>), false)]
    [InlineData(typeof(string), false)]
    public void DetectorClassifiesCollectionTypes(Type type, bool mutable) =>
        Assert.Equal(mutable, IsMutableCollectionType(type));
}
