// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KcdMp.Client;

public sealed record RecordedGameCall([property: JsonRequired] int Id, [property: JsonRequired] string Operation,
    [property: JsonRequired] JsonElement Arguments);

internal enum RecordedGameOutcome : byte { Returned, Cancelled, Failed }
internal sealed record RecordedGameResult([property: JsonRequired] int Id, [property: JsonRequired] RecordedGameOutcome Outcome,
    [property: JsonRequired] JsonElement Value);

internal static class GameTranscript
{
    // PlayerState uses properties, and the identity read returns a ValueTuple using fields.
    public static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    public static byte[] Call(int id, string operation, object?[] args) =>
        JsonSerializer.SerializeToUtf8Bytes(new { Id = id, Operation = operation, Arguments = args }, Json);

    public static byte[] Result(int id, RecordedGameOutcome outcome, object? value) =>
        JsonSerializer.SerializeToUtf8Bytes(new { Id = id, Outcome = outcome, Value = value }, Json);
}

/// <summary>Plays back calls and returns in transcript order without contacting a game or evaluating any Lua.</summary>
public sealed class ReplayGameTransport : IGameTransport
{
    private readonly record struct Step(RecordedGameCall? Call, RecordedGameResult? Result);
    private readonly object _gate = new();
    private readonly List<Step> _steps = new();
    private readonly List<RecordedGameCall> _observed = new();
    private readonly Dictionary<int, TaskCompletionSource<RecordedGameResult>> _pending = new();
    private int _next;
    private bool _disposed;
    private InvalidDataException? _mismatch;

    public ReplayGameTransport(IEnumerable<SessionRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var calls = new HashSet<int>();
        var pending = new Dictionary<int, Type>();
        try
        {
            foreach (var record in records)
            {
                if (record.Kind == SessionRecordKind.GameCall)
                {
                    var call = JsonSerializer.Deserialize<RecordedGameCall>(record.Payload, GameTranscript.Json);
                    if (call is null || call.Id <= 0 || OperationResult(call.Operation) is not Type resultType ||
                        call.Arguments.ValueKind != JsonValueKind.Array || !calls.Add(call.Id))
                        throw new InvalidDataException("Invalid or duplicate game call in recording.");
                    pending.Add(call.Id, resultType);
                    _steps.Add(new(call, null));
                }
                else if (record.Kind == SessionRecordKind.GameResult)
                {
                    var result = JsonSerializer.Deserialize<RecordedGameResult>(record.Payload, GameTranscript.Json);
                    if (result is null || !Enum.IsDefined(result.Outcome) || !pending.Remove(result.Id, out var resultType))
                        throw new InvalidDataException("Game result has no matching recorded call.");
                    ValidateResult(resultType, result);
                    _steps.Add(new(null, result));
                }
            }
        }
        catch (JsonException error) { throw new InvalidDataException("Malformed game entry in recording.", error); }
        if (pending.Count != 0) throw new InvalidDataException("Game recording ends with an unfinished call.");
    }

    public string Name => "replay";
    public int RoundTripsPerStateRead => 0;
    public IReadOnlyList<RecordedGameCall> ObservedCalls { get { lock (_gate) return _observed.ToArray(); } }

    public static SessionRecord Call(long offsetMs, int id, string operation, params object?[] args) =>
        new(offsetMs, SessionRecordKind.GameCall, GameTranscript.Call(id, operation, args));

    public static SessionRecord Result(long offsetMs, int id, object? value = null) =>
        new(offsetMs, SessionRecordKind.GameResult, GameTranscript.Result(id, RecordedGameOutcome.Returned, value));

    public void VerifyComplete()
    {
        lock (_gate)
        {
            if (_mismatch is not null) throw _mismatch;
            if (_next != _steps.Count || _pending.Count != 0)
                throw new InvalidDataException($"Game replay has {_steps.Count - _next} unused transcript entries.");
        }
    }

    private static Type? OperationResult(string operation) => operation switch
    {
        nameof(IsGameReadyAsync) => typeof(bool),
        nameof(ReadPlayerStateAsync) => typeof(PlayerState?),
        nameof(ReadEquippedItemClassesAsync) or nameof(ReadGhostEquippedItemClassesAsync) => typeof(Guid[]),
        nameof(ReadGhostSoulGuidAsync) => typeof(Guid?),
        nameof(ReadSoulNameByGuidAsync) => typeof(string),
        nameof(ReadPlayerSoulIdentityAsync) => typeof((Guid?, string)),
        nameof(ExecuteAsync) or nameof(ExecuteNowAsync) or nameof(FlushAsync) or nameof(RequestFlush) or
            nameof(EquipItemOnGhostAsync) or nameof(UnequipItemOnGhostAsync) => typeof(void),
        _ => null
    };

