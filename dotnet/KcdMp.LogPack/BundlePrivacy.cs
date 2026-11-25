// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace KcdMp.LogPack;

internal static partial class BundlePrivacy
{
    // Crash dumps can embed process memory, including credentials. The text
    // crash report is shareable after redaction. Saves stay on the player's PC.
    public static bool IsPrivateFile(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".dmp" or ".mdmp" or ".whs";

    [GeneratedRegex("""(?<![\w])(?:\[[0-9a-f:.]+(?:%[\w.-]+)?\](?::\d+)?|[0-9a-f]{0,4}:[0-9a-f:.]*:[0-9a-f:.]*(?:%[\w.-]+)?)(?![\w:])""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Ipv6();

    [GeneratedRegex("""(?<![\d.])(?<host>(?:\d{1,3}\.){3}\d{1,3})(?::\d+)?(?!\d|\.\d)""", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4();

    [GeneratedRegex("""(?<!\w)(?:steamid:\s*\d+|STEAM_[0-5]:[01]:\d+|\[[A-Z]:[0-5]:\d+(?::\d+)?\]|76561\d{12})(?!\w)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamIds();

    [GeneratedRegex("""(?<prefix>(?:(?:[A-Z]:)?\\+(?:Users|Documents and Settings)\\+|[A-Z]:/+(?:Users|Documents and Settings)/+))[^\\/:"'\r\n]+(?=[\\/]|$|["'])""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindowsProfiles();

    [GeneratedRegex("""(?<prefix>/(?:Users|home)/)[^/\s"']+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnixProfiles();

    [GeneratedRegex("""(?<!\w)(?<key>["']?(?:host[-_ ]?restart[-_ ]?ticket|steam(?:join)?code|password|passphrase|api[_-]?key|access[_-]?token|relay[_-]?token|authorization)["']?\s*[:=]\s*)(?<value>"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|(?:Bearer[ ])?[^\s,;}\]]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretValues();

    public static string Text(string text) => Redact(text, false);
    public static string Entry(string text) => Redact(text, true);

    private static string Redact(string text, bool entry)
    {
        text = SecretValues().Replace(text, match =>
        {
            string value = match.Groups["value"].Value;
            string quote = value[0] is '"' or '\'' ? value[..1] : "";
            return match.Groups["key"].Value + quote + (entry ? "private" : "<redacted>") + quote;
        });
        text = SteamIds().Replace(text, entry ? "private-steam" : "<steam-id>");
        text = Ipv6().Replace(text, match =>
        {
            var host = match.ValueSpan;
            var trailing = host.TrimEnd('.');
            string suffix = host[trailing.Length..].ToString();
            host = trailing;
            if (host[0] == '[') host = host[1..host.IndexOf(']')];
            if (host.IndexOf('%') is int zone && zone >= 0) host = host[..zone];
            return IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6
                ? (entry ? "private-ip" : "<ip>") + suffix : match.Value;
        });
        text = Ipv4().Replace(text, match =>
            IPAddress.TryParse(match.Groups["host"].ValueSpan, out _)
                ? entry ? "private-ip" : "<ip>" : match.Value);
        text = WindowsProfiles().Replace(text, "${prefix}private-user");
        return UnixProfiles().Replace(text, "${prefix}private-user");
    }

    public static byte[]? Bytes(byte[] raw)
    {
        ReadOnlySpan<byte> body = raw;
        Encoding encoding = Encoding.UTF8;
        if (body.StartsWith(Encoding.Unicode.Preamble)) encoding = Encoding.Unicode;
        else if (body.StartsWith(Encoding.BigEndianUnicode.Preamble)) encoding = Encoding.BigEndianUnicode;

        ReadOnlySpan<byte> preamble = encoding.Preamble;
        if (body.StartsWith(preamble)) body = body[preamble.Length..];
        else preamble = [];
        if (encoding == Encoding.UTF8 && body.Contains((byte)0)) return null;

        string text = encoding.GetString(body);
        // Some engine logs use an ANSI single-byte encoding. All the structured
        // identifiers being removed are ASCII, so keep other bytes intact.
        if (encoding == Encoding.UTF8 && text.Contains('\uFFFD'))
        {
            encoding = Encoding.Latin1;
            text = encoding.GetString(body);
        }
        string sanitized = Text(text);
        if (sanitized == text) return raw;
        var result = new byte[preamble.Length + encoding.GetByteCount(sanitized)];
        preamble.CopyTo(result);
        encoding.GetBytes(sanitized, result.AsSpan(preamble.Length));
        return result;
    }
}
