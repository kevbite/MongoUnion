using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace MongoUnion.Serialization;

/// <summary>
/// Reflects over a C# 15 <c>union</c> type and caches the pieces a serializer needs:
/// the case types, the constructor that wraps each case, and the <c>Value</c> accessor.
/// </summary>
internal sealed class UnionInfo
{
    private static readonly ConcurrentDictionary<Type, UnionInfo> Cache = new();

    private readonly PropertyInfo _valueProperty;
    private readonly IReadOnlyDictionary<Type, ConstructorInfo> _constructorsByCaseType;

    public IReadOnlyList<Type> CaseTypes { get; }

    private UnionInfo(Type unionType)
    {
        // A union exposes a public get-only 'Value' property of type object holding the active case.
        _valueProperty = unionType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"Union type '{unionType}' does not expose a 'Value' property.");

        // The union's creation members are its single-parameter public constructors, one per case type.
        var constructors = new Dictionary<Type, ConstructorInfo>();
        foreach (var ctor in unionType.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            var parameters = ctor.GetParameters();
            if (parameters.Length == 1)
                constructors[parameters[0].ParameterType] = ctor;
        }

        if (constructors.Count == 0)
            throw new InvalidOperationException(
                $"Union type '{unionType}' has no single-argument creation constructors.");

        _constructorsByCaseType = constructors;
        CaseTypes = constructors.Keys.ToArray();
    }

    /// <summary>True when the type is a C# 15 union (marked with <see cref="UnionAttribute"/> / implements <see cref="IUnion"/>).</summary>
    public static bool IsUnion(Type type) =>
        typeof(IUnion).IsAssignableFrom(type) || type.IsDefined(typeof(UnionAttribute), inherit: false);

    public static UnionInfo For(Type unionType) => Cache.GetOrAdd(unionType, static t => new UnionInfo(t));

    /// <summary>Reads the active case value out of a union instance (null if the union is empty).</summary>
    public object? GetValue(object union) => _valueProperty.GetValue(union);

    /// <summary>Wraps a case value back into the union via its matching constructor.</summary>
    public object Create(Type caseType, object caseValue)
    {
        if (!_constructorsByCaseType.TryGetValue(caseType, out var ctor))
            throw new InvalidOperationException(
                $"No union constructor accepts a value of type '{caseType}'.");

        return ctor.Invoke(new[] { caseValue });
    }
}
