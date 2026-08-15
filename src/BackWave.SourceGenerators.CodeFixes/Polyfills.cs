// Polyfill so the netstandard2.0 code-fix assembly can use init-only members (record struct).

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit;
}
