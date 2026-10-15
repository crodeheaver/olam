using System.Linq.Expressions;

namespace Olam;

internal static class Expressions
{
    internal static Expression Unwrap(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Quote or ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
            expression = unary.Operand;
        return expression;
    }

    internal static LambdaExpression Lambda(Expression expression) => Unwrap(expression) as LambdaExpression
        ?? throw new ODataTranslationException("Expected a captured expression lambda.", expression);

    internal static bool IsLinq(System.Reflection.MethodInfo method)
        => method.DeclaringType == typeof(Queryable) || method.DeclaringType == typeof(Enumerable);
}
