using System.ComponentModel;

namespace System.Runtime.CompilerServices;

/// <summary>
/// Polyfill required to use C# 9+ records / init-only setters on .NET Standard 2.0.
/// The compiler only needs this type to exist; it is never called at runtime.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
internal static class IsExternalInit
{
}
