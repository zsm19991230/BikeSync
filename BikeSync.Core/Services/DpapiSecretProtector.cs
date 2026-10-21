using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using BikeSync.Core.Abstractions;

namespace BikeSync.Core.Services;

/// <summary>
/// Encrypts secrets with Windows DPAPI in the current user's context.
/// Ciphertext is bound to the Windows account and cannot be moved to another user.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BikeSync.v1.secret-store");

    public string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(cipher);
    }

    public string Unprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
        {
            return string.Empty;
        }

        try
        {
            var cipher = Convert.FromBase64String(protectedValue);
            var bytes = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new InvalidOperationException(
                "无法解密本地凭据。它可能由另一个 Windows 用户创建，或数据已损坏。请断开平台后重新连接。",
                ex);
        }
    }
}
