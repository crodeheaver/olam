namespace Olam;

internal abstract record Binding(Type Type);
internal sealed record PathBinding(Type ClrType, string Path, bool RootRelative = true) : Binding(ClrType);
internal sealed record ValueBinding(Type ClrType, string Text) : Binding(ClrType);
internal sealed record ObjectBinding(Type ClrType, IReadOnlyDictionary<string, Binding> Members) : Binding(ClrType);
internal sealed record GroupBinding(Type ClrType, Binding Key, Binding Element) : Binding(ClrType);
