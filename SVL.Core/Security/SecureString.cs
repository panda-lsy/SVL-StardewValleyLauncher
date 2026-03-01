using System;
using System.Security.Cryptography;
using System.Text;
using SVL.Core.Logging;

namespace SVL.Core.Security;

/// <summary>
/// 加密字符串服务（用于保护 API Key 等敏感信息）
/// </summary>
public static class SecureString
{
    // 从固定种子派生密钥和 IV，确保长度正确
    private static readonly byte[] EncryptionKey;
    private static readonly byte[] EncryptionIV;

    static SecureString()
    {
        // 使用 SHA256 从种子生成 32 字节的 Key
        using var sha256 = SHA256.Create();
        EncryptionKey = sha256.ComputeHash(Encoding.UTF8.GetBytes("SVL_SecureKey_2024"));

        // 使用 MD5 从种子生成 16 字节的 IV
        using var md5 = MD5.Create();
        EncryptionIV = md5.ComputeHash(Encoding.UTF8.GetBytes("SVL_InitVec_2024"));
    }

    /// <summary>
    /// 加密字符串
    /// </summary>
    /// <param name="plainText">明文</param>
    /// <returns>Base64 编码的密文</returns>
    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return plainText ?? string.Empty;
        }

        try
        {
            using var aes = Aes.Create();
            aes.Key = EncryptionKey;
            aes.IV = EncryptionIV;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var encryptor = aes.CreateEncryptor();
            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            var encryptedBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

            return Convert.ToBase64String(encryptedBytes);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SecureString] 加密失败");
            throw;
        }
    }

    /// <summary>
    /// 解密字符串
    /// </summary>
    /// <param name="cipherText">Base64 编码的密文</param>
    /// <returns>明文</returns>
    public static string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
        {
            return cipherText ?? string.Empty;
        }

        try
        {
            using var aes = Aes.Create();
            aes.Key = EncryptionKey;
            aes.IV = EncryptionIV;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            var encryptedBytes = Convert.FromBase64String(cipherText);
            var decryptedBytes = decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);

            return Encoding.UTF8.GetString(decryptedBytes);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SecureString] 解密失败");
            return string.Empty; // 解密失败返回空字符串
        }
    }

    /// <summary>
    /// 掩码字符串用于显示（只显示前后各2个字符，中间用 **** 代替）
    /// </summary>
    /// <param name="value">原始字符串</param>
    /// <returns>掩码后的字符串</returns>
    public static string Mask(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        if (value.Length <= 4)
        {
            return "****";
        }

        var start = value.Substring(0, 2);
        var end = value.Substring(value.Length - 2, 2);
        var maskLength = Math.Max(4, value.Length - 4);

        return $"{start}{new string('*', maskLength)}{end}";
    }

    /// <summary>
    /// 检查字符串是否已被掩码
    /// </summary>
    public static bool IsMasked(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        return value.Contains("*");
    }
}
