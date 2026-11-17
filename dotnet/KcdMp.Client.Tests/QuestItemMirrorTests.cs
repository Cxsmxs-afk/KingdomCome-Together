// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Reflection;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

public class QuestItemMirrorTests
{
    private const string Item = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task Pickup_and_hand_in_are_both_accepted_frames_and_failed_sends_are_not_counted()
    {
        var frames = new List<byte[]>();
        Task<bool> Send(byte peer, string text)
        {
            if (peer == 3) return Task.FromResult(false);
            frames.Add(new LootMsg(Protocol.QuestHostItemCount, 0, text).BuildUp(Protocol.QuestHostUp, peer));
            return Task.FromResult(true);
        }
        int sent = await QuestItemMirror.ForwardAsync(Item + ":1", [2, 3], Send);
        sent += await QuestItemMirror.ForwardAsync(Item + ":0 malformed 00000000-0000-0000-0000-000000000000:1", [2, 3], Send);
        Assert.Equal(2, sent);
        Assert.Equal(2, frames.Count);
        var counts = new List<int>();
        foreach (var frame in frames)
        {
            Assert.Equal(Protocol.QuestHostUp, frame[0]);
            Assert.True(LootMsg.TryDecode(frame.AsSpan(3 + Protocol.JoinHeaderLen), out var message));
            Assert.Equal(Protocol.QuestHostItemCount, message.Kind);
            Assert.True(Wo200Rules.TryParseItemCountText(message.Text, out int count, out _));
            counts.Add(count);
        }
        Assert.Equal(new[] { 1, 0 }, counts);
    }

    [Fact]
    public async Task Missing_classes_and_partial_publication_are_retryable()
    {
        var classes = Enumerable.Range(1, 40).Select(n => new Guid(n, 0, 0, new byte[8]).ToString("D")).ToList();
        var calls = new List<string>();
        Assert.False(await QuestItemMirror.PublishClassesAsync(() => Task.FromResult<IReadOnlyList<string>?>(null), lua =>
        {
            calls.Add(lua); return Task.CompletedTask;
        }, () => true));
        Assert.Empty(calls);
        int adds = 0;
        await Assert.ThrowsAsync<IOException>(() => QuestItemMirror.PublishClassesAsync(() => Task.FromResult<IReadOnlyList<string>?>(classes), lua =>
        {
            calls.Add(lua);
            if (lua.Contains("QClassAdd") && ++adds == 2) throw new IOException("temporary transport failure");
            return Task.CompletedTask;
        }, () => true));
        calls.Clear();
        Assert.True(await QuestItemMirror.PublishClassesAsync(() => Task.FromResult<IReadOnlyList<string>?>(classes), lua =>
        {
            calls.Add(lua); return Task.CompletedTask;
        }, () => true));
        Assert.Contains("QClassReset", calls[0]);
        Assert.Equal(3, calls.Count);
        Assert.All(classes, cls => Assert.Contains(calls, c => c.Contains(cls)));
        Assert.All(calls, lua => Assert.True(Uri.EscapeDataString("#" + lua).Length <= QuestItemMirror.MaxClassCallEncoded));
    }

    [Fact]
    public async Task Publication_awaits_each_immediate_chunk_before_starting_the_next()
    {
        var classes = Enumerable.Range(1, 40).Select(n => new Guid(n, 0, 0, new byte[8]).ToString("D")).ToList();
        var firstAdd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        Task publication = QuestItemMirror.PublishClassesAsync(() => Task.FromResult<IReadOnlyList<string>?>(classes), lua =>
        {
            calls.Add(lua);
            return calls.Count == 2 ? firstAdd.Task : Task.CompletedTask;
        }, () => true);
        Assert.False(publication.IsCompleted);
        Assert.Equal(2, calls.Count); // Reset then first chunk; the final chunk has not started.
        firstAdd.SetResult();
        await publication;
        Assert.Equal(3, calls.Count);
    }

    public class CaptureTransport : DispatchProxy
    {
        public readonly List<string> Lua = new();
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "ExecuteAsync") { Lua.Add((string)args![0]!); return Task.CompletedTask; }
            throw new InvalidOperationException("Unexpected transport call: " + method?.Name);
        }
    }

    [Fact]
    public void Actual_joiner_receiver_counts_pickup_and_handoff_and_refuses_malformed_counts()
    {
        var bridge = new GameBridge(new ClientConfig());
        var transport = DispatchProxy.Create<IGameTransport, CaptureTransport>();
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        void Set(string field, object value) => typeof(GameBridge).GetField(field, flags)!.SetValue(bridge, value);
        Set("_transport", transport); Set("_combatRoleApplied", true); Set("_isDamageAuthority", false);
        Set("_sharedWorld", true); Set("_hostAuthority", true); Set("_joinedWorld", true);
        var receive = typeof(GameBridge).GetMethod("Wo200OnHostItemCount", flags)!;
        foreach (string text in new[] { "1 " + Item, "0 " + Item, "-1 " + Item, "10000 " + Item }) receive.Invoke(bridge, [text]);
        Assert.Equal(2L, typeof(GameBridge).GetField("_w200QIn", flags)!.GetValue(bridge));
        var calls = ((CaptureTransport)(object)transport).Lua;
        Assert.Equal(2, calls.Count);
        Assert.Contains(", 1,", calls[0]);
        Assert.Contains(", 0,", calls[1]);
    }
}
