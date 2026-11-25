// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Net;
using System.Text;
using KcdMp.Wire;

namespace KcdMp.LogPack;

internal sealed class BundlePrivacy
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly Dictionary<IPAddress, string> _addresses = new();

    // Dumps can contain process memory. Saves stay on the player's PC.
    public static bool IsPrivateFile(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".dmp" or ".mdmp" or ".whs";

    public string Text(string text) => DiagnosticRedaction.Text(text, AddressAlias);
    public string Entry(string text) => DiagnosticRedaction.Text(text, AddressAlias, entry: true);

    private string? AddressAlias(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return null;
        if (!_addresses.TryGetValue(ip, out string? alias))
        {
            alias = FormattableString.Invariant($"ip-{_addresses.Count + 1}");
            _addresses.Add(ip, alias);
        }
        return alias;
    }

    public byte[]? Bytes(byte[] raw)
    {
        var decoded = Decode(raw);
        if (decoded is not { } result) return null;
        string sanitized = Text(result.Text);
        if (!result.RemovedNuls && sanitized == result.Text) return raw;
        var output = new byte[result.PreambleLength + result.Encoding.GetByteCount(sanitized)];
        raw.AsSpan(0, result.PreambleLength).CopyTo(output);
        result.Encoding.GetBytes(sanitized, output.AsSpan(result.PreambleLength));
        return output;
    }

    public static string? ReadText(byte[] raw) => Decode(raw)?.Text;

    private static (string Text, Encoding Encoding, int PreambleLength, bool RemovedNuls)? Decode(byte[] raw)
    {
        ReadOnlySpan<byte> body = raw;
        if (HasBinarySignature(body)) return null;

        Encoding encoding = Encoding.UTF8;
        if (body.StartsWith(Encoding.Unicode.Preamble)) encoding = Encoding.Unicode;
        else if (body.StartsWith(Encoding.BigEndianUnicode.Preamble)) encoding = Encoding.BigEndianUnicode;
        int preambleLength = body.StartsWith(encoding.Preamble) ? encoding.Preamble.Length : 0;
        body = body[preambleLength..];

        string text;
        try { text = (encoding == Encoding.UTF8 ? StrictUtf8 : encoding).GetString(body); }
        catch (DecoderFallbackException)
        {
            // Some engine logs use a single-byte encoding or end in an
            // incomplete UTF-8 character. Preserve bytes outside redactions.
            encoding = Encoding.Latin1;
            text = encoding.GetString(body);
        }

        bool removedNuls = text.Contains('\0');
        if (removedNuls) text = text.Replace("\0", "", StringComparison.Ordinal);
        if (body.Length > 0 && text.Length == 0) return null;
        int controls = 0;
        foreach (char chr in text)
            if (char.IsControl(chr) && chr is not ('\t' or '\r' or '\n' or '\f')) controls++;
        // Real crash logs may have large zero-filled regions. Decide from the
        // remaining contents, allowing incidental engine console controls.
        if (controls > 0 && (controls >= 4 || controls == text.Length) && controls > text.Length / 20) return null;
        return (text, encoding, preambleLength, removedNuls);
    }

    private static bool HasBinarySignature(ReadOnlySpan<byte> data) =>
        data.Length >= 32 && data.StartsWith<byte>([0x4D, 0x44, 0x4D, 0x50, 0x93, 0xA7]) ||
        data.StartsWith("\u007fELF"u8) ||
        data.StartsWith<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) ||
        data.StartsWith<byte>([0x50, 0x4B, 0x03, 0x04]) ||
        data.StartsWith<byte>([0x28, 0xB5, 0x2F, 0xFD]) ||
        data.StartsWith<byte>([0x1F, 0x8B, 0x08]);
}
