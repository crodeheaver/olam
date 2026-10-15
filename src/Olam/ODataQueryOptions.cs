using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Olam;

/// <summary>Configures CLR-to-EDM names. A query takes its own copy of this configuration.</summary>
public sealed class ODataQueryOptions
{
    private readonly Dictionary<MemberInfo, string> _properties = [];
    private readonly Dictionary<Type, string> _enums = [];

    /// <summary>Optional policy for names without an explicit override or enabled JSON attribute.</summary>
    public JsonNamingPolicy? PropertyNamingPolicy { get; set; }

    /// <summary>Whether to honor System.Text.Json's JsonPropertyName attributes. Defaults to false.</summary>
    public bool UseJsonPropertyNames { get; set; }

    /// <summary>Overrides a direct POCO property's EDM name.</summary>
    public ODataQueryOptions MapProperty<T, TProperty>(Expression<Func<T, TProperty>> property, string name)
    {
        ArgumentGuard.ThrowIfNull(property);
        Names.Validate(name);
        if (Expressions.Unwrap(property.Body) is not MemberExpression { Member: PropertyInfo member } access
            || access.Expression != property.Parameters[0])
            throw new ArgumentException("Select one direct property, for example p => p.Name.", nameof(property));
        _properties[member] = name;
        return this;
    }

    /// <summary>Overrides the namespace-qualified EDM type name used in enum literals.</summary>
    public ODataQueryOptions MapEnum<TEnum>(string edmTypeName) where TEnum : struct, Enum
    {
        ArgumentGuard.ThrowIfNullOrWhiteSpace(edmTypeName);
        foreach (var part in edmTypeName.Split('.')) Names.Validate(part);
        if (!edmTypeName.Contains('.'))
            throw new ArgumentException("An EDM enum type name must be namespace-qualified.", nameof(edmTypeName));
        _enums[typeof(TEnum)] = edmTypeName;
        return this;
    }

    internal ODataQueryOptions Snapshot()
    {
        var copy = new ODataQueryOptions { PropertyNamingPolicy = PropertyNamingPolicy, UseJsonPropertyNames = UseJsonPropertyNames };
        foreach (var item in _properties) copy._properties.Add(item.Key, item.Value);
        foreach (var item in _enums) copy._enums.Add(item.Key, item.Value);
        return copy;
    }

    internal string Name(MemberInfo member)
    {
        var name = _properties.TryGetValue(member, out var mapped) ? mapped
            : UseJsonPropertyNames && member.GetCustomAttribute<JsonPropertyNameAttribute>() is { } attribute ? attribute.Name
            : PropertyNamingPolicy?.ConvertName(member.Name) ?? member.Name;
        Names.Validate(name);
        return name;
    }

    internal string EnumName(Type type) => _enums.TryGetValue(type, out var mapped) ? mapped
        : (type.FullName ?? type.Name).Replace('+', '.');
}

internal static class Names
{
    internal static void Validate(string name)
    {
        ArgumentGuard.ThrowIfNullOrWhiteSpace(name);
        if (!(char.IsLetter(name[0]) || name[0] == '_') || name.Any(c => !(char.IsLetterOrDigit(c) || c == '_')))
            throw new ArgumentException($"'{name}' is not a valid OData identifier.", nameof(name));
    }
}
