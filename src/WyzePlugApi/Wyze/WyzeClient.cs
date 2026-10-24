using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace WyzePlugApi.Wyze;

/// <summary>
/// Minimal client for the (unofficial) Wyze cloud API, modelled on the community wyze-sdk.
/// Handles login, token caching, device listing and plug power get/set.
/// </summary>
public sealed class WyzeClient
{
    private const string AuthUrl = "https://auth-prod.api.wyze.com/api/user/login";
    private const string ApiBase = "https://api.wyzecam.com";

    // Public constants used by the Wyze Android app / wyze-sdk.
    private const string AuthXApiKey = "RckMFKbsds5p6QY3COEXc2ABwNTYY0q18ziEiSEm";
    private const string AppId = "9319141212m2ik";
    private const string AppVersion = "2.19.14";
    private const string AppName = "com.hualai";
    private const string Sc = "a626948714654991afd3c0dbd7cdb901";

    private const string SvGetObjectList = "c417b62d72ee44bf933054bdca183e77";
    private const string SvGetProperty = "1df2807c63254e16a06213323fe8dec8";
    private const string SvSetProperty = "44b6d5640c4d4978baba65c8ab9a6d6e";

    private const string PowerPid = "P3";

    private static readonly string[] PlugModels = ["WLPP1", "WLPP1CFH", "WLPPO", "WLPPO-SUB"];

    private readonly HttpClient _http;
    private readonly WyzeOptions _options;
    private readonly ILogger<WyzeClient> _log;
    private readonly string _phoneId = Guid.NewGuid().ToString();
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private string? _accessToken;

    public WyzeClient(HttpClient http, IOptions<WyzeOptions> options, ILogger<WyzeClient> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;
    }

    public async Task<IReadOnlyList<PlugInfo>> ListPlugsAsync(CancellationToken ct)
    {
        var devices = await GetPlugDevicesAsync(ct);
        var result = new List<PlugInfo>();
        foreach (var d in devices)
            result.Add(ToInfo(d, null));
        return result;
    }

    public async Task<PlugInfo> GetPlugAsync(string mac, CancellationToken ct)
    {
        var device = await FindPlugAsync(mac, ct);
        var isOn = await ReadPowerAsync(device, ct);
        return ToInfo(device, isOn);
    }

    public async Task<PlugInfo> SetPowerAsync(string mac, bool on, CancellationToken ct)
    {
        var device = await FindPlugAsync(mac, ct);
        await CallApiAsync("/app/v2/device/set_property", SvSetProperty, new JsonObject
        {
            ["device_mac"] = device.Mac,
            ["device_model"] = device.Model,
            ["pid"] = PowerPid,
            ["pvalue"] = on ? "1" : "0",
        }, ct);
        return ToInfo(device, on);
    }

    // ---- helpers ----

    private sealed record Device(string Mac, string Model, string Nickname, bool Online);

    private static PlugInfo ToInfo(Device d, bool? isOn) => new(d.Mac, d.Model, d.Nickname, d.Online, isOn);

    private async Task<List<Device>> GetPlugDevicesAsync(CancellationToken ct)
    {
        var resp = await CallApiAsync("/app/v2/home_page/get_object_list", SvGetObjectList, new JsonObject(), ct);
        var list = resp["data"]?["device_list"]?.AsArray() ?? [];
        var devices = new List<Device>();
        foreach (var node in list)
        {
            var model = node?["product_model"]?.GetValue<string>();
            if (node is null || model is null || !PlugModels.Contains(model)) continue;
            devices.Add(new Device(
                node["mac"]?.GetValue<string>() ?? "",
                model,
                node["nickname"]?.GetValue<string>() ?? "",
                (node["conn_state"]?.ToString() ?? "0") == "1"));
        }
        return devices;
    }

    private async Task<Device> FindPlugAsync(string mac, CancellationToken ct)
    {
        var devices = await GetPlugDevicesAsync(ct);
        return devices.FirstOrDefault(d => string.Equals(d.Mac, mac, StringComparison.OrdinalIgnoreCase))
               ?? throw new PlugNotFoundException(mac);
    }

