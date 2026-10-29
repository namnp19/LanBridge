using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace LanBridge.Core;

public sealed class DeviceIdentity : IDisposable
{
    public X509Certificate2 Certificate { get; }
    public string DeviceId { get; }
    private DeviceIdentity(X509Certificate2 certificate)
    {
        Certificate = certificate;
        if (!Certificate.HasPrivateKey) throw new CryptographicException("Device certificate has no private key");
        DeviceId = IdentityOf(certificate);
    }

    public static DeviceIdentity LoadOrCreate(string folder)
    {
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "identity.dpapi");
        if (File.Exists(file))
        {
            var decrypted = Dpapi.Unprotect(File.ReadAllBytes(file));
            return new DeviceIdentity(X509CertificateLoader.LoadPkcs12(decrypted, null,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet));
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=LanBridge Device", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        {
            new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2")
        }, false));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        var pfx = generated.Export(X509ContentType.Pfx);
        var encrypted = Dpapi.Protect(pfx);
        var tmp = file + ".tmp";
        File.WriteAllBytes(tmp, encrypted);
        File.Move(tmp, file, false);
        return new DeviceIdentity(X509CertificateLoader.LoadPkcs12(pfx, null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet));
    }

    /// <summary>Creates a transient identity for tests; never persists private key.</summary>
    public static DeviceIdentity CreateEphemeral()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=LanBridge Test", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        { new("1.3.6.1.5.5.7.3.1"), new("1.3.6.1.5.5.7.3.2") }, false));
        return new DeviceIdentity(request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1)));
    }

    public string PublicCertificate => Convert.ToBase64String(Certificate.Export(X509ContentType.Cert));

    public static string IdentityOf(X509Certificate2 cert)
    {
        using var pub = cert.GetECDsaPublicKey() ?? throw new CryptographicException("ECDSA certificate required");
        return Convert.ToHexString(SHA256.HashData(pub.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
    }

    public static X509Certificate2 ImportPublic(string base64)
    {
        var raw = Convert.FromBase64String(base64);
        if (raw.Length > 8192) throw new InvalidDataException("Oversized identity certificate");
        var cert = X509CertificateLoader.LoadCertificate(raw);
        if (!IsPlausible(cert)) { cert.Dispose(); throw new CryptographicException("Invalid identity certificate"); }
        return cert;
    }

    public static bool IsPlausible(X509Certificate2 cert)
    {
        try
        {
            if (cert.NotBefore.ToUniversalTime() > DateTime.UtcNow.AddMinutes(5) || cert.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
                return false;
            if (cert.GetECDsaPublicKey() is not { } publicKey) return false;
            using (publicKey)
            {
                // Trust is checked separately against signed group memberships / approved join sessions.
                return publicKey.KeySize >= 256;
            }
        }
        catch { return false; }
    }

    public string Sign(string message)
    {
        using var privateKey = Certificate.GetECDsaPrivateKey()!;
        return Convert.ToBase64String(privateKey.SignData(Encoding.UTF8.GetBytes(message), HashAlgorithmName.SHA256));
    }

    public static bool Verify(string certificate, string message, string signature)
    {
        try
        {
            using var cert = ImportPublic(certificate);
            using var key = cert.GetECDsaPublicKey()!;
            return key.VerifyData(Encoding.UTF8.GetBytes(message), Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
        }
        catch { return false; }
    }

    public static string PairCode(string groupId, string adminId, string joinerId, string joinId)
    {
        var input = Encoding.UTF8.GetBytes($"LanBridge/v1/{groupId}/{adminId}/{joinerId}/{joinId}");
        var digest = SHA256.HashData(input);
        var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(digest.AsSpan(0, 4)) % 1000000;
        return value.ToString("D6");
    }

    public void Dispose() => Certificate.Dispose();
}

// Protect secrets with Windows Data Protection API, scoped to the current Windows user.
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr LocalFree(IntPtr ptr);

    private static byte[] Apply(byte[] bytes, bool protect)
    {
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            Blob output;
            if (protect)
            {
                if (!CryptProtectData(ref input, "LanBridge identity", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x01, out output))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            else if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x01, out output))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally
        {
            // Clear plaintext key bytes from unmanaged temporary buffer.
            Marshal.Copy(new byte[input.Length], 0, input.Data, input.Length);
            Marshal.FreeHGlobal(input.Data);
        }
    }
    public static byte[] Protect(byte[] plaintext) => Apply(plaintext, true);
    public static byte[] Unprotect(byte[] encrypted) => Apply(encrypted, false);
}
