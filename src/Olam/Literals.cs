using System.Globalization;
using System.Xml;

namespace Olam;

internal static class Literals
{
    internal static string Format(object? value, ODataQueryOptions options) => value switch
    {
        null => "null",
        string text => "'" + text.Replace("'", "''") + "'",
        char character => Format(character.ToString(), options),
        bool boolean => boolean ? "true" : "false",
        Enum enumeration => EnumLiteral(enumeration, options),
        Guid guid => guid.ToString("D"),
        ODataDate date => date.ToLiteral(),
#if NET6_0_OR_GREATER
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
#endif
        DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
        DateTime { Kind: DateTimeKind.Unspecified } => throw new NotSupportedException(
            "DateTime literals must specify UTC or Local kind. Prefer DateTimeOffset for timestamps."),
        DateTime date => new DateTimeOffset(date).ToString("O", CultureInfo.InvariantCulture),
        TimeSpan duration => "duration'" + XmlConvert.ToString(duration) + "'",
        byte[] bytes => "binary'" + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') + "'",
        double number => number.ToString("R", CultureInfo.InvariantCulture) switch
        {
            "Infinity" => "INF", "-Infinity" => "-INF", var result => result
        },
        float number => number.ToString("R", CultureInfo.InvariantCulture) switch
        {
            "Infinity" => "INF", "-Infinity" => "-INF", var result => result
        },
        byte or sbyte or short or ushort or int or uint or long or ulong or decimal
            => ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
        _ => throw new NotSupportedException($"Values of type {value.GetType()} have no supported OData literal.")
    };

    private static string EnumLiteral(Enum value, ODataQueryOptions options)
    {
        var name = options.EnumName(value.GetType());
        foreach (var segment in name.Split('.')) Names.Validate(segment);
        if (!name.Contains('.')) throw new NotSupportedException("Map enum types without a CLR namespace using MapEnum<TEnum>().");
        var members = StringCompatibility.SplitAndTrim(value.ToString(), ',');
        if (members.Any(m => !Enum.GetNames(value.GetType()).Contains(m, StringComparer.Ordinal)))
            throw new NotSupportedException("Undefined enum values cannot be represented as named OData enum literals.");
        return name + "'" + string.Join(",", members) + "'";
    }
}

// Tracks OData date semantics for DateTime.Date without requiring a CLR DateOnly type.
internal readonly struct ODataDate(DateTime value)
{
    internal string ToLiteral() => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
