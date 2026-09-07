#if UNITY_EDITOR
using System.Security.Cryptography;

/// <summary>Shared SHA-256 provider; hash identity is independent of the platform.</summary>
public static class MCBHashing
{
    public static HashAlgorithm CreateSha256()
    {
#if UNITY_EDITOR_WIN
        return new MCBWindowsSha256();
#else
        return SHA256.Create();
#endif
    }
}
#endif
