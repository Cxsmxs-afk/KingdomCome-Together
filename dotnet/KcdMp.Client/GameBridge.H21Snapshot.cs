// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Text;
using System.Threading.Channels;
using KcdMp.Wire;

namespace KcdMp.Client;

public partial class GameBridge
{
    private sealed record SnapshotFile(string Path, byte[]? ConfirmedBytes = null);

    // The write-completion log identifies the path. The engine's saved
    // description identifies the request. Both are required for this scope.
    private sealed class H21SnapshotReceipt : IDisposable
    {
        private readonly LogTailGameTransport _engine;
        private readonly long _incarnation;
        private readonly CancellationTokenSource _done = new();
        private readonly Channel<string> _writes = Channel.CreateBounded<string>(16);

        public Guid Id { get; } = Guid.NewGuid();

        public H21SnapshotReceipt(LogTailGameTransport engine)
        {
            _engine = engine;
            _incarnation = engine.EngineLogIncarnation;
            _engine.EngineSaveWritten += OnWrite;
        }

        private void OnWrite(LogTailGameTransport.EngineSaveWrite receipt)
        {
            if (receipt.Incarnation == _incarnation) _writes.Writer.TryWrite(receipt.Path);
        }

        public Task<SnapshotFile> WaitAsync(string saves, int playline, Func<bool> current)
        {
            string marker = "kct_h21_" + Convert.ToHexString(Id.ToByteArray()).ToLowerInvariant();
            var ct = _done.Token;
            return Task.Run(async () =>
            {
                bool refused = false;
                await foreach (string reported in _writes.Reader.ReadAllAsync(ct))
                {
                    if (_engine.EngineLogIncarnation != _incarnation || !current()) break;
                    string? path = H21EngineSavePath(saves, reported);
                    if (path is not null && WorldSaved.ParsePath(path) is { Kind: Protocol.SaveKindQuick } save &&
                        save.Playline == playline && TryRead(path, marker, save.Idx) is { } bytes &&
                        _engine.EngineLogIncarnation == _incarnation && current())
                        return new SnapshotFile(path, bytes);
                    if (!refused)
                    {
                        refused = true;
                        Console.WriteLine("MP-H21-SNAPSHOT ignored reason=write-does-not-prove-current-attempt");
                    }
                }
                throw new OperationCanceledException(ct);
            }, ct);
        }

        private static byte[]? TryRead(string path, string marker, int saveId)
        {
            try
            {
                if (!LocalStartCheckpointStore.SafeDirectory(Path.GetDirectoryName(path)!) || !LocalStartCheckpointStore.SafeFile(path)) return null;
                using var save = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (save.Length is <= 72 or > Protocol.WorldMaxBytes) return null;
                // A bounded header read rejects other saves before reading their bodies.
                Span<byte> header = stackalloc byte[8];
                save.ReadExactly(header);
                int length = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
                if (BinaryPrimitives.ReadUInt32LittleEndian(header) != uint.MaxValue ||
                    length is <= 0 or > (1 << 20) || 72L + length >= save.Length) return null;
                var xml = new byte[length];
                save.ReadExactly(xml);
                if (SaveGameReader.ParseDescription(Encoding.UTF8.GetString(xml)) is not { } description ||
                    description.SaveId != saveId || description.QuestNameOverride != marker) return null;
                var bytes = new byte[checked((int)save.Length)];
                header.CopyTo(bytes);
                xml.CopyTo(bytes, header.Length);
                save.ReadExactly(bytes.AsSpan(header.Length + length));
                return bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }

        public void Dispose()
        {
            _engine.EngineSaveWritten -= OnWrite;
            _done.Cancel();
            _done.Dispose();
        }
    }
}
