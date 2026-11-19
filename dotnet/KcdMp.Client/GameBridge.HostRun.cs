// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using KcdMp.Wire;

namespace KcdMp.Client;

public partial class GameBridge
{
    private sealed record ActiveHostRun(Guid Id, HostRunSave Loaded, string SavesRoot, LogTailGameTransport Engine,
        long Incarnation, uint Epoch, ulong Generation);
    private sealed record HostRunWrite(ActiveHostRun Run, string Path, Task<HostRunSave?> Written);

    private readonly HostRunLedger _h21HostRuns = new(Path.Combine(Path.GetDirectoryName(HenryStore.DefaultRoot())!, "host-runs.json"));
    private readonly object _h21HostRunGate = new();
    private ActiveHostRun? _h21ActiveHostRun;
    private HostRunWrite? _h21HostRunWrite;
    private long _h21HostLoadSerial;
    private bool H21Hosting => config.IsHosting && _sharedWorld && _combatRoleApplied && _isDamageAuthority;

    private bool H21HostRunCurrent(ActiveHostRun run)
    {
        var scope = _worldLoad.Current;
        return H21Hosting && ReferenceEquals(_transport, run.Engine) && run.Engine.EngineLogIncarnation == run.Incarnation &&
            scope.Generation == run.Generation && scope.HostWorld == run.Epoch && _w206.World == run.Epoch &&
            scope.Phase is WorldLoadPhase.Settle or WorldLoadPhase.Play;
    }

    private void H21HostGameplayStarted()
    {
        if (!H21Hosting || _transport is not LogTailGameTransport engine) return;
        long serial = H21RetireActiveHostRun();
        if (engine.DispatchingTerminalSave is not { } receipt) return;
        var scope = _worldLoad.Current;
        long incarnation = engine.EngineLogIncarnation;
        _ = Task.Run(() =>
        {
            string? saves = BenchInstance.SavesDirFromLog(engine.LogPath);
            if (saves is null || H21EngineSavePath(saves, receipt) is not { } path ||
                !H21TryReadHostAnchor(saves, path, out var anchor) || H21ResolveHostRun(anchor) is not Guid id)
            {
                Console.WriteLine("MP-H21-RUN result=refused reason=engine-lineage-unknown");
                return;
            }
            H21PublishActiveHostRun(new(id, anchor, saves, engine, incarnation, scope.HostWorld, scope.Generation), serial);
        });
    }

    private Guid? H21ResolveHostRun(HostRunSave anchor)
    {
        if (_h21HostRuns.Resolve(anchor) is Guid existing) return existing;
        // A legacy save or engine load alone cannot create a campaign ID. Enrollment
        // requires the exact same file in a previously verified O4 creation reservation.
        var checkpointStore = new LocalStartCheckpointStore(LocalStartCheckpointStore.DefaultRoot(), anchor.ProfileId);
        string display = $"playline{anchor.Playline}/{Path.GetFileNameWithoutExtension(anchor.FileName)}";
        return checkpointStore.TryFindExact(display, anchor.Sha256, anchor.Build, out var checkpoint)
            ? _h21HostRuns.RegisterPrologue(checkpoint.CreationId, anchor) : null;
    }

    private void H21PublishActiveHostRun(ActiveHostRun run, long serial)
    {
        lock (_h21HostRunGate)
        {
            if (serial != _h21HostLoadSerial || !H21HostRunCurrent(run)) return;
            Volatile.Write(ref _h21ActiveHostRun, run);
        }
        Console.WriteLine($"MP-H21-RUN result=active run={run.Id.ToString("N")[..8]} world={run.Epoch} gen={run.Generation}");
    }

    private long H21RetireActiveHostRun()
    {
        lock (_h21HostRunGate)
        {
            Volatile.Write(ref _h21ActiveHostRun, null);
            Volatile.Write(ref _h21HostRunWrite, null);
            return ++_h21HostLoadSerial;
        }
    }

