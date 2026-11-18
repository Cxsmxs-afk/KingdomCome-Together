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
    private volatile string _hostAuthorityFailureReason = "";
    // Stays set throughout the load and holds. NPC decisions are not a quest-mirror feature toggle.
    private bool HostAuthorityOwned => _hostAuthorityPrepared && !_isDamageAuthority;

    private void HostAuthorityGuard(long generation, CancellationToken ct, Func<bool>? owner = null)
    {
        ProcessorFrameScope.ThrowIfAbandoned(); ct.ThrowIfCancellationRequested();
        if (generation != Interlocked.Read(ref _hostAuthorityGeneration) || (owner is not null && !owner()))
            throw new OperationCanceledException("authority owner was superseded", ct);
    }

    private async Task<bool> PushHostAuthorityRoleAsync(byte role, CancellationToken ct = default)
    {
        using var scope = ProcessorFrameScope.Link(ct);
        long generation = Interlocked.Read(ref _hostAuthorityGeneration);
        HostAuthorityGuard(generation, scope.Token);
        var body = new byte[5];
        BitConverter.GetBytes(W216Switch.NativeBits(W216On)).CopyTo(body, 0);
        body[4] = role;
        var r = await _combat.Wo163Async(Protocol.Wo216OpConfig, body, scope.Token);
        HostAuthorityGuard(generation, scope.Token);
        if (r is not { Ok: true }) return false;
        _w216RolePushed = role;
        return true;
    }

    private async Task<bool> ArmHostAuthorityAsync(Action guard, CancellationToken ct)
    {
        guard();
        _hostAuthorityFailureReason = "";
        try
        {
            await _transport.ExecuteNowAsync("if KCD2MP_HostAuthority then KCD2MP_HostAuthority(true,false) end", ct);
            guard();
            var probe = await HostAuthorityReadiness.ProbeAndConfigureAsync(async () =>
            {
                guard();
                var r = await _combat.Wo163Async(Protocol.Wo216OpBrains, [], ct);
                guard();
                return r is { Ok: true } ready ? Encoding.ASCII.GetString(ready.Payload) : null;
            }, async () => { guard(); bool pushed = await PushHostAuthorityRoleAsync(2, ct); guard(); return pushed; }, ct);
            guard();
            _hostAuthorityFailureReason = probe.FailureReason;
            if (_hostAuthorityFailureReason.Length != 0)
            {
                Console.WriteLine($"MP-AUTHORITY unavailable reason={_hostAuthorityFailureReason} native={probe.NativeStatus}");
                return false;
            }
            _hostAuthorityPrepared = true;
            bool configured = await Wo200PushConfigCoreAsync(ct);
            guard();
            if (!configured) { _hostAuthorityFailureReason = HostAuthorityFailure.NotConfirmed; return false; }
            await _transport.ExecuteNowAsync("if KCD2MP_HostAuthority then KCD2MP_HostAuthority(true,true) end", ct);
            guard();
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            guard();
            _hostAuthorityFailureReason = HostAuthorityFailure.NotConfirmed;
            Console.WriteLine($"MP-AUTHORITY unavailable reason={_hostAuthorityFailureReason}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> PrepareHostAuthorityAsync(JoinerJoin join, CancellationToken ct = default)
    {
        using var scope = ProcessorFrameScope.Link(ct);
        long generation = Interlocked.Read(ref _hostAuthorityGeneration);
        void Guard() => HostAuthorityGuard(generation, scope.Token, () => ReferenceEquals(_jj, join) && !join.Abandoned.Task.IsCompleted);
        await _hostAuthorityGate.WaitAsync(scope.Token);
        try
        {
            Guard();
            if (generation != Interlocked.Read(ref _hostAuthorityGeneration) || _isDamageAuthority ||
                !ReferenceEquals(_jj, join) || join.Abandoned.Task.IsCompleted) return false;
            bool armed = await ArmHostAuthorityAsync(Guard, scope.Token);
            Guard();
            if (!armed)
            {
                _hostAuthorityPrepared = false;
                try { Guard(); await PushHostAuthorityRoleAsync(0, scope.Token); Guard(); await Wo200PushConfigCoreAsync(scope.Token); Guard(); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Console.WriteLine($"MP-AUTHORITY refused-join cleanup: {ex.GetType().Name}"); }
                Console.WriteLine("MP-AUTHORITY join refused: native brain/scheduler and story gates are unavailable or the join was cancelled");
                return false;
            }
            Console.WriteLine($"MP-AUTHORITY join 0x{join.JoinId:x8}: decision_gate=armed story_gate=armed before load/Ready");
            return true;
        }
        finally { _hostAuthorityGate.Release(); }
    }

    private async Task ResetHostAuthorityAsync(string why, CancellationToken ct = default)
    {
        using var scope = ProcessorFrameScope.Link(ct);
        ProcessorFrameScope.ThrowIfAbandoned(); scope.Token.ThrowIfCancellationRequested();
        long generation = Interlocked.Increment(ref _hostAuthorityGeneration);
        _hostAuthorityPrepared = false;
        await _hostAuthorityGate.WaitAsync(scope.Token);
        try
        {
            HostAuthorityGuard(generation, scope.Token);
            if (generation != Interlocked.Read(ref _hostAuthorityGeneration)) return;
            _hostAuthorityFailureReason = "";
            _hostAuthorityPrepared = false;   // an older in-flight arm may have completed while this reset waited
            await PushHostAuthorityRoleAsync(W137Host ? (byte)1 : (byte)0, scope.Token);
            HostAuthorityGuard(generation, scope.Token);
            await Wo200PushConfigCoreAsync(scope.Token);
            HostAuthorityGuard(generation, scope.Token);
            await _transport.ExecuteNowAsync("if KCD2MP_HostAuthority then KCD2MP_HostAuthority(false,false) end", scope.Token);
            HostAuthorityGuard(generation, scope.Token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Console.WriteLine($"MP-AUTHORITY reset ({why}): {ex.GetType().Name}"); }
        finally { _hostAuthorityGate.Release(); }
    }

    // Called only by the calendar's confirmed same-world reconnect, never by an arbitrary periodic tick.
    private async Task<bool> ResumeHostAuthorityAsync(long calendarGeneration, CancellationToken ct = default)
    {
        using var scope = ProcessorFrameScope.Link(ct);
        long generation = Interlocked.Read(ref _hostAuthorityGeneration);
        void Guard() => HostAuthorityGuard(generation, scope.Token, () => calendarGeneration == Interlocked.Read(ref _hostCalendarGeneration));
        await _hostAuthorityGate.WaitAsync(scope.Token);
        try
        {
            Guard();
            if (generation != Interlocked.Read(ref _hostAuthorityGeneration) || calendarGeneration != Interlocked.Read(ref _hostCalendarGeneration)) return false;
            _hostAuthorityPrepared = true;   // confirmed host world: retain suppression intent during a transient pipe failure
            bool armed = await ArmHostAuthorityAsync(Guard, scope.Token);
            Guard();
            return armed;
        }
        finally { _hostAuthorityGate.Release(); }
    }
}

public static class HostAuthorityReadiness
{
    public readonly record struct Preparation(string FailureReason, string NativeStatus);

    public static async Task<Preparation> ProbeAndConfigureAsync(Func<Task<string?>> probe, Func<Task<bool>> configure, CancellationToken ct = default)
    {
        using var scope = ProcessorFrameScope.Link(ct);
        void Guard() { ProcessorFrameScope.ThrowIfAbandoned(); scope.Token.ThrowIfCancellationRequested(); }
        Guard();
        // Role 2 is deliberately refused when a required hook is absent. Read
        // readiness first so that refusal cannot hide which game integration failed.
        string status = await probe() ?? "";
        Guard();
        string failure = HostAuthorityFailure.FromStatus(status);
        if (failure.Length != 0) return new(failure, status);
        bool configured = await configure();
        Guard();
        return new(configured ? "" : HostAuthorityFailure.NotConfirmed, status);
    }

    public static bool Armed(string status)
    {
        var fields = status.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return fields.Contains("decision_gate=armed") && fields.Contains("story_gate=armed");
    }
}
