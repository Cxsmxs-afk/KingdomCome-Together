// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Reflection;
using KcdMp.Wire;

namespace KcdMp.Client.Tests;

[Collection("Join calendar integration")]
public sealed class JoinLateEngineReceiptTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task Late_engine_completion_releases_only_the_retired_join_file()
    {
        await using var fixture = new Fixture();
        await fixture.ConnectNativeAsync();
        var retired = fixture.Submit(joinId: 1, playline: 1);
        fixture.FinishDispatch(retired);
        await fixture.AbortAsync(retired);
        Assert.True(File.Exists(retired.Path));

        var replacement = fixture.Submit(joinId: 2, playline: 2);
        fixture.FinishDispatch(replacement);
        object placeBefore = fixture.Place;
        fixture.ReadAndComplete(retired.Path);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (File.Exists(retired.Path) && !deadline.IsCancellationRequested) await Task.Delay(10);
        Assert.False(File.Exists(retired.Path), "The exactly consumed retired file still occupies its playline.");
        Assert.Same(replacement.Join, fixture.ActiveJoin);
        Assert.Equal(placeBefore, fixture.Place);
        Assert.False(fixture.HasGameplayReceipt(replacement));
        Assert.Equal(Fixture.SaveBytes, File.ReadAllBytes(replacement.Path));
        Assert.Equal(Fixture.SaveBytes, File.ReadAllBytes(fixture.PersonalSave));
    }

    [Fact]
    public async Task Unrelated_or_new_process_receipts_do_not_release_a_submitted_join_file()
    {
        await using var fixture = new Fixture();
        await fixture.ConnectNativeAsync();
        var retired = fixture.Submit(joinId: 1, playline: 1);
        fixture.FinishDispatch(retired);
        await fixture.AbortAsync(retired);
        var replacement = fixture.Submit(joinId: 2, playline: 2);
        fixture.FinishDispatch(replacement);

        fixture.ReadAndComplete(Path.Combine(fixture.OtherProfile, "playline1", Path.GetFileName(retired.Path)));
        Assert.False(fixture.HasGameplayReceipt(replacement));
        Assert.False(fixture.HasEngineTerminal(retired));
        fixture.Game.Feed("CSystem::Quit invoked");
        fixture.Game.Feed("Log Started at 2026-11-19  23:45:01");
        fixture.ReadAndComplete(retired.Path);
        Assert.False(fixture.HasGameplayReceipt(replacement));
        Assert.False(fixture.HasEngineTerminal(retired));
        Assert.Equal(Fixture.SaveBytes, File.ReadAllBytes(retired.Path));
        Assert.Equal(Fixture.SaveBytes, File.ReadAllBytes(replacement.Path));
        Assert.Equal(Fixture.SaveBytes, File.ReadAllBytes(fixture.PersonalSave));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("kcdmp-late-join-receipt-").FullName;
        private readonly string? _previousData = Environment.GetEnvironmentVariable("KCDMP_DATA_DIR");
        private readonly GameBridge _bridge = new(new ClientConfig());
        private readonly JoinCalendarTestPipe _pipe = new();
        private string Saves => Path.Combine(_root, "saves");
        public string OtherProfile => Path.Combine(_root, "other-profile");
        public string PersonalSave => Path.Combine(Saves, "playline0", "save001.whs");
        public JoinCalendarTestEngine Game { get; }
        public object? ActiveJoin => Field(_bridge, "_jj", Private).GetValue(_bridge);
        public object Place => Field(_bridge, "_where", Private).GetValue(_bridge)!;
        public static readonly byte[] SaveBytes = "synthetic verified save bytes"u8.ToArray();

        public Fixture()
        {
            Environment.SetEnvironmentVariable("KCDMP_DATA_DIR", _root);
            Directory.CreateDirectory(Path.GetDirectoryName(PersonalSave)!);
            File.WriteAllBytes(PersonalSave, SaveBytes);
            Game = new JoinCalendarTestEngine(Path.Combine(_root, "game.log"), Saves, null, _pipe.ObserveGameplayStarted);
            Field(_bridge, "_combat", Private).SetValue(_bridge, _pipe.Pipe);
            Field(_bridge, "_transport", Private).SetValue(_bridge, Game.Transport);
            Field(_bridge, "_savesDir", Private).SetValue(_bridge, Saves);
            Game.Transport.SaveLoadAccepted += Handler<Action<string>>("Wo124OnSaveLoadAccepted");
            Game.Transport.LoadStarted += Handler<Action>("Wo124OnLoadStarted");
            Game.Transport.GameplayStarted += Handler<Action>("Wo124OnGameplayStarted");
        }

        public async Task ConnectNativeAsync() => await _pipe.Pipe.CalendarFollowAsync(0);

        public (object Join, string Path) Submit(uint joinId, int playline)
        {
            string slot = Path.Combine(Saves, $"playline{playline}");
            Directory.CreateDirectory(slot);
            string name = $"mpworld{joinId:x8}";
            string placed = Path.Combine(slot, name + ".whs");
            string tag = WhsSave.SeedTag(0x12345678 + joinId);
            FieldInfo active = Field(_bridge, "_jj", Private);
            object join = Activator.CreateInstance(active.FieldType, nonPublic: true)!;
            Field(join, "JoinId").SetValue(join, joinId);
            Field(join, "Playline").SetValue(join, playline);
            Field(join, "Name").SetValue(join, name);
            Field(join, "WorldTag").SetValue(join, tag);
            active.SetValue(_bridge, join);
            Assert.True(Call("TryPublishJoinWorld", join, Saves, placed, SaveBytes, tag) is true);
            Field(join, "PlacedVerified").SetValue(join, true);
            Assert.True(Call("TryBeginJoinLoad", join, Saves) is true);
            return (join, placed);
        }

        public void FinishDispatch((object Join, string Path) attempt) =>
            Field(attempt.Join, "LoadDispatchInFlight").SetValue(attempt.Join, false);

        public bool HasGameplayReceipt((object Join, string Path) attempt) =>
            ((TaskCompletionSource<bool>)Field(attempt.Join, "GameplayStarted").GetValue(attempt.Join)!).Task.IsCompleted;

        public bool HasEngineTerminal((object Join, string Path) attempt) =>
            attempt.Join.GetType().GetField("EngineReceipt")?.GetValue(attempt.Join) is { } receipt &&
            ((TaskCompletionSource<bool>)Field(receipt, "Terminal").GetValue(receipt)!).Task.IsCompleted;

        public Task AbortAsync((object Join, string Path) attempt) =>
            (Task)Call("AbortJoinerJoinAsync", attempt.Join, Protocol.JoinAbortLoadFailed, "fixture-abort", "Test join retired.", false)!;

        public void ReadAndComplete(string path)
        {
            Game.Feed($"Loading saved game '{path}'");
            Game.Feed($"[CryAction] LoadGame: '{path}'");
            Game.Feed("Gameplay started");
        }

        private T Handler<T>(string name) where T : Delegate => _bridge.GetType().GetMethod(name, Private)!.CreateDelegate<T>(_bridge);
        private object? Call(string name, params object?[] args) => _bridge.GetType().GetMethod(name, Private)!.Invoke(_bridge, args);
        private static FieldInfo Field(object target, string name, BindingFlags flags = BindingFlags.Instance | BindingFlags.Public) =>
            target.GetType().GetField(name, flags)!;

        public async ValueTask DisposeAsync()
        {
            await Game.DisposeAsync();
            await _pipe.DisposeAsync();
            Environment.SetEnvironmentVariable("KCDMP_DATA_DIR", _previousData);
            Directory.Delete(_root, recursive: true);
        }
    }
}
