using System.Text.Json;
using System.Text.Json.Serialization;

namespace Unciv.Logic.Trade;

// core/src/com/unciv/logic/trade/TradeOfferType.kt

/// <summary>How the value number of a trade offer is formatted (<c>TradeOfferType.TradeTypeNumberType</c>).</summary>
public enum TradeTypeNumberType
{
    None,
    Simple,
    Gold,
}

/// <summary>Enum that classifies Trade Types. Wire names preserve the Kotlin save format.</summary>
[JsonConverter(typeof(TradeOfferTypeConverter))]
public enum TradeOfferType
{
    Embassy,
    Gold,
    GoldPerTurn,
    /// <summary>Treaties are shared by both sides - like peace treaty and defensive pact</summary>
    Treaty,
    /// <summary>Agreements are one-sided, like open borders</summary>
    Agreement,
    LuxuryResource,
    StrategicResource,
    StockpiledResource,
    Technology,
    Introduction,
    WarDeclaration,
    PeaceProposal,
    City,
}

/// <summary>The two constructor parameters of Kotlin's <c>TradeOfferType</c> enum entries.</summary>
public static class TradeOfferTypeExtensions
{
    /// <summary>How the value number is formatted - None, Simple number or with a Gold symbol.</summary>
    public static TradeTypeNumberType NumberType(this TradeOfferType type) => type switch
    {
        TradeOfferType.Embassy => TradeTypeNumberType.None,
        TradeOfferType.Gold => TradeTypeNumberType.Gold,
        TradeOfferType.GoldPerTurn => TradeTypeNumberType.Gold,
        TradeOfferType.Treaty => TradeTypeNumberType.None,
        TradeOfferType.Agreement => TradeTypeNumberType.None,
        TradeOfferType.LuxuryResource => TradeTypeNumberType.Simple,
        TradeOfferType.StrategicResource => TradeTypeNumberType.Simple,
        TradeOfferType.StockpiledResource => TradeTypeNumberType.Simple,
        TradeOfferType.Technology => TradeTypeNumberType.None,
        TradeOfferType.Introduction => TradeTypeNumberType.None,
        TradeOfferType.WarDeclaration => TradeTypeNumberType.None,
        TradeOfferType.PeaceProposal => TradeTypeNumberType.None,
        TradeOfferType.City => TradeTypeNumberType.None,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>Trade is a one-time effect without duration.</summary>
    public static bool IsImmediate(this TradeOfferType type) => type switch
    {
        TradeOfferType.Embassy => true,
        TradeOfferType.Gold => true,
        TradeOfferType.GoldPerTurn => false,
        TradeOfferType.Treaty => false,
        TradeOfferType.Agreement => false,
        TradeOfferType.LuxuryResource => false,
        TradeOfferType.StrategicResource => false,
        TradeOfferType.StockpiledResource => true,
        TradeOfferType.Technology => true,
        TradeOfferType.Introduction => true,
        TradeOfferType.WarDeclaration => true,
        TradeOfferType.PeaceProposal => true,
        TradeOfferType.City => true,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>Returns the Kotlin wire/save name for <paramref name="type"/>.</summary>
    public static string ToWireString(this TradeOfferType type) => type switch
    {
        TradeOfferType.GoldPerTurn => "Gold_Per_Turn",
        TradeOfferType.LuxuryResource => "Luxury_Resource",
        TradeOfferType.StrategicResource => "Strategic_Resource",
        TradeOfferType.StockpiledResource => "Stockpiled_Resource",
        _ => type.ToString(),
    };

    /// <summary>Parses a Kotlin wire/save name or C# enum name into <see cref="TradeOfferType"/>.</summary>
    public static bool TryParseWireString(string? value, out TradeOfferType result)
    {
        switch (value)
        {
            case "Gold_Per_Turn":
                result = TradeOfferType.GoldPerTurn;
                return true;
            case "Luxury_Resource":
                result = TradeOfferType.LuxuryResource;
                return true;
            case "Strategic_Resource":
                result = TradeOfferType.StrategicResource;
                return true;
            case "Stockpiled_Resource":
                result = TradeOfferType.StockpiledResource;
                return true;
            default:
                return Enum.TryParse(value, out result) && Enum.IsDefined(result);
        }
    }
}

internal sealed class TradeOfferTypeConverter : JsonConverter<TradeOfferType>
{
    public override TradeOfferType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        if (TradeOfferTypeExtensions.TryParseWireString(raw, out var value)) return value;
        throw new JsonException($"Unknown TradeOfferType '{raw}'.");
    }

    public override void Write(Utf8JsonWriter writer, TradeOfferType value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToWireString());
}
