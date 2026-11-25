// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
namespace KcdMp.Client;

/// <summary>A frame may give up without leaving its continuations authorized to write later.
/// Completed frames keep their explicitly queued work; abandoned frames lose that authority.</summary>
public static class ProcessorFrameScope
{
    private static readonly AsyncLocal<Lease?> Ambient = new();
    public enum Outcome { Completed, TimedOut, Faulted, SessionCancelled }
    public readonly record struct Result(Outcome Status, long ElapsedMs, Exception? Error = null);

    public sealed class Lease
    {
        private readonly CancellationToken _parent;
        private readonly CancellationTokenSource _cancel;
        private readonly CancellationToken _runningToken;
        private Task _cancellation = Task.CompletedTask;
        private int _released;
        internal CancellationToken ParentToken => _parent;
        private int _state; // 0 running, 1 completed, 2 abandoned
        internal Lease(CancellationToken parent)
        {
            _parent = parent;
            _cancel = CancellationTokenSource.CreateLinkedTokenSource(parent);
            _runningToken = _cancel.Token;
        }
        public bool IsAbandoned => Volatile.Read(ref _state) == 2 || _parent.IsCancellationRequested;
        public CancellationToken Token => IsAbandoned ? new CancellationToken(true)
            : Volatile.Read(ref _state) == 1 ? _parent : _runningToken;
        public void ThrowIfAbandoned() => Token.ThrowIfCancellationRequested();
        internal void Complete() => Interlocked.CompareExchange(ref _state, 1, 0);
        internal void Abandon()
        {
            Interlocked.Exchange(ref _state, 2);
            // A transport's cancellation callback must never hold up the processor itself.
            _cancellation = _cancel.CancelAsync();
            _ = _cancellation.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        internal void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            if (_cancellation.IsCompleted) _cancel.Dispose();
            else _ = _cancellation.ContinueWith(_ => _cancel.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    public static Lease? Capture() => Ambient.Value;
    public static CancellationToken CurrentToken => Ambient.Value?.Token ?? CancellationToken.None;
    public static void ThrowIfAbandoned() => Ambient.Value?.ThrowIfAbandoned();

    public sealed class TokenLink : IDisposable
    {
        private readonly CancellationTokenSource? _linked;
        public CancellationToken Token { get; }
        internal TokenLink(CancellationToken caller, CancellationToken frame, CancellationToken session)
        {
            var tokens = new[] { caller, frame, session }.Where(t => t.CanBeCanceled).Distinct().ToArray();
            if (tokens.Length == 0) Token = CancellationToken.None;
            else if (tokens.Length == 1) Token = tokens[0];
            else { _linked = CancellationTokenSource.CreateLinkedTokenSource(tokens); Token = _linked.Token; }
        }
        public void Dispose() => _linked?.Dispose();
    }
    public static TokenLink Link(CancellationToken caller = default) => new(caller, CurrentToken, Ambient.Value?.ParentToken ?? CancellationToken.None);

    public static Task<Result> RunAsync(Func<CancellationToken, Task> handler, int timeoutMs, CancellationToken parent = default)
        => RunAsync(handler, timeoutMs, parent, TimeProvider.System);

    internal static async Task<Result> RunAsync(Func<CancellationToken, Task> handler, int timeoutMs,
        CancellationToken parent, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (timeoutMs < 1) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        var lease = new Lease(parent);
        long start = timeProvider.GetTimestamp();
        long Elapsed() => (long)timeProvider.GetElapsedTime(start).TotalMilliseconds;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(parent);
        Task work = Task.Run(async () =>
        {
            var previous = Ambient.Value;
            Ambient.Value = lease;
            try { lease.ThrowIfAbandoned(); await handler(lease.Token); }
            finally { Ambient.Value = previous; }
        }, CancellationToken.None);
        // Leave a small part of the configured upper budget for cancellation and
        // accounting. ElapsedMs is always the actual stopwatch value, never clamped.
        int reserveMs = Math.Min(50, Math.Max(1, timeoutMs / 20));
        int deadlineMs = Math.Max(1, timeoutMs - reserveMs);
        var timeout = Task.Delay(TimeSpan.FromMilliseconds(deadlineMs), timeProvider, deadline.Token);
        if (await Task.WhenAny(work, timeout) != work)
        {
            lease.Abandon();
            // Observe and clean up a late completion without ever awaiting it on this lane.
            _ = work.ContinueWith(t => { _ = t.Exception; lease.Release(); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return new(parent.IsCancellationRequested ? Outcome.SessionCancelled : Outcome.TimedOut, Elapsed());
        }
        deadline.Cancel();
        try
        {
            await work;
            parent.ThrowIfCancellationRequested();
            lease.Complete();
            return new(Outcome.Completed, Elapsed());
        }
        catch (OperationCanceledException ex) when (parent.IsCancellationRequested)
        {
            lease.Abandon();
            return new(Outcome.SessionCancelled, Elapsed(), ex);
        }
        catch (Exception ex)
        {
            // HttpClient.Timeout also throws OCE. It is a frame failure, not a session cancellation.
            lease.Abandon();
            return new(Outcome.Faulted, Elapsed(), ex);
        }
        finally { lease.Release(); }
    }
}
