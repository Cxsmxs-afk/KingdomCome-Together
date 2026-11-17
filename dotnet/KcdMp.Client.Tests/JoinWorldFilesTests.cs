// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace KcdMp.Client.Tests;

/// <summary>A slow save catalogue must not throw away the verified world or let an abandoned join load it.</summary>
public sealed class JoinWorldFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kcdmp-join-retention-" + Guid.NewGuid().ToString("N"));

    public JoinWorldFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string World(string name = "mpworld1116a6a9.whs")
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("synthetic placed world: " + name));
        return path;
    }

    private static CombatPipe.SaveListReport Report(bool listed) => new(listed, listed ? 0 : -1, listed ? 1 : 0, 2, 2, 0, "mpworld1116a6a9");

    private static Task NoDelay(TimeSpan _, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Timeout_then_missing_then_listed_reuses_the_verified_file_and_find_only_queries()
    {
        string path = World();
        byte[] digest = SHA256.HashData(File.ReadAllBytes(path));
        const int playline = 2;
        string name = Path.GetFileNameWithoutExtension(path);
        var requests = new List<(byte Op, int Playline, string Name, string Path)>();
        var owner = new object();
        var files = new JoinWorldFiles<object>();
        files.Keep(path, owner);
        var expected = Report(true);

        var result = await JoinSaveList.WaitAsync((op, _) =>
        {
            requests.Add((op, playline, name, path));
            Assert.True(files.Protects(path));
            Assert.False(files.MayRemove(path, owner));
            Assert.Equal(digest, SHA256.HashData(File.ReadAllBytes(path)));
            return Task.FromResult<CombatPipe.SaveListReport?>(requests.Count switch
            {
                1 => null,
                2 => Report(false),
                _ => expected,
            });
        }, () => true, delay: NoDelay);

        Assert.Same(expected, result);
        Assert.Equal(new byte[] { 1, 3, 3 }, requests.Select(r => r.Op).ToArray());
        Assert.All(requests, r => Assert.Equal((playline, name, path), (r.Playline, r.Name, r.Path)));
        Assert.Equal(digest, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.True(files.Protects(path)); // Listed is not yet join=in.
    }

    [Fact]
    public async Task Superseded_join_ignores_a_successful_reply_that_was_already_in_flight()
    {
        bool active = true;
        int calls = 0;
        var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<CombatPipe.SaveListReport?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = JoinSaveList.WaitAsync((_, _) =>
        {
            calls++;
            waiting.TrySetResult(true);
            return reply.Task;
        }, () => active, delay: NoDelay);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        active = false;
        reply.SetResult(Report(true));

        Assert.Null(await task);
        Assert.Equal(1, calls); // No result authorizes a load after this join was cancelled or replaced.
    }

    [Fact]
    public async Task Inactive_join_does_not_ask_the_game()
    {
        int calls = 0;
        var result = await JoinSaveList.WaitAsync((_, _) =>
        {
            calls++;
            return Task.FromResult<CombatPipe.SaveListReport?>(Report(true));
        }, () => false, delay: NoDelay);
        Assert.Null(result);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_catalogue_that_never_lists_the_world_has_a_bounded_number_of_attempts(bool readable)
    {
        var ops = new List<byte>();
        int delays = 0;
        var result = await JoinSaveList.WaitAsync((op, _) =>
        {
            ops.Add(op);
            return Task.FromResult<CombatPipe.SaveListReport?>(readable ? Report(false) : null);
        }, () => true, delay: (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            delays++;
            return Task.CompletedTask;
        });
        Assert.Null(result);
        Assert.InRange(ops.Count, 2, JoinSaveList.MaxAttempts);
        Assert.Equal(ops.Count - 1, delays);
        Assert.Equal((byte)1, ops[0]);
        if (!readable) Assert.All(ops.Skip(1), op => Assert.Equal((byte)3, op));
    }

    [Fact]
    public async Task Already_cancelled_token_never_queries_the_game()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JoinSaveList.WaitAsync((_, _) =>
        {
            calls++;
            return Task.FromResult<CombatPipe.SaveListReport?>(Report(true));
        }, () => true, cancellation.Token, NoDelay));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Cancellation_during_a_query_cannot_return_a_late_success()
    {
        using var cancellation = new CancellationTokenSource();
        var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<CombatPipe.SaveListReport?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = JoinSaveList.WaitAsync((_, ct) =>
        {
            Assert.Equal(cancellation.Token, ct);
            waiting.TrySetResult(true);
            return reply.Task;
        }, () => true, cancellation.Token, NoDelay);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        reply.SetResult(Report(true));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await task; });
    }

    [Fact]
    public async Task Cancellation_of_the_retry_delay_stops_further_queries()
    {
        using var cancellation = new CancellationTokenSource();
        int queries = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JoinSaveList.WaitAsync((_, _) =>
        {
            queries++;
            return Task.FromResult<CombatPipe.SaveListReport?>(null);
        }, () => true, cancellation.Token, (_, ct) =>
        {
            Assert.Equal(cancellation.Token, ct);
            cancellation.Cancel();
            return Task.FromCanceled(ct);
        }));
        Assert.Equal(1, queries);
    }

    [Fact]
    public void Failed_attempt_keeps_the_last_good_world_and_does_not_authorize_cleanup()
    {
        var files = new JoinWorldFiles<object>();
        var owner = new object();
        string path = World();
        files.Keep(path, owner);
        Assert.Empty(files.End(joined: false, cancelled: false));
        Assert.True(files.Protects(path));
        Assert.False(files.MayRemove(path, owner));
        Assert.False(files.TryDelete(path, owner));
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Only_join_in_or_player_cancel_releases_all_retained_worlds(bool joined, bool cancelled)
    {
        var files = new JoinWorldFiles<object>();
        var first = new object();
        var second = new object();
        string a = World();
        string b = World("mpworld35905a71.whs");
        files.Keep(a, first);
        files.Keep(b, second);

        var released = files.End(joined, cancelled);
        Assert.Equal(2, released.Length);
        Assert.Contains(first, released);
        Assert.Contains(second, released);
        Assert.True(files.MayRemove(a, first));
        Assert.True(files.MayRemove(b, second));
        Assert.True(files.Protects(a)); // Sweep protection lasts until actual deletion.
        Assert.True(files.Protects(b));
        Assert.True(files.TryDelete(a, first));
        Assert.False(File.Exists(a));
        Assert.False(files.Protects(a));
        Assert.True(files.TryDelete(b, second));
        Assert.False(File.Exists(b));
        Assert.False(files.Protects(b));
    }

    [Fact]
    public void Renewing_a_path_prevents_an_old_cleanup_from_deleting_the_new_lease()
    {
        var files = new JoinWorldFiles<object>();
        string path = World();
        var oldJoin = new object();
        var newJoin = new object();
        files.Keep(path, oldJoin);
        files.End(joined: false, cancelled: true);
        Assert.True(files.MayRemove(path, oldJoin));
        files.Keep(path, newJoin);
        Assert.False(files.MayRemove(path, oldJoin));
        Assert.False(files.TryDelete(path, oldJoin));
        Assert.False(files.MayRemove(path, newJoin));
        Assert.True(files.Protects(path));
        Assert.True(File.Exists(path));
        files.End(joined: true, cancelled: false);
        Assert.False(files.TryDelete(path, oldJoin));
        Assert.True(files.TryDelete(path, newJoin));
    }

    [Fact]
    public void Failed_file_deletion_preserves_its_lease_and_sweep_protection()
    {
        var files = new JoinWorldFiles<object>();
        var owner = new object();
        // File.Delete on a directory fails on Windows and Unix without depending on ACLs or runner privileges.
        string path = Path.Combine(_dir, "mpworld35905a71.whs");
        Directory.CreateDirectory(path);
        files.Keep(path, owner);
        files.End(joined: true, cancelled: false);
        var error = Record.Exception(() => files.TryDelete(path, owner));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.True(files.Protects(path));
        Assert.True(files.MayRemove(path, owner));
        Assert.True(Directory.Exists(path));
        Directory.Delete(path);
        Assert.True(files.TryDelete(path, owner));
        Assert.False(files.Protects(path));
    }
}
