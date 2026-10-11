using System.Security.Cryptography;

namespace FileTransferPoc.Client.Download;

/// <summary>
/// An expected checksum taken from the JustisHub message that carried the URI.
/// Written as <c>algorithm:value</c> (e.g. <c>sha256:9f86d0...</c>); a bare value is read as SHA-256.
/// The value may be hex or base64.
/// </summary>
public sealed record Checksum(HashAlgorithmName Algorithm, byte[] Expected)
{
    public static Checksum Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var separator = text.IndexOf(':');
        var algorithmName = separator > 0 ? text[..separator] : "sha256";
        var value = separator > 0 ? text[(separator + 1)..] : text;

        var algorithm = algorithmName.Replace("-", "").ToLowerInvariant() switch
        {
            "sha256" => HashAlgorithmName.SHA256,
            "sha384" => HashAlgorithmName.SHA384,
            "sha512" => HashAlgorithmName.SHA512,
            "sha1" => HashAlgorithmName.SHA1,
            "md5" => HashAlgorithmName.MD5,
            _ => throw new FormatException($"Unsupported checksum algorithm '{algorithmName}'."),
        };

        return new Checksum(algorithm, DecodeValue(value.Trim(), ExpectedLength(algorithm)));
    }

    /// <summary>Hashes the file and compares it with the expected value in constant time.</summary>
    public async Task<bool> MatchesAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var actual = Algorithm.Name switch
        {
            "SHA256" => await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false),
            "SHA384" => await SHA384.HashDataAsync(stream, cancellationToken).ConfigureAwait(false),
            "SHA512" => await SHA512.HashDataAsync(stream, cancellationToken).ConfigureAwait(false),
            "SHA1" => await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false),
            "MD5" => await MD5.HashDataAsync(stream, cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException(Algorithm.Name),
        };
        return CryptographicOperations.FixedTimeEquals(actual, Expected);
    }

    public override string ToString() => $"{Algorithm.Name}:{Convert.ToHexStringLower(Expected)}";

    private static int ExpectedLength(HashAlgorithmName algorithm) => algorithm.Name switch
    {
        "SHA256" => 32,
        "SHA384" => 48,
        "SHA512" => 64,
        "SHA1" => 20,
        "MD5" => 16,
        _ => throw new NotSupportedException(algorithm.Name),
    };

    private static byte[] DecodeValue(string value, int length)
    {
        if (value.Length == length * 2 && value.All(Uri.IsHexDigit))
        {
            return Convert.FromHexString(value);
        }

        try
        {
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length == length)
            {
                return bytes;
            }
        }
        catch (FormatException)
        {
        }

        throw new FormatException($"Checksum value is not a {length}-byte hex or base64 string.");
    }
}
