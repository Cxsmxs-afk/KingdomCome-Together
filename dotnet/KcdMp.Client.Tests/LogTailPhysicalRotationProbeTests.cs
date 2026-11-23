// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Xunit.Abstractions;

namespace KcdMp.Client.Tests;

[Collection("Join calendar integration")]
public sealed class LogTailPhysicalRotationProbeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("replace", false)]
    [InlineData("replace", true)]
    [InlineData("truncate", true)]
    [InlineData("append", false)]
    public async Task Tail_receipts_follow_only_the_active_log_and_never_replay_history(string transition, bool bufferedTerminal)
    {
        var folder = Directory.CreateTempSubdirectory("kct-log-tail-");
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var releaseRead = new ManualResetEventSlim();
            bool changed = transition != "append";
            string path = Path.Combine(folder.FullName, "kcd.log");
            string old = path + ".old";
            const string historical = "Log Started at 2026-11-20  00:10:00\n[ CryAction ] historical marker\n" +
                "[CryAction] LoadGame: 'playline1/historical-save'\nGameplay started\nCSystem::Quit invoked\n";
            File.WriteAllText(path, historical + new string(' ', 4096) + "\n");
            // The ordinary case models the game's same open log writer. Rotation
            // cases replace or truncate the path and therefore open each write anew.
            using var writer = changed ? null : new FileStream(path, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            void Append(string text)
            {
                if (writer is null) File.AppendAllText(path, text);
                else
                {
                    writer.Write(Encoding.UTF8.GetBytes(text));
                    writer.Flush();
                }
            }

            await using var tail = new LogTailGameTransport(new HttpGameTransport("http://localhost", handler: new EmitterReply()), path);
            var firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var scanned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var receipts = new ConcurrentQueue<LogTailGameTransport.EngineSaveTerminal>();
            var reads = new ConcurrentQueue<string?>();
            var accepts = new ConcurrentQueue<string>();
            var quits = 0;
            var menus = 0;
            tail.LoadStarted += () =>
            {
                reads.Enqueue(tail.EngineReadSave);
                if (firstRead.TrySetResult() && changed) releaseRead.Wait(deadline.Token);
            };
            tail.SaveLoadAccepted += accepts.Enqueue;
            tail.EngineSaveFinished += receipts.Enqueue;
            tail.ModInitDetected += () => scanned.TrySetResult();
            tail.GameQuit += () => Interlocked.Increment(ref quits);
            tail.MainMenuShown += () => Interlocked.Increment(ref menus);
            long beforeAttach = tail.EngineLogIncarnation;
            await tail.StartAsync(deadline.Token);
            while (tail.EngineLogIncarnation == beforeAttach) await Task.Delay(2, deadline.Token);
            const string retired = "Loading saved game 'playline1/retired-save'\n" +
                "[CryAction] LoadGame: 'playline1/retired-save'\nPlayVideoOnly 'main_menu.bk2'\n" +
                "CSystem::Quit invoked\nLog Started at 2026-11-20  01:20:00\nGameplay started\n";
            async Task WaitForLineAsync(Task line, string stage)
            {
                try { await line.WaitAsync(deadline.Token); }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    Assert.Fail($"{transition}: timed out at {stage}, incarnation={tail.EngineLogIncarnation}, " +
                        $"reads={string.Join(',', reads)}, receipts={string.Join(',', receipts.Select(receipt => receipt.Path))}");
                }
            }

            Append("[CryAction] LoadGame: 'playline1/old-save'\n" + (bufferedTerminal ? retired : ""));
            await WaitForLineAsync(firstRead.Task, "first LoadGame");
            long oldIncarnation = tail.EngineLogIncarnation;
            try
            {
                if (transition == "replace")
                {
                    File.Move(path, old);
                    File.WriteAllText(path, historical);
                    File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(old));
                }
                else if (transition == "truncate") File.WriteAllText(path, historical);
            }
            finally { releaseRead.Set(); }

            if (changed)
            {
                using var reopenDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try
                {
                    while (tail.EngineLogIncarnation == oldIncarnation) await Task.Delay(2, reopenDeadline.Token);
                }
                catch (OperationCanceledException)
                {
                    output.WriteLine($"transition={transition} buffered_terminal={bufferedTerminal} old_incarnation={oldIncarnation} current_incarnation={tail.EngineLogIncarnation} receipts={string.Join(',', receipts.Select(r => r.Path))}");
                    Assert.True(tail.EngineLogIncarnation > oldIncarnation, "Tail did not adopt the active log.");
                }
            }
            string newRead = changed ? "Log Started at 2026-11-20  02:20:00\nLoading saved game 'playline1/new-save'\n" +
                "[CryAction] LoadGame: 'playline1/new-save'\n" : "";
            Append(newRead + "Gameplay started\n[KCD2-MP] MOD INIT\n");
            await WaitForLineAsync(scanned.Task, "Gameplay and MOD INIT");
            output.WriteLine($"transition={transition} buffered_terminal={bufferedTerminal} old_incarnation={oldIncarnation} current_incarnation={tail.EngineLogIncarnation} receipts={string.Join(',', receipts.Select(r => r.Path))} quits={quits} menus={menus} reads={string.Join(',', reads)} accepts={string.Join(',', accepts)}");
            var receipt = Assert.Single(receipts);
            Assert.Equal(changed ? "playline1/new-save" : "playline1/old-save", receipt.Path);
            Assert.Equal(changed, receipt.Incarnation > oldIncarnation);
            Assert.True(receipt.Loaded);
            Assert.Equal(0, quits);
            Assert.Equal(0, menus);
            Assert.Equal(changed ? ["playline1/old-save", "playline1/new-save"] : new[] { "playline1/old-save" }, reads.ToArray());
            Assert.Equal(changed ? ["playline1/new-save"] : Array.Empty<string>(), accepts.ToArray());
        }
        finally { folder.Delete(recursive: true); }
    }

    private sealed class EmitterReply : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
