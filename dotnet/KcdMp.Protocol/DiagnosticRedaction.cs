// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace KcdMp.Wire;

/// <summary>Shared redaction rules for live diagnostics and support bundles.</summary>
public static partial class DiagnosticRedaction
{
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

    [GeneratedRegex("""(?<!\w)(?<key>["']?(?:host[-_ ]?restart[-_ ]?ticket|steam(?:join)?code|password|passphrase|api[_-]?key|access[_-]?token|relay[_-]?token|authorization|(?:player|display)[_-]?name)["']?\s*[:=]\s*)(?<value>"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*'|(?:Bearer[ ])?[^\s,;}\]]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrivateValues();

    // Keep surrounding quotes and following key=value fields in textual diagnostics and JSON.
    [GeneratedRegex("""(?<![\w/\\:])(?:[A-Z]:[\\/]+|\\{2,}[^\\/\s"'<>|]+\\+|/(?:home|users|mnt|media|run|tmp|var|opt|workspace|volumes|srv|root|etc|private|applications)/)[^"'<>|\r\n]*?(?=$|[\r\n"'<>|]|[ \t]+(?:/|[A-Z]:[\\/]|[A-Z_][\w-]*=))""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AbsolutePaths();

    [GeneratedRegex("""^(?<prefix>[ \t]*(?:\d{2}:\d{2}:\d{2}(?:\.\d+)?[ \t]+)?(?:\[KcdLog\][ \t]+Found Steam name[ \t]*:|\[Steam\][ \t]+PersonaName[ \t]*=|Name[ \t]*:)[ \t]*)[^\r\n]+""", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegacyAgentNames();

    [GeneratedRegex("""(?<!\w)(?<prefix>user_id[ \t]*=[ \t]*)\d+[ \t]*=[ \t]*'[^'\r\n]*'""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GameUser();

    [GeneratedRegex("""(?<![a-z0-9])(?:version|build|v)\s*[:=]?\s*$""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionLabel();

    public static string Text(string text, Func<IPAddress, string?>? addressAlias = null, bool entry = false)
    {
        if (!entry)
        {
            text = AbsolutePaths().Replace(text, "<path>");
            text = LegacyAgentNames().Replace(text, "${prefix}<redacted>");
            text = GameUser().Replace(text, "${prefix}<steam-id>='<redacted>'");
        }
        text = PrivateValues().Replace(text, match =>
        {
            string value = match.Groups["value"].Value;
            string quote = value[0] is '"' or '\'' ? value[..1] : "";
            return match.Groups["key"].Value + quote + (entry ? "private" : "<redacted>") + quote;
        });
        text = SteamIds().Replace(text, entry ? "private-steam" : "<steam-id>");
        text = Ipv6().Replace(text, match =>
        {
            var host = match.ValueSpan;
            var trimmed = host.TrimEnd('.');
            string suffix = host[trimmed.Length..].ToString();
            host = trimmed;
            int port = -1;
            if (host[0] == '[')
            {
                port = host.IndexOf(']') + 1;
                host = host[1..(port - 1)];
            }
            if (host.IndexOf('%') is int zone && zone >= 0) host = host[..zone];
            if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6)
                return match.Value;

            string? alias = addressAlias is null ? "<ip>" : addressAlias(ip);
            return alias is null ? match.Value : alias + (port >= 0 ? trimmed[port..].ToString() : "") + suffix;
        });
        text = Ipv4().Replace(text, match =>
        {
            var host = match.Groups["host"];
            if (!IPAddress.TryParse(host.ValueSpan, out var ip)) return match.Value;
            if (addressAlias is not null &&
                VersionLabel().IsMatch(text.AsSpan(Math.Max(0, match.Index - 32), Math.Min(32, match.Index))) &&
                host.Length == match.Length)
                return match.Value;
            string? alias = addressAlias is null ? "<ip>" : addressAlias(ip);
            return alias is null ? match.Value : alias + match.Value[host.Length..];
        });
        text = WindowsProfiles().Replace(text, "${prefix}private-user");
        return UnixProfiles().Replace(text, "${prefix}private-user");
    }
}
