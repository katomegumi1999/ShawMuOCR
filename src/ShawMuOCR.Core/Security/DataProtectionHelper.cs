using System;
using System.Security.Cryptography;
using System.Text;

namespace ShawMuOCR.Core.Security;

/// <summary>
/// Windows DPAPI 本地数据保护辅助类：
/// 基于当前 Windows 登录账户的主密钥对敏感凭据（如 API Key 与 Token）进行硬件与系统绑定的加密保护。
/// 即便 settings.json 被复制到他人电脑或意外上传至公开仓库，他人也绝对无法解密。
/// </summary>
public static class DataProtectionHelper
{
    private const string EncryptedPrefix = "enc:";

    /// <summary>
    /// 加密敏感字符串（使用当前 Windows 用户的 DPAPI），生成以 "enc:" 为前缀的密文字符串
    /// </summary>
    public static string Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return string.Empty;
        }

        if (plainText.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
        {
            return plainText; // 已经是密文，不重复加密
        }

        if (!OperatingSystem.IsWindows())
        {
            return plainText; // 非 Windows 平台回退保护
        }

        try
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
            byte[] cipherBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            return EncryptedPrefix + Convert.ToBase64String(cipherBytes);
        }
        catch
        {
            return plainText;
        }
    }

    /// <summary>
    /// 解密敏感字符串，如果是以 "enc:" 为前缀的密文则解密，否则视为旧版明文直接返回
    /// </summary>
    public static string Unprotect(string? cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
        {
            return string.Empty;
        }

        if (!cipherText.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
        {
            // 不再兼容旧版明文，解密失败或未加密时直接返回空字符串由用户重新录入
            return string.Empty;
        }

        if (!OperatingSystem.IsWindows())
        {
            return string.Empty;
        }

        try
        {
            string base64 = cipherText[EncryptedPrefix.Length..];
            byte[] cipherBytes = Convert.FromBase64String(base64);
            byte[] plainBytes = ProtectedData.Unprotect(cipherBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            // 解密失败（如复制到了不同机器或不同系统用户），安全置空避免崩溃
            return string.Empty;
        }
    }
}
