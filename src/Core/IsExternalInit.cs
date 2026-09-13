namespace System.Runtime.CompilerServices
{
    /// <summary>Polyfill so `init` accessors (C# 9+) compile on net48.
    /// The type is emitted by the compiler on .NET 5+; on .NET Framework the
    /// compiler looks it up, so we declare it once here.</summary>
    internal static class IsExternalInit
    {
    }
}