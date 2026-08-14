using System.Security.Cryptography;
using System.Text;

namespace FeedServer;

public sealed class FeedAuthorizationKey
{
    private FeedAuthorizationKey(byte[] hash)
    {
        Hash = hash;
    }

    public byte[] Hash { get; }

    public static bool TryRead(HttpRequest request, out FeedAuthorizationKey? key, out string problem)
    {
        key = null;

        if (request.Headers.Authorization.Count == 0)
        {
            problem = string.Empty;
            return true;
        }

        if (request.Headers.Authorization.Count != 1)
        {
            problem = "Authorization must include exactly one key.";
            return false;
        }

        var rawKey = request.Headers.Authorization[0];
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            problem = "Authorization key must not be empty.";
            return false;
        }

        var keyBytes = Encoding.UTF8.GetBytes(rawKey);
        key = new FeedAuthorizationKey(SHA256.HashData(keyBytes));
        Array.Clear(keyBytes);
        problem = string.Empty;
        return true;
    }

    public bool Matches(FeedAuthorizationKey other)
    {
        return CryptographicOperations.FixedTimeEquals(Hash, other.Hash);
    }

    public bool MatchesHash(byte[] hash)
    {
        return CryptographicOperations.FixedTimeEquals(hash, Hash);
    }
}
