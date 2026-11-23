// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;

namespace KcdMp.Wire;

/// <summary>Local ownership handshake between one launcher and its own session agent.</summary>
public sealed class LauncherAgentControl
{
    public const string EnvironmentKey = "KCDMP_LAUNCHER_AGENT_CONTROL";
    public const string StopCommand = "kcdmp-launcher-stop";
    private const uint Header = 0x3147434B;
    private const int StampLength = 16;
    private readonly string _file;

    private LauncherAgentControl(Guid id)
    {
        Id = id.ToString("N");
        _file = Path.Combine(Path.GetTempPath(), $"kcdmp-agent-control-{Id}.lock");
    }

    public string Id { get; }

    public static LauncherAgentControl Create()
    {
        var control = new LauncherAgentControl(Guid.NewGuid());
        using var file = new FileStream(control._file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        return control;
    }

    public static LauncherAgentControl? FromId(string id) =>
        Guid.TryParseExact(id, "N", out Guid unique) && unique != Guid.Empty ? new(unique) : null;

    public static long ReadProcessStart(Process process)
    {
        if (!OperatingSystem.IsLinux()) return process.StartTime.ToUniversalTime().Ticks;
        // .NET converts Linux boot ticks to wall time independently in each
        // process. Compare the kernel start time before that conversion.
        string stat = File.ReadAllText($"/proc/{process.Id}/stat");
        int nameEnd = stat.LastIndexOf(')');
        if (nameEnd < 0) return 0;
        var fields = stat[(nameEnd + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 19 && long.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out long start)
            ? start : 0;
    }

    public bool StampProcess(int pid, long startTime)
    {
        if (pid <= 0 || startTime <= 0 || Open(FileAccess.ReadWrite, FileShare.Read) is not { } file) return false;
        using (file)
        {
            if (file.Length != 0) return false;
            Span<byte> stamp = stackalloc byte[StampLength];
            BinaryPrimitives.WriteUInt32LittleEndian(stamp, Header);
            BinaryPrimitives.WriteInt32LittleEndian(stamp[4..], pid);
            BinaryPrimitives.WriteInt64LittleEndian(stamp[8..], startTime);
            file.Write(stamp);
            file.Flush(flushToDisk: true);
            return true;
        }
    }

    /// <summary>Hold from admission through an exact terminal engine receipt.</summary>
    public FileStream? TryProtectEngineRead() => Open(FileAccess.Read, FileShare.Read);

    /// <summary>Hold until the stamped process exits, fencing new submissions as well as existing ones.</summary>
    public FileStream? TryBeginStop(int pid, long startTime, out string reason)
    {
        // An early reconnect click must not lock out the child's initial stamp.
        using (var stamp = Open(FileAccess.Read, FileShare.ReadWrite))
        {
            reason = "agent-identity-unconfirmed";
            if (stamp is null || !Matches(stamp, pid, startTime)) return null;
        }
        reason = "engine-read-or-control-unavailable";
        if (Open(FileAccess.ReadWrite, FileShare.None) is not { } file) return null;
        try
        {
            reason = "agent-identity-unconfirmed";
            if (!Matches(file, pid, startTime)) return null;
            reason = "";
            return file;
        }
        finally { if (reason.Length != 0) file.Dispose(); }
    }

    private static bool Matches(FileStream file, int pid, long startTime)
    {
        if (file.Length != StampLength) return false;
        Span<byte> stamp = stackalloc byte[StampLength];
        file.ReadExactly(stamp);
        return BinaryPrimitives.ReadUInt32LittleEndian(stamp) == Header &&
            BinaryPrimitives.ReadInt32LittleEndian(stamp[4..]) == pid &&
            BinaryPrimitives.ReadInt64LittleEndian(stamp[8..]) == startTime;
    }

    public void Remove()
    {
        try { File.Delete(_file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private FileStream? Open(FileAccess access, FileShare share)
    {
        try { return new FileStream(_file, FileMode.Open, access, share); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
