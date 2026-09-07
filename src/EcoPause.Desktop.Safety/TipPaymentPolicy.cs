using System.Text.RegularExpressions;

namespace EcoPause.Desktop.Safety;

public sealed record CryptoTipOption(string Label, string Address)
{
    public string DisplayAddress => Address.Length <= 22
        ? Address
        : $"{Address[..12]}…{Address[^8..]}";
}

public sealed record TipPaymentConfiguration(
    Uri PayPalUri,
    IReadOnlyList<CryptoTipOption> CryptoOptions);

public static partial class TipPaymentPolicy
{
    public const int MaximumConfigurationCharacters = 4096;
    private const int MaximumOptions = 12;

    public static TipPaymentConfiguration Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length is <= 0 or > MaximumConfigurationCharacters)
        {
            throw new InvalidOperationException("The tip configuration has an invalid size.");
        }

        Uri? payPalUri = null;
        var crypto = new List<CryptoTipOption>();
        var seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length > MaximumOptions)
        {
            throw new InvalidOperationException("The tip configuration contains too many options.");
        }

        foreach (var line in lines)
        {
            var separator = line.IndexOf(':');
            if (separator <= 0 || separator == line.Length - 1)
            {
                throw new InvalidOperationException("A tip configuration line is malformed.");
            }

            var suppliedLabel = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            var key = NormalizeLabel(suppliedLabel);
            if (!seenLabels.Add(key))
            {
                throw new InvalidOperationException($"The tip option '{suppliedLabel}' is duplicated.");
            }

            switch (key)
            {
                case "paypal":
                    payPalUri = ParsePayPal(value);
                    break;
                case "usdc-erc20":
                    crypto.Add(new CryptoTipOption("USDC (ERC-20)", ValidateEvmAddress(value, suppliedLabel)));
                    break;
                case "ethereum":
                    crypto.Add(new CryptoTipOption("Ethereum (ETH)", ValidateEvmAddress(value, suppliedLabel)));
                    break;
                case "solana":
                    crypto.Add(new CryptoTipOption("Solana (SOL)", ValidateSolanaAddress(value)));
                    break;
                case "bitcoin":
                    crypto.Add(new CryptoTipOption("Bitcoin (BTC)", ValidateBitcoinAddress(value)));
                    break;
                default:
                    throw new InvalidOperationException($"The tip option '{suppliedLabel}' is not supported.");
            }
        }

        return new TipPaymentConfiguration(
            payPalUri ?? throw new InvalidOperationException("A PayPal tip destination is required."),
            crypto);
    }

    private static string NormalizeLabel(string label) =>
        label.Trim().ToUpperInvariant() switch
        {
            "PAYPAL" => "paypal",
            "USDC ERC20" or "USDC (ERC20)" or "USDC (ERC-20)" or "USCD ERC20" => "usdc-erc20",
            "ETH" or "ETHEREUM" => "ethereum",
            "SOL" or "SOLANA" => "solana",
            "BTC" or "BITCOIN" => "bitcoin",
            _ => label
        };

    private static Uri ParsePayPal(string value)
    {
        var candidate = value.StartsWith("paypal.me/", StringComparison.OrdinalIgnoreCase)
            ? "https://" + value
            : SimplePayPalHandle().IsMatch(value)
                ? "https://paypal.me/" + value
                : value;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length != 0 ||
            uri.Port != 443 ||
            !AllowedPayPalHosts.Contains(uri.IdnHost) ||
            string.IsNullOrWhiteSpace(uri.AbsolutePath.Trim('/')))
        {
            throw new InvalidOperationException("The PayPal tip destination is not a supported HTTPS PayPal link.");
        }
        return uri;
    }

    private static string ValidateEvmAddress(string value, string label)
    {
        if (!EvmAddress().IsMatch(value))
        {
            throw new InvalidOperationException($"The {label} destination is not a valid EVM address.");
        }
        return value;
    }

    private static string ValidateSolanaAddress(string value)
    {
        if (!SolanaAddress().IsMatch(value))
        {
            throw new InvalidOperationException("The Solana destination is not a valid base58 address.");
        }
        return value;
    }

    private static string ValidateBitcoinAddress(string value)
    {
        if (!BitcoinLegacyAddress().IsMatch(value) && !BitcoinBech32Address().IsMatch(value))
        {
            throw new InvalidOperationException("The Bitcoin destination is not a recognized mainnet address.");
        }
        return value;
    }

    private static readonly HashSet<string> AllowedPayPalHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "paypal.me",
        "paypal.com",
        "www.paypal.com"
    };

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SimplePayPalHandle();

    [GeneratedRegex("^0x[0-9A-Fa-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex EvmAddress();

    [GeneratedRegex("^[1-9A-HJ-NP-Za-km-z]{32,44}$", RegexOptions.CultureInvariant)]
    private static partial Regex SolanaAddress();

    [GeneratedRegex("^[13][1-9A-HJ-NP-Za-km-z]{25,34}$", RegexOptions.CultureInvariant)]
    private static partial Regex BitcoinLegacyAddress();

    [GeneratedRegex("^(bc1)[ac-hj-np-z02-9]{11,71}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BitcoinBech32Address();
}
