#if UNITY_EDITOR_WIN
using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

internal sealed class MCBWindowsSha256 : HashAlgorithm
{
    IntPtr algorithm, hash;
    public MCBWindowsSha256()
    {
        HashSizeValue = 256;
        Check(BCryptOpenAlgorithmProvider(out algorithm, "SHA256", null, 0));
        try { Initialize(); }
        catch { BCryptCloseAlgorithmProvider(algorithm, 0); algorithm = IntPtr.Zero; throw; }
    }
    public override void Initialize()
    {
        if (hash != IntPtr.Zero) { BCryptDestroyHash(hash); hash = IntPtr.Zero; }
        Check(BCryptCreateHash(algorithm, out hash, IntPtr.Zero, 0, IntPtr.Zero, 0, 0));
    }
    protected override void HashCore(byte[] array, int offset, int count)
    {
        if (count == 0) return;
        var pinned = GCHandle.Alloc(array, GCHandleType.Pinned);
        try { Check(BCryptHashData(hash, IntPtr.Add(pinned.AddrOfPinnedObject(), offset), count, 0)); }
        finally { pinned.Free(); }
    }
    protected override byte[] HashFinal()
    {
        var bytes = new byte[32];
        Check(BCryptFinishHash(hash, bytes, bytes.Length, 0));
        return bytes;
    }
    protected override void Dispose(bool disposing)
    {
        if (hash != IntPtr.Zero) { BCryptDestroyHash(hash); hash = IntPtr.Zero; }
        if (algorithm != IntPtr.Zero) { BCryptCloseAlgorithmProvider(algorithm, 0); algorithm = IntPtr.Zero; }
        base.Dispose(disposing);
    }
    static void Check(int status) { if (status != 0) throw new CryptographicException("Windows SHA-256 status 0x" + status.ToString("X8")); }
    [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)] static extern int BCryptOpenAlgorithmProvider(out IntPtr algorithm, string id, string implementation, int flags);
    [DllImport("bcrypt.dll")] static extern int BCryptCloseAlgorithmProvider(IntPtr algorithm, int flags);
    [DllImport("bcrypt.dll")] static extern int BCryptCreateHash(IntPtr algorithm, out IntPtr hash, IntPtr objectBuffer, int objectBytes, IntPtr secret, int secretBytes, int flags);
    [DllImport("bcrypt.dll")] static extern int BCryptHashData(IntPtr hash, IntPtr input, int bytes, int flags);
    [DllImport("bcrypt.dll")] static extern int BCryptFinishHash(IntPtr hash, [Out] byte[] output, int bytes, int flags);
    [DllImport("bcrypt.dll")] static extern int BCryptDestroyHash(IntPtr hash);
}
#endif
