using System.Security.Cryptography;
using System.Text;

namespace EssenceL10n.Core;

/// <summary>内容寻址：源文本哈希是跨版本匹配的主键之一（key 改了也能命中）。</summary>
public static class Hashing
{
    public static string Of(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}
