namespace WyzePlugApi.Wyze;

public sealed record PlugInfo(string Mac, string Model, string Nickname, bool IsOnline, bool? IsOn);

public sealed class WyzeException(string message, int? code = null) : Exception(message)
{
    public int? Code { get; } = code;
}

public sealed class PlugNotFoundException(string mac) : Exception($"No plug with MAC {mac}");
