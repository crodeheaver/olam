using System.Collections.ObjectModel;

namespace Olam;

/// <summary>An immutable set of unescaped OData query options, independent of a service URL.</summary>
public sealed class ODataQueryResult
{
    internal ODataQueryResult(IEnumerable<KeyValuePair<string, string>> options)
        => QueryOptions = new ReadOnlyDictionary<string, string>(DictionaryCompatibility.Copy(options));

    /// <summary>OData option names and raw, unescaped option values.</summary>
    public IReadOnlyDictionary<string, string> QueryOptions { get; }

    /// <summary>Renders a URI-escaped query string with a leading ?, or an empty string for an empty query.</summary>
    public string ToQueryString() => QueryOptions.Count == 0 ? string.Empty
        : "?" + string.Join("&", QueryOptions.Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value)));

    /// <summary>Renders the escaped query string.</summary>
    public override string ToString() => ToQueryString();
}
