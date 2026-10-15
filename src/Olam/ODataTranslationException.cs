using System.Linq.Expressions;

namespace Olam;

/// <summary>A LINQ expression cannot be represented by the supported OData query vocabulary.</summary>
public sealed class ODataTranslationException : NotSupportedException
{
    /// <summary>Creates an exception describing an unsupported expression.</summary>
    public ODataTranslationException(string message, Expression expression)
        : base($"{message} Expression: {expression}") => Expression = expression;

    /// <summary>The expression that could not be translated.</summary>
    public Expression Expression { get; }
}
