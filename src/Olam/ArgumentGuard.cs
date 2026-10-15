using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Olam;

internal static class ArgumentGuard
{
    internal static void ThrowIfNull([NotNull] object? argument,
        [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
#if NETFRAMEWORK
        if (argument is null) throw new ArgumentNullException(paramName);
#else
        ArgumentNullException.ThrowIfNull(argument, paramName);
#endif
    }

    internal static void ThrowIfNullOrWhiteSpace([NotNull] string? argument,
        [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
#if NETFRAMEWORK
        ThrowIfNull(argument, paramName);
        if (string.IsNullOrWhiteSpace(argument))
            throw new ArgumentException("The value cannot be empty or whitespace.", paramName);
#else
        ArgumentException.ThrowIfNullOrWhiteSpace(argument, paramName);
#endif
    }
}
