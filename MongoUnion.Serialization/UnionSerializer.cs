using System.Collections.Concurrent;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace MongoUnion.Serialization;

/// <summary>
/// Serializes a C# 15 <c>union</c> to BSON the same way System.Text.Json does: the active case's
/// value is written directly, with no wrapper object and no type discriminator.
///
/// Because no discriminator is emitted, the case is recovered on read purely from the value's shape,
/// scalar cases by their BSON type, object cases by their set of element names. This means two cases
/// that produce the same BSON shape (e.g. two records with identical properties) cannot be told apart
/// and are reported as ambiguous, mirroring System.Text.Json's stance on same-token unions.
/// </summary>
public sealed class UnionSerializer<TUnion> : SerializerBase<TUnion>
{
    private static readonly ConcurrentDictionary<Type, HashSet<string>> ElementNameCache = new();

    private readonly UnionInfo _info = UnionInfo.For(typeof(TUnion));

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, TUnion value)
    {
        var caseValue = _info.GetValue(value!);
        if (caseValue is null)
        {
            context.Writer.WriteNull();
            return;
        }

        // Delegate to the active case type's own serializer, setting the nominal type to the case
        // type so the class-map serializer treats it as non-polymorphic and writes no '_t' discriminator.
        var caseType = caseValue.GetType();
        var caseArgs = args;
        caseArgs.NominalType = caseType;
        BsonSerializer.LookupSerializer(caseType).Serialize(context, caseArgs, caseValue);
    }

    public override TUnion Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var bsonType = context.Reader.GetCurrentBsonType();
        if (bsonType == BsonType.Null)
        {
            context.Reader.ReadNull();
            return default!;
        }

        Type caseType;
        object caseValue;

        if (bsonType == BsonType.Document)
        {
            // Buffer the document so we can inspect its shape before committing to a case type,
            // then deserialize the buffered document into the chosen case.
            var document = BsonDocumentSerializer.Instance.Deserialize(context);
            caseType = SelectDocumentCase(document);

            // Drop MongoDB's injected '_id' before deserializing into a case that doesn't map it,
            // otherwise the case's class-map serializer rejects it as an extra element.
            if (!ElementNamesFor(caseType).Contains("_id"))
                document.Remove("_id");

            caseValue = BsonSerializer.Deserialize(document, caseType);
        }
        else
        {
            caseType = SelectScalarCase(bsonType);
            caseValue = BsonSerializer.LookupSerializer(caseType).Deserialize(context, args);
        }

        return (TUnion)_info.Create(caseType, caseValue);
    }

    private Type SelectDocumentCase(BsonDocument document)
    {
        var elementNames = new HashSet<string>(document.Names);

        var matches = _info.CaseTypes
            .Where(t => Matches(ElementNamesFor(t), elementNames))
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new BsonSerializationException(
                $"No case of union '{typeof(TUnion)}' matches document fields {{{string.Join(", ", elementNames)}}}."),
            _ => throw new BsonSerializationException(
                $"Document fields {{{string.Join(", ", elementNames)}}} are ambiguous across union '{typeof(TUnion)}' " +
                $"cases {string.Join(", ", matches.Select(m => m.Name))}; a discriminator would be required to disambiguate.")
        };
    }

    private Type SelectScalarCase(BsonType bsonType)
    {
        var clrType = bsonType switch
        {
            BsonType.String => typeof(string),
            BsonType.Int32 => typeof(int),
            BsonType.Int64 => typeof(long),
            BsonType.Boolean => typeof(bool),
            BsonType.Double => typeof(double),
            BsonType.Decimal128 => typeof(decimal),
            BsonType.DateTime => typeof(DateTime),
            BsonType.ObjectId => typeof(ObjectId),
            BsonType.Binary => typeof(Guid),
            _ => null
        };

        var matches = _info.CaseTypes.Where(t => t == clrType).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new BsonSerializationException(
                $"No case of union '{typeof(TUnion)}' maps to BSON type '{bsonType}'."),
            _ => throw new BsonSerializationException(
                $"BSON type '{bsonType}' is ambiguous across union '{typeof(TUnion)}' cases.")
        };
    }

    private static bool Matches(HashSet<string> caseNames, HashSet<string> documentNames)
    {
        if (caseNames.SetEquals(documentNames))
            return true;

        // MongoDB injects an '_id' when persisting. Ignore it during matching unless a case
        // type maps its own '_id' member.
        if (caseNames.Contains("_id") || !documentNames.Contains("_id"))
            return false;

        var withoutId = new HashSet<string>(documentNames);
        withoutId.Remove("_id");
        return caseNames.SetEquals(withoutId);
    }

    private static HashSet<string> ElementNamesFor(Type caseType) =>
        ElementNameCache.GetOrAdd(caseType, static t =>
            new HashSet<string>(BsonClassMap.LookupClassMap(t).AllMemberMaps.Select(m => m.ElementName)));
}
