// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Text;
using KcdMp.Wire;

namespace KcdMp.Client;

public partial class GameBridge
{
    private readonly SemaphoreSlim _hostAuthorityGate = new(1, 1);
    private volatile bool _hostAuthorityPrepared;
    private long _hostAuthorityGeneration;
    // Stays set throughout the load and holds. NPC decisions are not a quest-mirror feature toggle.
    private bool HostAuthorityOwned => _hostAuthorityPrepared && !_isDamageAuthority;

    private async Task<bool> PushHostAuthorityRoleAsync(byte role)
    {
        var body = new byte[5];
        BitConverter.GetBytes(W216Switch.NativeBits(W216On)).CopyTo(body, 0);
        body[4] = role;
        var r = await _combat.Wo163Async(Protocol.Wo216OpConfig, body);
        if (r is not { Ok: true }) return false;
        _w216RolePushed = role;
        return true;
    }

    private async Task<bool> ArmHostAuthorityAsync()
    {
        await _transport.ExecuteNowAsync("if KCD2MP_HostAuthority then KCD2MP_HostAuthority(true,false) end");
        if (!await PushHostAuthorityRoleAsync(2)) return false;
        var r = await _combat.Wo163Async(Protocol.Wo216OpBrains, []);
        if (r is not { Ok: true } ready || !HostAuthorityReadiness.Armed(Encoding.ASCII.GetString(ready.Payload))) return false;
        _hostAuthorityPrepared = true;
        if (!await Wo200PushConfigCoreAsync()) return false;
        await _transport.ExecuteNowAsync("if KCD2MP_HostAuthority then KCD2MP_HostAuthority(true,true) end");
        return true;
    }

    private async Task<bool> PrepareHostAuthorityAsync(JoinerJoin join)
    {
        long generation = Interlocked.Read(ref _hostAuthorityGeneration);
        await _hostAuthorityGate.WaitAsync();
        try
        {
            if (generation != Interlocked.Read(ref _hostAuthorityGeneration) || _isDamageAuthority ||
                !ReferenceEquals(_jj, join) || join.Abandoned.Task.IsCompleted) return false;
            bool armed = await ArmHostAuthorityAsync();
            if (!armed || generation != Interlocked.Read(ref _hostAuthorityGeneration) ||
                !ReferenceEquals(_jj, join) || join.Abandoned.Task.IsCompleted)
            {
                _hostAuthorityPrepared = false;
                await PushHostAuthorityRoleAsync(0);
                await Wo200PushConfigCoreAsync();
                Console.WriteLine("MP-AUTHORITY join refused: native brain/scheduler and story gates are unavailable or the join was cancelled");
                return false;
            }
            Console.WriteLine($"MP-AUTHORITY join 0x{join.JoinId:x8}: decision_gate=armed story_gate=armed before load/Ready");
            return true;
        }
        finally { _hostAuthorityGate.Release(); }
    }

    private async Task ResetHostAuthorityAsync(string why)
    {
        long generation = Interlocked.Increment(ref _hostAuthorityGeneration);
        _hostAuthorityPrepared = false;
        await _hostAuthorityGate.WaitAsync();
        try
        {
            if (generation != Interlocked.Read(ref _hostAuthorityGeneration)) return;
            _hostAuthorityPrepared = false;   // an older in-flight arm may have completed while this reset waited
            await PushHostAuthorityRoleAsync(W137Host ? (byte)1 : (byte)0);
            await Wo200PushConfigCoreAsync();
            await _transport.ExecuteNowAsync("if KCD2MP_HostAuthority then KCD2MP_HostAuthority(false,false) end");
        }
        catch (Exception ex) { Console.WriteLine($"MP-AUTHORITY reset ({why}): {ex.GetType().Name}"); }
        finally { _hostAuthorityGate.Release(); }
    }

    // Called only by the calendar's confirmed same-world reconnect, never by an arbitrary periodic tick.
    private async Task<bool> ResumeHostAuthorityAsync(long calendarGeneration)
    {
        long generation = Interlocked.Read(ref _hostAuthorityGeneration);
        await _hostAuthorityGate.WaitAsync();
        try
        {
            if (generation != Interlocked.Read(ref _hostAuthorityGeneration) || calendarGeneration != Interlocked.Read(ref _hostCalendarGeneration)) return false;
            _hostAuthorityPrepared = true;   // confirmed host world: retain suppression intent during a transient pipe failure
            bool armed = await ArmHostAuthorityAsync();
            if (generation == Interlocked.Read(ref _hostAuthorityGeneration) && calendarGeneration == Interlocked.Read(ref _hostCalendarGeneration)) return armed;
            _hostAuthorityPrepared = false;
            await PushHostAuthorityRoleAsync(0);
            await Wo200PushConfigCoreAsync();
            return false;
        }
        finally { _hostAuthorityGate.Release(); }
    }
}

public static class HostAuthorityReadiness
{
    public static bool Armed(string status)
    {
        var fields = status.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return fields.Contains("decision_gate=armed") && fields.Contains("story_gate=armed");
    }
}
