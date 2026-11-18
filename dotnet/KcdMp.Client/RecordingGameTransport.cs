// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;

namespace KcdMp.Client;

/// <summary>Observes opted-in local game calls and their completion order. Console text is opt-in per statement.</summary>
public sealed class RecordingGameTransport(IGameTransport inner, SessionRecorder recorder, Func<string, bool>? recordLua = null) : IGameTransport
{
    public string Name => inner.Name;
    public int RoundTripsPerStateRead => inner.RoundTripsPerStateRead;

    private int Begin(string operation, object?[] args)
    {
        int id = recorder.NextGameCallId();
        return id != 0 && Capture(SessionRecordKind.GameCall, () => GameTranscript.Call(id, operation, args)) ? id : 0;
    }

    private bool Capture(SessionRecordKind kind, Func<byte[]> encode)
    {
        try
        {
            byte[] payload = encode();
            return kind == SessionRecordKind.GameResult ? recorder.TryAppendGameResult(payload) : recorder.TryAppend(kind, payload);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException)
        {
            recorder.FailCapture();
            return false;
        }
    }

    private void End<T>(int id, RecordedGameOutcome outcome, T value)
    {
        if (id != 0 && !recorder.Failed)
            Capture(SessionRecordKind.GameResult, () => GameTranscript.Result(id, outcome, value));
    }

    private async Task<T> ObserveAsync<T>(string operation, Func<Task<T>> call, params object?[] args)
    {
        int id = Begin(operation, args);
        try
        {
            T value = await call();
            End(id, RecordedGameOutcome.Returned, value);
            return value;
        }
        catch (OperationCanceledException)
        {
            End<object?>(id, RecordedGameOutcome.Cancelled, null);
            throw;
        }
        catch
        {
            End<object?>(id, RecordedGameOutcome.Failed, null);
            throw;
        }
    }

    private Task ObserveEffectAsync(string operation, Func<Task> call, params object?[] args) =>
        ObserveAsync<object?>(operation, async () => { await call(); return null; }, args);

    public Task ObserveExecuteAsync(string lua, Func<Task> execute) =>
        ObserveLuaAsync(nameof(ExecuteNowAsync), lua, execute);

    private Task ObserveLuaAsync(string operation, string lua, Func<Task> call) =>
        recordLua?.Invoke(lua) == true ? ObserveEffectAsync(operation, call, lua) : call();

    public Task<bool> IsGameReadyAsync(CancellationToken ct = default) => ObserveAsync(nameof(IsGameReadyAsync), () => inner.IsGameReadyAsync(ct));
    public Task<PlayerState?> ReadPlayerStateAsync(CancellationToken ct = default) =>
        ObserveAsync(nameof(ReadPlayerStateAsync), () => inner.ReadPlayerStateAsync(ct));
    public Task ExecuteAsync(string lua, CancellationToken ct = default) => ObserveLuaAsync(nameof(ExecuteAsync), lua, () => inner.ExecuteAsync(lua, ct));
    public Task FlushAsync(CancellationToken ct = default) => ObserveEffectAsync(nameof(FlushAsync), () => inner.FlushAsync(ct));
    public void RequestFlush()
    {
        int id = Begin(nameof(RequestFlush), []);
        try
        {
            inner.RequestFlush();
            End<object?>(id, RecordedGameOutcome.Returned, null);
        }
        catch (OperationCanceledException)
        {
            End<object?>(id, RecordedGameOutcome.Cancelled, null);
            throw;
        }
        catch
        {
            End<object?>(id, RecordedGameOutcome.Failed, null);
            throw;
        }
    }
    public Task<Guid[]?> ReadEquippedItemClassesAsync(CancellationToken ct = default) =>
        ObserveAsync(nameof(ReadEquippedItemClassesAsync), () => inner.ReadEquippedItemClassesAsync(ct));
    public Task<Guid[]?> ReadGhostEquippedItemClassesAsync(string ghostSoulName, CancellationToken ct = default) =>
        ObserveAsync(nameof(ReadGhostEquippedItemClassesAsync), () => inner.ReadGhostEquippedItemClassesAsync(ghostSoulName, ct), ghostSoulName);
    public Task EquipItemOnGhostAsync(string ghostSoulName, Guid itemClass, bool createIfMissing, CancellationToken ct = default) =>
        ObserveEffectAsync(nameof(EquipItemOnGhostAsync), () => inner.EquipItemOnGhostAsync(ghostSoulName, itemClass, createIfMissing, ct),
            ghostSoulName, itemClass, createIfMissing);
    public Task UnequipItemOnGhostAsync(string ghostSoulName, Guid itemClass, CancellationToken ct = default) =>
        ObserveEffectAsync(nameof(UnequipItemOnGhostAsync), () => inner.UnequipItemOnGhostAsync(ghostSoulName, itemClass, ct), ghostSoulName, itemClass);
    public Task<Guid?> ReadGhostSoulGuidAsync(string ghostSoulName, CancellationToken ct = default) =>
        ObserveAsync(nameof(ReadGhostSoulGuidAsync), () => inner.ReadGhostSoulGuidAsync(ghostSoulName, ct), ghostSoulName);
    public Task<string?> ReadSoulNameByGuidAsync(Guid soulGuid, CancellationToken ct = default) =>
        ObserveAsync(nameof(ReadSoulNameByGuidAsync), () => inner.ReadSoulNameByGuidAsync(soulGuid, ct), soulGuid);
    public Task<(Guid? Guid, string? Name)> ReadPlayerSoulIdentityAsync(CancellationToken ct = default) =>
        ObserveAsync(nameof(ReadPlayerSoulIdentityAsync), () => inner.ReadPlayerSoulIdentityAsync(ct));
    public Task ExecuteNowAsync(string lua, CancellationToken ct = default) =>
        ObserveLuaAsync(nameof(ExecuteNowAsync), lua, () => inner.ExecuteNowAsync(lua, ct));
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
