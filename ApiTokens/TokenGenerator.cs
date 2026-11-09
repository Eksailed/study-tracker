using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace StudyTracker.ApiTokens;

public static class TokenGenerator
{
    public const string Marker = "zt_";

    /// <summary><c>zt_</c> и 43 символа base64url из 32 случайных байт; в базу уходит только <c>Hash</c>.</summary>
    public static (string Token, string Hash, string Prefix) Create()
    {
        var token = Marker + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        return (token, Hash(token), token[..8]);
    }

    public static string Hash(string token) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
