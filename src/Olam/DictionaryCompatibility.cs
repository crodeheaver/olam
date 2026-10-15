namespace Olam;

internal static class DictionaryCompatibility
{
    internal static Dictionary<TKey, TValue> Copy<TKey, TValue>(IEnumerable<KeyValuePair<TKey, TValue>> source)
        where TKey : notnull
    {
#if NETFRAMEWORK
        return source.ToDictionary(p => p.Key, p => p.Value);
#else
        return new Dictionary<TKey, TValue>(source);
#endif
    }

#if NETFRAMEWORK
    // On modern .NET, calls resolve to the built-in CollectionExtensions method.
    internal static TValue? GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key)
    {
        ArgumentGuard.ThrowIfNull(dictionary);
        return dictionary.TryGetValue(key, out var value) ? value : default;
    }
#endif
}
