namespace WyzePlugApi.Wyze;

public sealed class WyzeOptions
{
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public string KeyId { get; set; } = "";
    public string ApiKey { get; set; } = "";

    /// <summary>Optional proxy for Wyze traffic, e.g. socks5://127.0.0.1:40000 (Cloudflare WARP proxy mode).</summary>
    public string? Proxy { get; set; }

    public int TimeoutSeconds { get; set; } = 20;
}
