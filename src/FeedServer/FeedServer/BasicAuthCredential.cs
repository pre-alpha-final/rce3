using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace FeedServer;

public sealed class BasicAuthCredential
{
    private BasicAuthCredential(byte[] hash)
    {
        Hash = hash;
    }

    public byte[] Hash { get; }

    public static bool TryRead(HttpRequest request, out BasicAuthCredential? credential, out string problem)
    {
        credential = null;

        if (request.Headers.Authorization.Count == 0)
        {
            problem = string.Empty;
            return true;
        }

        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var header))
        {
            problem = "Authorization header is malformed.";
            return false;
        }

        if (!string.Equals(header.Scheme, "Basic", StringComparison.OrdinalIgnoreCase))
        {
            problem = "Only Basic authorization is supported for feeds.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(header.Parameter))
        {
            problem = "Basic authorization must include credentials.";
            return false;
        }

        byte[] credentialBytes;
        try
        {
            credentialBytes = Convert.FromBase64String(header.Parameter);
        }
        catch (FormatException)
        {
            problem = "Basic authorization credentials must be valid base64.";
            return false;
        }

        try
        {
            if (Array.IndexOf(credentialBytes, (byte)':') < 0)
            {
                problem = "Basic authorization credentials must contain a user name and password.";
                return false;
            }

            credential = new BasicAuthCredential(SHA256.HashData(credentialBytes));
            problem = string.Empty;
            return true;
        }
        finally
        {
            Array.Clear(credentialBytes);
        }
    }

    public bool Matches(BasicAuthCredential other)
    {
        return CryptographicOperations.FixedTimeEquals(Hash, other.Hash);
    }

    public bool MatchesHash(byte[] hash)
    {
        return CryptographicOperations.FixedTimeEquals(hash, Hash);
    }
}