    private void H21HostEngineSaveLine(string line)
    {
        const string start = "SaveGame: '";
        if (!line.StartsWith(start, StringComparison.Ordinal) ||
            Volatile.Read(ref _h21ActiveHostRun) is not { } run || !H21HostRunCurrent(run)) return;
        int end = line.IndexOf("' done.", start.Length, StringComparison.Ordinal);
        if (end < 0 || H21EngineSavePath(run.SavesRoot, line[start.Length..end]) is not { } path) return;
        var written = Task.Run(() =>
        {
            if (!H21TryReadHostAnchor(run.SavesRoot, path, out var anchor) || !H21HostRunCurrent(run) ||
                !ReferenceEquals(Volatile.Read(ref _h21ActiveHostRun), run) ||
                _h21HostRuns.RecordDescendant(run.Id, run.Loaded, anchor) != run.Id) return null;
            return anchor;
        });
        Volatile.Write(ref _h21HostRunWrite, new(run, path, written));
    }

    private async Task<WorldRunOffer?> H21HostOfferRunAsync(string path, byte[] bytes, string md5)
    {
        if (Volatile.Read(ref _h21ActiveHostRun) is not { } run || !H21HostRunCurrent(run)) return null;
        string? full = H21EngineSavePath(run.SavesRoot, path);
        if (full is null) return null;
        var anchor = run.Loaded;
        if (Volatile.Read(ref _h21HostRunWrite) is { } write && ReferenceEquals(write.Run, run) &&
            string.Equals(write.Path, full, H21PathComparison))
        {
            if (await write.Written is not { } saved) return null;
            anchor = saved;
        }
        if (SaveDisplay(full).ToLowerInvariant() != $"playline{anchor.Playline}/{anchor.FileName}" ||
            !string.Equals(md5, anchor.Md5, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), anchor.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !H21HostRunCurrent(run) || !ReferenceEquals(Volatile.Read(ref _h21ActiveHostRun), run)) return null;
        return new WorldRunOffer(run.Id, Wo157InstallKey(), run.Epoch, run.Generation);
    }

    private bool H21HostOfferRunCurrent(WorldRunOffer run) => Volatile.Read(ref _h21ActiveHostRun) is { } active &&
        active.Id == run.RunId && active.Epoch == run.HostWorldEpoch && active.Generation == run.HostGeneration && H21HostRunCurrent(active);

    private bool H21GuestOfferRunCurrent(byte host, uint receivedWorld, WorldRunOffer run) => _hostModeKnown &&
        host == _hostModeFrom && run.HostWorldEpoch == receivedWorld && receivedWorld == _w206.World;

    private static StringComparison H21PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string? H21EngineSavePath(string saves, string reported)
    {
        const string alias = "%USER%/saves/";
        try
        {
            if (!Path.IsPathFullyQualified(saves)) return null;
            string value = reported.Replace('\\', '/');
            if (value.StartsWith(alias, StringComparison.OrdinalIgnoreCase)) value = Path.Combine(saves, value[alias.Length..]);
            if (!Path.IsPathFullyQualified(value)) return null;
            string full = Path.GetFullPath(value);
            if (WorldSaved.ParsePath(full) is not { Playline: <= 4 } save ||
                !string.Equals(Path.GetDirectoryName(full), Path.Combine(Path.GetFullPath(saves), $"playline{save.Playline}"), H21PathComparison)) return null;
            return full;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool H21TryReadHostAnchor(string saves, string path, out HostRunSave anchor)
    {
        anchor = null!;
        try
        {
            if (H21EngineSavePath(saves, path) is not { } full || WorldSaved.ParsePath(full) is not { } save ||
                (File.GetAttributes(full) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return false;
            for (DirectoryInfo? dir = new(Path.GetDirectoryName(full)!); dir is not null; dir = dir.Parent)
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            byte[] bytes;
            using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length is <= 0 or > Protocol.WorldMaxBytes) return false;
                bytes = new byte[checked((int)stream.Length)];
                stream.ReadExactly(bytes);
            }
            var check = WhsSave.Verify(bytes);
            if (!check.Ok) return false;
            var inflated = WhsSave.Inflate(bytes);
            if (WhsSave.ReadSeed(inflated.Raw) is not uint seed ||
                WhsSave.DescriptionSummary(inflated.Desc).GetValueOrDefault("BuildInfo") is not { } build ||
                !WhsSave.PlayerOf(inflated.Raw).IsHenry) return false;
            string profileId = LocalStartCheckpointStore.ProfileId(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(saves))!);
            anchor = new(profileId, save.Playline, Path.GetFileName(full).ToLowerInvariant(), seed, build,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), check.Md5.ToLowerInvariant());
            return anchor.IsValid;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException) { return false; }
    }
}
