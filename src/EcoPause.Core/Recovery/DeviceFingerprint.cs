using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace EcoPause.Core.Recovery;

public sealed record DeviceFingerprint
{
    private const string Prefix = "sha256:";
    private const int Sha256HexLength = 64;
    private static readonly SearchValues<char> UpperHexCharacters =
        SearchValues.Create("0123456789ABCDEF");

    [JsonConstructor]
    public DeviceFingerprint(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (!value.StartsWith(Prefix, StringComparison.Ordinal) ||
            value.Length != Prefix.Length + Sha256HexLength ||
            value.AsSpan(Prefix.Length).ContainsAnyExcept(UpperHexCharacters))
        {
            throw new ArgumentException("The device fingerprint must be a SHA-256 identifier.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public static DeviceFingerprint FromStableIdentifier(string stableIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableIdentifier);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(stableIdentifier));
        return new DeviceFingerprint(Prefix + Convert.ToHexString(digest));
    }

    public override string ToString() => Value;
}
