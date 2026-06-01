using System.Security.Cryptography;
using System.Text;

namespace IClassMobile.Services;

internal static class WebVpnUrl
{
    private const string GatewayHost = "d.buaa.edu.cn";
    private const string KeyText = "wrdvpnisthebest!";
    private static readonly byte[] KeyBytes = Encoding.UTF8.GetBytes(KeyText);
    private static readonly byte[] IvBytes = Encoding.UTF8.GetBytes(KeyText);

    public const string GatewayOrigin = "https://d.buaa.edu.cn";

    public static string Wrap(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return url;
        }

        if (parsed.Host.Equals(GatewayHost, StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var protocolPart = parsed switch
        {
            { Scheme: "http", Port: 80 } => "http",
            { Scheme: "https", Port: 443 } => "https",
            { IsDefaultPort: true } => parsed.Scheme,
            _ => $"{parsed.Scheme}-{parsed.Port}"
        };

        return $"https://{GatewayHost}/{protocolPart}/{EncryptHost(parsed.Host)}{parsed.PathAndQuery}{parsed.Fragment}";
    }

    public static string Unwrap(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            !parsed.Host.Equals(GatewayHost, StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var segments = parsed.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return url;
        }

        var protocolParts = segments[0].Split('-', 2);
        var scheme = protocolParts[0];
        if (string.IsNullOrWhiteSpace(scheme))
        {
            return url;
        }

        var host = DecryptHost(segments[1]);
        var authority = protocolParts.Length == 2 && int.TryParse(protocolParts[1], out var port)
            ? $"{scheme}://{host}:{port}"
            : $"{scheme}://{host}";
        var path = segments.Length > 2 ? "/" + string.Join("/", segments.Skip(2)) : string.Empty;
        return $"{authority}{path}{parsed.Query}{parsed.Fragment}";
    }

    public static bool IsGatewayUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var parsed) &&
            parsed.Host.Equals(GatewayHost, StringComparison.OrdinalIgnoreCase);
    }

    private static string EncryptHost(string host)
    {
        var plain = Encoding.UTF8.GetBytes(host);
        var padded = new byte[plain.Length + ((16 - plain.Length % 16) % 16)];
        Buffer.BlockCopy(plain, 0, padded, 0, plain.Length);
        for (var i = plain.Length; i < padded.Length; i++)
        {
            padded[i] = (byte)'0';
        }

        var cipher = Transform(padded, encrypt: true, IvBytes);
        return ToHex(IvBytes) + ToHex(cipher)[..(plain.Length * 2)];
    }

    private static string DecryptHost(string encodedHost)
    {
        if (encodedHost.Length < 32)
        {
            return string.Empty;
        }

        var iv = FromHex(encodedHost[..32]);
        var cipherHex = encodedHost[32..];
        cipherHex += new string('0', (32 - cipherHex.Length % 32) % 32);
        var decrypted = Transform(FromHex(cipherHex), encrypt: false, iv);
        var hostLength = encodedHost.Length / 2 - 16;
        return Encoding.UTF8.GetString(decrypted, 0, Math.Min(hostLength, decrypted.Length));
    }

    private static byte[] Transform(byte[] input, bool encrypt, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CFB;
        aes.Padding = PaddingMode.None;
        aes.FeedbackSize = 128;
        aes.Key = KeyBytes;
        aes.IV = iv;

        using var transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
        return transform.TransformFinalBlock(input, 0, input.Length);
    }

    private static string ToHex(byte[] bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static byte[] FromHex(string text)
    {
        return Convert.FromHexString(text);
    }
}