    private async Task<bool?> ReadPowerAsync(Device d, CancellationToken ct)
    {
        var resp = await CallApiAsync("/app/v2/device/get_property_list", SvGetProperty, new JsonObject
        {
            ["device_mac"] = d.Mac,
            ["device_model"] = d.Model,
            ["target_pid_list"] = new JsonArray(),
        }, ct);
        var props = resp["data"]?["property_list"]?.AsArray();
        var power = props?.FirstOrDefault(p => p?["pid"]?.GetValue<string>() == PowerPid);
        var value = power?["value"]?.ToString();
        return value is null ? null : value == "1";
    }

    /// <summary>Calls an api.wyzecam.com endpoint, logging in first and retrying once on an auth failure.</summary>
    private async Task<JsonNode> CallApiAsync(string path, string sv, JsonObject body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await EnsureTokenAsync(ct);
            var payload = new JsonObject();
            foreach (var kv in body) payload[kv.Key] = kv.Value?.DeepClone();
            payload["access_token"] = token;
            payload["app_name"] = AppName;
            payload["app_ver"] = $"{AppName}___{AppVersion}";
            payload["app_version"] = AppVersion;
            payload["phone_id"] = _phoneId;
            payload["phone_system_type"] = "2";
            payload["sc"] = Sc;
            payload["sv"] = sv;
            payload["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            using var req = new HttpRequestMessage(HttpMethod.Post, ApiBase + path)
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("User-Agent", "okhttp/4.7.2");

            var json = await SendAsync(req, ct);
            var code = json["code"]?.ToString();
            if (code == "1") return json;

            // 2001 = access token error/expired; re-login once.
            if (attempt == 0 && code is "2001" or "2002")
            {
                _log.LogInformation("Wyze access token rejected (code {Code}); logging in again", code);
                _accessToken = null;
                continue;
            }
            throw new WyzeException($"Wyze API {path} failed: code {code}, msg {json["msg"]}",
                int.TryParse(code, out var c) ? c : null);
        }
    }

    private async Task<string> EnsureTokenAsync(CancellationToken ct)
    {
        if (_accessToken is not null) return _accessToken;
        await _loginLock.WaitAsync(ct);
        try
        {
            if (_accessToken is not null) return _accessToken;
            _accessToken = await LoginAsync(ct);
            return _accessToken;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    private async Task<string> LoginAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.Email) || string.IsNullOrEmpty(_options.Password)
            || string.IsNullOrEmpty(_options.KeyId) || string.IsNullOrEmpty(_options.ApiKey))
            throw new WyzeException("Wyze credentials are not configured (Wyze:Email, Password, KeyId, ApiKey).");

        var nonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var body = new JsonObject
        {
            ["nonce"] = nonce.ToString(),
            ["email"] = _options.Email,
            ["password"] = Md5Hex(Md5Hex(Md5Hex(_options.Password))),
        }.ToJsonString();

        using var req = new HttpRequestMessage(HttpMethod.Post, AuthUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("x-api-key", AuthXApiKey);
        req.Headers.TryAddWithoutValidation("keyid", _options.KeyId);
        req.Headers.TryAddWithoutValidation("apikey", _options.ApiKey);
        req.Headers.TryAddWithoutValidation("appid", AppId);
        req.Headers.TryAddWithoutValidation("appinfo", $"wyze_android_{AppVersion}");
        req.Headers.TryAddWithoutValidation("phoneid", _phoneId);
        req.Headers.TryAddWithoutValidation("requestid", Md5Hex(Md5Hex(nonce.ToString())));
        req.Headers.TryAddWithoutValidation("User-Agent", $"wyze_android_{AppVersion}");

        var json = await SendAsync(req, ct);
        var token = json["access_token"]?.GetValue<string>();
        if (string.IsNullOrEmpty(token))
        {
            if (json["mfa_options"] is not null)
                throw new WyzeException("Wyze login requires 2FA, which this service does not support.");
            throw new WyzeException($"Wyze login failed: {json["description"] ?? json["msg"] ?? "no access_token returned"}");
        }
        _log.LogInformation("Logged in to Wyze");
        return token;
    }

    private async Task<JsonNode> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            return JsonNode.Parse(text) ?? throw new WyzeException($"Empty response from Wyze (HTTP {(int)resp.StatusCode})");
        }
        catch (HttpRequestException ex)
        {
            throw new WyzeException($"Could not reach Wyze: {ex.Message}");
        }
        catch (JsonException ex)
        {
            throw new WyzeException($"Unexpected response from Wyze: {ex.Message}");
        }
    }

    private static string Md5Hex(string s) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(s)));
}
