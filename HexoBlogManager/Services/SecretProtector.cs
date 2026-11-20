using System.Security.Cryptography;
using System.Text;

namespace HexoBlogManager.Services;

/// <summary>
/// Windows DPAPI 加解密工具，专门用来保护 GitHub Token。
///
/// 为什么不用明文存？
/// settings.json 放在 %AppData% 下，任何能读你用户目录的程序都能看到。
/// Token 泄露等于把仓库写权限交出去，所以必须加密。
///
/// 为什么用 DPAPI 而不是自己写 AES + 固定密钥？
/// DPAPI 的密钥由 Windows 根据当前用户登录凭据派生并托管，程序里不存任何密钥。
/// 换用户、换机器都解不开 —— 安全性远高于把密钥硬编码在代码里。
/// </summary>
public static class SecretProtector
{
    /// <summary>附加熵，进一步限定「只有本程序能解」。换掉这个值会导致旧密文全部失效。</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HexoBlogManager.GitHubToken.v1");

    /// <summary>把明文加密成 Base64 字符串；失败返回空串（不抛异常，避免影响保存流程）。</summary>
    public static string Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        try
        {
            byte[] cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(cipher);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>解密 Base64 密文；失败返回空串（例如换了登录用户、文件被改坏）。</summary>
    public static string Unprotect(string? cipherBase64)
    {
        if (string.IsNullOrEmpty(cipherBase64))
            return string.Empty;

        try
        {
            byte[] cipher = Convert.FromBase64String(cipherBase64);
            byte[] plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return string.Empty;
        }
    }
}
