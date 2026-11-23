// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
namespace KcdMp.Client;

public partial class GameBridge
{
    // World of the save actually loaded by the last successful join. A new join
    // epoch (a transfer or retry) is not evidence that the host loaded a new save.
    private uint _t3JoinedHostWorld;
    private string _t3RejoinReason = "-";

    // Epochs only increase in the live host process. A lower counter after an
    // agent restart is ambiguous, not proof that another save was loaded.
    private bool T3HostWorldChanged => _t3JoinedHostWorld != 0 && _w206.World > _t3JoinedHostWorld;

    private bool T3ArmInWorldRejoin(string reason)
    {
        bool changed = T3HostWorldChanged;
        Console.WriteLine($"T3-AUTO-REJOIN trigger={reason} decision={(changed ? "arm" : "wait-world-proof")} loaded_epoch={_t3JoinedHostWorld} host_epoch={_w206.World} joined={_joinedWorld}");
        if (!_joinedWorld || !changed) return false;
        _t3RejoinReason = reason;
        if (!_rejoinPending) { _rejoinPending = true; _rejoinSinceUtc = DateTime.UtcNow; }
        return true;
    }

    private bool T3AdmitInWorldRejoin() => T3AdmitInWorldRejoinFrom(null);

    private bool T3AdmitInWorldRejoinFrom(string? trigger)
    {
        string reason = trigger ?? _t3RejoinReason;
        if (T3HostWorldChanged)
        {
            if (trigger is null) T3Increment("auto_rejoins_in_world");
            Console.WriteLine($"T3-AUTO-REJOIN trigger={reason} decision=request loaded_epoch={_t3JoinedHostWorld} host_epoch={_w206.World}");
            return true;
        }
        Console.WriteLine($"T3-AUTO-REJOIN trigger={reason} decision=suppress-unchanged-or-unknown loaded_epoch={_t3JoinedHostWorld} host_epoch={_w206.World}");
        if (trigger is null) _rejoinPending = false;
        return false;
    }

    private void T3AfterJoinedWorldCleanup()
    {
        if (!_newWorldLeavePending && !_leaveInProgress && T3HostWorldChanged)
            T3ArmInWorldRejoin("host-world-changed-during-join");
    }
}
