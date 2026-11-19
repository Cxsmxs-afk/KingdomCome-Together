// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using KcdMp.Wire;

namespace KcdMp.Client;

public partial class GameBridge
{
    private static readonly TimeSpan JoinCalendarRepeat = TimeSpan.FromMilliseconds(500);

    private sealed class HeldJoinCalendar(Stream stream, uint world, uint joinEpoch, CancellationTokenSource stop)
    {
        public readonly Stream Stream = stream;
        public readonly uint World = world;
        public readonly uint JoinEpoch = joinEpoch;
        public readonly CancellationTokenSource Stop = stop;
        public readonly CancellationToken Token = stop.Token;
        public readonly TaskCompletionSource Receipt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Repeats = Task.CompletedTask;
    }

    private async Task SendHeldJoinCalendarAsync(HostJoin join, string line, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var life = T3SideLease();
        T3SideGuard(life);
        if (_wo122Stream is not Stream stream) throw new IOException("no relay connection for the frozen calendar");
        // Live clock rows still use an unscoped envelope. Bootstrap rows carry the
        // actual attempt in the envelope and the originating world in the token.
        var packet = new LootMsg(Protocol.W164Clock, life.World, line).BuildUp(Protocol.W164Up, join.Joiner, join.JoinId);
        var calendar = new HeldJoinCalendar(stream, life.World, _w206.Join,
            CancellationTokenSource.CreateLinkedTokenSource(join.DeadlineCts?.Token ?? join.Cts.Token, _wo122Ct, life.Token));
        Volatile.Write(ref join.Calendar, calendar); // An immediate reply can arrive before the first write returns.
        calendar.Token.ThrowIfCancellationRequested();
        if (!HeldJoinCalendarCurrent(join, calendar)) throw new IOException("the held calendar's join, world or link was retired");

        await WritePacketAsync(stream, packet, calendar.Token);
        Interlocked.Increment(ref _w200ClockOut);
        calendar.Token.ThrowIfCancellationRequested();
        ct.ThrowIfCancellationRequested();
        Console.WriteLine($"MP-CALENDAR host join=0x{join.JoinId:x8} world={calendar.World} snapshot=sent");
        calendar.Repeats = RepeatHeldJoinCalendarAsync(join, calendar, packet);
    }

    private bool HeldJoinCalendarCurrent(HostJoin join, HeldJoinCalendar calendar) =>
        !calendar.Token.IsCancellationRequested && ReferenceEquals(Volatile.Read(ref join.Calendar), calendar) &&
        ReferenceEquals(Volatile.Read(ref _hostJoin), join) && ReferenceEquals(_wo122Stream, calendar.Stream) &&
        W137Host && join.PausedUtc is not null && _where == GameWhere.World && calendar.World != 0 &&
        calendar.World == _w206.World && calendar.JoinEpoch == _w206.Join;

    private async Task RepeatHeldJoinCalendarAsync(HostJoin join, HeldJoinCalendar calendar, byte[] packet)
    {
        bool reportedRetry = false;
        bool reportedFailure = false;
        try
        {
            while (!calendar.Receipt.Task.IsCompleted && HeldJoinCalendarCurrent(join, calendar))
            {
                var delay = Task.Delay(JoinCalendarRepeat, calendar.Token);
                if (await Task.WhenAny(delay, calendar.Receipt.Task) == calendar.Receipt.Task) return;
                await delay;
                if (calendar.Receipt.Task.IsCompleted || !HeldJoinCalendarCurrent(join, calendar)) return;
                try
                {
                    await WritePacketAsync(calendar.Stream, packet, calendar.Token);
                    Interlocked.Increment(ref _w200ClockOut);
                    if (!reportedRetry)
                    {
                        reportedRetry = true;
                        Console.WriteLine($"MP-CALENDAR host join=0x{join.JoinId:x8} world={calendar.World} snapshot=repeated reason=receipt-pending");
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    if (!reportedFailure)
                    {
                        reportedFailure = true;
                        Console.WriteLine($"MP-CALENDAR host join=0x{join.JoinId:x8} world={calendar.World} snapshot=retry-failed reason={ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (calendar.Token.IsCancellationRequested || ProcessorFrameScope.Capture()?.IsAbandoned == true) { }
    }

    private void ObserveJoinCalendarReceipt(byte source, uint joinId, uint epoch, Stream stream)
    {
        var join = Volatile.Read(ref _hostJoin);
        if (join is null || join.JoinId != joinId || Volatile.Read(ref join.Calendar) is not { } calendar)
        {
            DropJoinCalendar(source, joinId, "inactive-receipt");
            return;
        }
        string? rejection = source != join.Joiner ? "receipt-source"
            : epoch != calendar.World ? "receipt-world"
            : !ReferenceEquals(stream, calendar.Stream) ? "receipt-link"
            : !HeldJoinCalendarCurrent(join, calendar) ? "retired-receipt"
            : null;
        if (rejection is not null)
        {
            DropJoinCalendar(source, joinId, rejection);
            return;
        }
        if (calendar.Receipt.TrySetResult())
            Console.WriteLine($"MP-CALENDAR host join=0x{joinId:x8} world={epoch} receipt=accepted source={source}");
    }

    private static async Task StopHeldJoinCalendarAsync(HostJoin join)
    {
        // Only RunHostJoin owns the publication and retirement of this snapshot.
        if (Volatile.Read(ref join.Calendar) is not { } calendar) return;
        calendar.Receipt.TrySetResult();
        Volatile.Write(ref join.Calendar, null);
        try
        {
            await calendar.Stop.CancelAsync();
            await calendar.Repeats;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MP-CALENDAR host join=0x{join.JoinId:x8} snapshot=stop-failed reason={ex.GetType().Name}: {ex.Message}");
        }
        finally { calendar.Stop.Dispose(); }
    }
}
