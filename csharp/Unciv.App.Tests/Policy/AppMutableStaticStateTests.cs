using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Unciv.Core.Tests.Policy;

// Policy (T424): Unciv.Client.Net, Unciv.Server, and Unciv.Relay must not hold shared mutable static state.
public class AppMutableStaticStateTests
{
    private static readonly Assembly ClientNetAssembly = typeof(Unciv.Client.Net.Session.ClientSession).Assembly;
    private static readonly Assembly ServerAssembly = typeof(Unciv.Server.ServerRunner).Assembly;
    private static readonly Assembly RelayAssembly = typeof(Unciv.Relay.RelayServer).Assembly;

    private const BindingFlags AllStatics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    [Fact]
    public void ClientNetHasNoMutableStaticState()
    {
        var violations = FindViolations(ClientNetAssembly).Select(v => $"{v.Key}: {v.Reason}").ToList();
        Assert.True(violations.Count == 0,
            "Mutable static state in Unciv.Client.Net (make it instance state or immutable):\n" + string.Join("\n", violations));
    }

    [Fact]
    public void ServerHasNoMutableStaticState()
    {
        var violations = FindViolations(ServerAssembly).Select(v => $"{v.Key}: {v.Reason}").ToList();
        Assert.True(violations.Count == 0,
            "Mutable static state in Unciv.Server (make it instance state or immutable):\n" + string.Join("\n", violations));
    }

    [Fact]
    public void RelayHasNoMutableStaticState()
    {
        var violations = FindViolations(RelayAssembly).Select(v => $"{v.Key}: {v.Reason}").ToList();
        Assert.True(violations.Count == 0,
            "Mutable static state in Unciv.Relay (make it instance state or immutable):\n" + string.Join("\n", violations));
    }

    private static List<(string Key, string Reason)> FindViolations(Assembly assembly)
    {
        var result = new List<(string, string)>();
        foreach (var type in assembly.GetTypes())
        {
            if (type.GetCustomAttribute<CompilerGeneratedAttribute>() != null || type.FullName?.StartsWith('<') == true) continue;
            if (IsInsideCompilerGeneratedType(type)) continue;
            if (type.FullName?.StartsWith("Coverlet.Core.Instrumentation.Tracker", StringComparison.Ordinal) == true) continue;
            var typeName = type.FullName!;

            foreach (var field in type.GetFields(AllStatics))
            {
                if (field.IsLiteral) continue;
                if (field.GetCustomAttribute<CompilerGeneratedAttribute>() != null && !field.Name.StartsWith('<')) continue;
                var name = BackingPropertyName(field) ?? field.Name;
                if (name.StartsWith('<')) continue;
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

    private static bool IsMutableCollectionType(Type type)
    {
        if (type.IsArray) return true;
        if (type.Namespace is "System.Collections.Immutable" or "System.Collections.Frozen") return false;
        if (type.Name.StartsWith("ReadOnly", StringComparison.Ordinal)) return false;
        return type.GetInterfaces().Append(type).Any(i => i == typeof(ICollection) || i == typeof(IList) || i == typeof(IDictionary) ||
            (i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(ICollection<>) || i.GetGenericTypeDefinition() == typeof(ISet<>))));
    }
}
