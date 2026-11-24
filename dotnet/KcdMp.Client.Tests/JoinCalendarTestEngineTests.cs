// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Reflection;

namespace KcdMp.Client.Tests;

public sealed class JoinCalendarTestEngineTests
{
    [Fact]
    public async Task Http_dispatch_returns_while_the_engine_log_is_busy()
    {
        var root = Directory.CreateTempSubdirectory("kct-join-console-boundary-");
        try
        {
            await using var engine = new JoinCalendarTestEngine(Path.Combine(root.FullName, "game.log"), root.FullName, null, () => { });
            var logGate = typeof(JoinCalendarTestEngine).GetField("_logGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine)!;
            var logLocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var deadline = TimeSpan.FromSeconds(5);
            using var releaseLog = new ManualResetEventSlim();
            Task log = Task.Run(() =>
            {
                lock (logGate)
                {
                    logLocked.SetResult();
                    releaseLog.Wait();
                }
            });
            Task<Task> handoff = Task.FromResult(Task.CompletedTask);
            try
            {
                await logLocked.Task.WaitAsync(deadline);
                handoff = Task.Factory.StartNew(
                    () => engine.Transport.ExecuteNowAsync("KCD2MP_W154Where(\"r12345678\")"),
                    CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
                try
                {
                    await handoff.WaitAsync(deadline);
                }
                catch (TimeoutException error)
                {
                    throw new TimeoutException("The HTTP handoff waited synchronously for the held log.", error);
                }
                Assert.Equal(0, engine.WhereQueries);
            }
            finally
            {
                releaseLog.Set();
                await log.WaitAsync(deadline);
                await handoff.Unwrap().WaitAsync(deadline).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            await handoff.Unwrap().WaitAsync(deadline);
            Assert.Equal(1, engine.WhereQueries);
        }
        finally { root.Delete(recursive: true); }
    }
}
