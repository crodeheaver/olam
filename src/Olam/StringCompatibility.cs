namespace Olam;

internal static class StringCompatibility
{
    internal static string[] SplitAndTrim(string value, char separator)
    {
#if NETFRAMEWORK
        return value.Split(separator).Select(part => part.Trim()).ToArray();
#else
        return value.Split(separator, StringSplitOptions.TrimEntries);
#endif
    }
}
