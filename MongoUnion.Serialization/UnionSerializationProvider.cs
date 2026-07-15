using System.Threading;
using MongoDB.Bson.Serialization;

namespace MongoUnion.Serialization;

/// <summary>
/// Resolves a <see cref="UnionSerializer{TUnion}"/> for any C# 15 union type, and defers everything
/// else to the driver's other providers by returning <c>null</c>. Registered ahead of the built-in
/// providers, this intercepts unions before the class-map provider tries (and fails) to treat them
/// as ordinary POCOs.
/// </summary>
public sealed class UnionSerializationProvider : IBsonSerializationProvider
{
    public IBsonSerializer? GetSerializer(Type type)
    {
        if (!UnionInfo.IsUnion(type))
            return null;

        var serializerType = typeof(UnionSerializer<>).MakeGenericType(type);
        return (IBsonSerializer)Activator.CreateInstance(serializerType)!;
    }
}

/// <summary>Entry point for registering union support with the BSON serializer.</summary>
public static class UnionSerialization
{
    private static bool _registered;
    private static readonly Lock Gate = new();

    /// <summary>
    /// Registers the union serialization provider. Safe to call more than once; only the first call
    /// takes effect.
    /// </summary>
    public static void Register()
    {
        lock (Gate)
        {
            if (_registered)
                return;

            BsonSerializer.RegisterSerializationProvider(new UnionSerializationProvider());
            _registered = true;
        }
    }
}