    private static void ValidateResult(Type type, RecordedGameResult result)
    {
        if (result.Outcome != RecordedGameOutcome.Returned || type == typeof(void))
        {
            if (result.Value.ValueKind != JsonValueKind.Null) throw new InvalidDataException("Unexpected recorded game result value.");
            return;
        }
        _ = result.Value.Deserialize(type, GameTranscript.Json);
    }

    private Task<RecordedGameResult> Begin(string operation, object?[] args)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_mismatch is not null) throw _mismatch;
            var expected = _next < _steps.Count ? _steps[_next].Call : null;
            if (expected is null || expected.Operation != operation ||
                JsonSerializer.Serialize(expected.Arguments, GameTranscript.Json) != JsonSerializer.Serialize(args, GameTranscript.Json))
            {
                _mismatch = new InvalidDataException($"Game replay call {_next + 1}: expected {expected?.Operation ?? "end of recording"}, got {operation}.");
                foreach (var pending in _pending.Values) pending.TrySetException(_mismatch);
                _pending.Clear();
                throw _mismatch;
            }
            _observed.Add(expected);
            var answer = new TaskCompletionSource<RecordedGameResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add(expected.Id, answer);
            _next++;
            // Some earlier calls can finish only after this call started in the recording.
            while (_next < _steps.Count && _steps[_next].Result is { } result)
            {
                _pending.Remove(result.Id, out var completion);
                completion!.TrySetResult(result);
                _next++;
            }
            return answer.Task;
        }
    }

    private static T Decode<T>(RecordedGameResult result)
    {
        if (result.Outcome == RecordedGameOutcome.Cancelled) throw new OperationCanceledException("Recorded game call was cancelled.");
        if (result.Outcome == RecordedGameOutcome.Failed) throw new IOException("Recorded game call failed.");
        try { return result.Value.Deserialize<T>(GameTranscript.Json)!; }
        catch (JsonException error) { throw new InvalidDataException("Recorded game result has the wrong type.", error); }
    }

    private async Task<T> Invoke<T>(string operation, CancellationToken ct, params object?[] args) =>
        Decode<T>(await Begin(operation, args).WaitAsync(ct));

    public Task<bool> IsGameReadyAsync(CancellationToken ct = default) => Invoke<bool>(nameof(IsGameReadyAsync), ct);
    public Task<PlayerState?> ReadPlayerStateAsync(CancellationToken ct = default) => Invoke<PlayerState?>(nameof(ReadPlayerStateAsync), ct);
    public Task ExecuteAsync(string lua, CancellationToken ct = default) => Invoke<object?>(nameof(ExecuteAsync), ct, lua);
    public Task FlushAsync(CancellationToken ct = default) => Invoke<object?>(nameof(FlushAsync), ct);
    public void RequestFlush()
    {
        var recorded = Begin(nameof(RequestFlush), []);
        if (recorded.IsCompleted) _ = Decode<object?>(recorded.GetAwaiter().GetResult());
    }
    public Task<Guid[]?> ReadEquippedItemClassesAsync(CancellationToken ct = default) => Invoke<Guid[]?>(nameof(ReadEquippedItemClassesAsync), ct);
    public Task<Guid[]?> ReadGhostEquippedItemClassesAsync(string ghostSoulName, CancellationToken ct = default) =>
        Invoke<Guid[]?>(nameof(ReadGhostEquippedItemClassesAsync), ct, ghostSoulName);
    public Task EquipItemOnGhostAsync(string ghostSoulName, Guid itemClass, bool createIfMissing, CancellationToken ct = default) =>
        Invoke<object?>(nameof(EquipItemOnGhostAsync), ct, ghostSoulName, itemClass, createIfMissing);
    public Task UnequipItemOnGhostAsync(string ghostSoulName, Guid itemClass, CancellationToken ct = default) =>
        Invoke<object?>(nameof(UnequipItemOnGhostAsync), ct, ghostSoulName, itemClass);
    public Task<Guid?> ReadGhostSoulGuidAsync(string ghostSoulName, CancellationToken ct = default) =>
        Invoke<Guid?>(nameof(ReadGhostSoulGuidAsync), ct, ghostSoulName);
    public Task<string?> ReadSoulNameByGuidAsync(Guid soulGuid, CancellationToken ct = default) =>
        Invoke<string?>(nameof(ReadSoulNameByGuidAsync), ct, soulGuid);
    public Task<(Guid? Guid, string? Name)> ReadPlayerSoulIdentityAsync(CancellationToken ct = default) =>
        Invoke<(Guid?, string?)>(nameof(ReadPlayerSoulIdentityAsync), ct);
    public Task ExecuteNowAsync(string lua, CancellationToken ct = default) => Invoke<object?>(nameof(ExecuteNowAsync), ct, lua);

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            foreach (var pending in _pending.Values) pending.TrySetCanceled();
            _pending.Clear();
        }
        return ValueTask.CompletedTask;
    }
}
