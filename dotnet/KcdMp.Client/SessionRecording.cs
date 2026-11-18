// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Diagnostics;

namespace KcdMp.Client;

public enum SessionRecordKind : byte
{
    GameCall = 1,
    GameResult = 2,
    RelaySent = 3,
    RelayReceived = 4,
    GameEvent = 5
}

public sealed record SessionRecord(long OffsetMs, SessionRecordKind Kind, byte[] Payload);

/// <summary>One chronological, versioned transport recording. A closing count distinguishes a complete file from a prefix.</summary>
public static class SessionRecording
{
    private static ReadOnlySpan<byte> Header => "KCDREC\r\n\x01\x00"u8;
    private const int EntryHeaderBytes = 13, FooterBytes = EntryHeaderBytes + 4;
    private const int MaxBytes = 64 * 1024 * 1024, MaxPayloadBytes = 128 * 1024;
    internal const int MaxEntries = 262144;
    private const long MaxMilliseconds = 7L * 24 * 60 * 60 * 1000;

    public static IReadOnlyList<SessionRecord> Read(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Span<byte> fileHeader = stackalloc byte[Header.Length];
        ReadExact(input, fileHeader);
        if (!fileHeader.SequenceEqual(Header)) throw new InvalidDataException("Unknown recording format or version.");

        var records = new List<SessionRecord>();
        Span<byte> recordHeader = stackalloc byte[EntryHeaderBytes];
        Span<byte> footer = stackalloc byte[4];
        int bytes = Header.Length;
        long last = 0;
        while (true)
        {
            ReadExact(input, recordHeader);
            var kind = (SessionRecordKind)recordHeader[0];
            long elapsed = BinaryPrimitives.ReadInt64LittleEndian(recordHeader[1..]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(recordHeader[9..]);
            if (kind == 0)
            {
                if (size != 4 || elapsed != last) throw new InvalidDataException("Invalid recording footer.");
                ReadExact(input, footer);
                if (BinaryPrimitives.ReadUInt32LittleEndian(footer) != records.Count || input.ReadByte() != -1)
                    throw new InvalidDataException("Invalid recording count or trailing data.");
                return records.AsReadOnly();
            }
            if (!Known(kind) || elapsed < last || elapsed > MaxMilliseconds || size is 0 or > MaxPayloadBytes ||
                records.Count >= MaxEntries || bytes + EntryHeaderBytes + size + FooterBytes > MaxBytes)
                throw new InvalidDataException("Invalid recording entry or size limit exceeded.");
            var payload = new byte[(int)size];
            ReadExact(input, payload);
            if (!ValidPayload(kind, payload)) throw new InvalidDataException("Invalid relay frame in recording.");
            records.Add(new(elapsed, kind, payload));
            bytes += EntryHeaderBytes + payload.Length;
            last = elapsed;
        }
    }

    public static void Write(Stream output, IEnumerable<SessionRecord> records)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(records);
        var writer = new Writer(output);
        foreach (var record in records)
        {
            if (!writer.TryAppend(record.OffsetMs, record.Kind, record.Payload))
                throw new InvalidDataException("Invalid recording entry or size limit exceeded.");
        }
        writer.Finish();
    }

    private static bool Known(SessionRecordKind kind) => kind is
        SessionRecordKind.GameCall or SessionRecordKind.GameResult or SessionRecordKind.RelaySent or
        SessionRecordKind.RelayReceived or SessionRecordKind.GameEvent;

    private static bool ValidPayload(SessionRecordKind kind, ReadOnlySpan<byte> payload) =>
        kind is not (SessionRecordKind.RelaySent or SessionRecordKind.RelayReceived) ||
        (payload.Length >= 3 && BinaryPrimitives.ReadUInt16LittleEndian(payload[1..]) == payload.Length - 3);

    private static void ReadExact(Stream input, Span<byte> bytes)
    {
        try { input.ReadExactly(bytes); }
        catch (EndOfStreamException error) { throw new InvalidDataException("Recording is incomplete.", error); }
    }

    internal sealed class Writer
    {
        private readonly Stream _output;
        private int _bytes = Header.Length, _count;
        private long _last;

        public Writer(Stream output)
        {
            output.Write(Header);
            _output = output;
        }

        public bool TryAppend(long offsetMs, SessionRecordKind kind, ReadOnlySpan<byte> payload)
        {
            if (!Known(kind) || offsetMs < _last || offsetMs > MaxMilliseconds || payload.Length is 0 or > MaxPayloadBytes ||
                _count >= MaxEntries || _bytes + EntryHeaderBytes + payload.Length + FooterBytes > MaxBytes || !ValidPayload(kind, payload))
                return false;
            WriteEntry(offsetMs, kind, payload);
            _last = offsetMs;
            _count++;
            _bytes += EntryHeaderBytes + payload.Length;
            return true;
        }

        public void Finish()
        {
            Span<byte> count = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(count, _count);
            WriteEntry(_last, 0, count);
        }

        private void WriteEntry(long offsetMs, SessionRecordKind kind, ReadOnlySpan<byte> payload)
        {
            Span<byte> header = stackalloc byte[EntryHeaderBytes];
            header[0] = (byte)kind;
            BinaryPrimitives.WriteInt64LittleEndian(header[1..], offsetMs);
            BinaryPrimitives.WriteInt32LittleEndian(header[9..], payload.Length);
            _output.Write(header);
            _output.Write(payload);
        }
    }
}

/// <summary>Opt-in append-only recording. An incomplete capture stays unreadable after a size limit or write failure.</summary>
public sealed class SessionRecorder : IDisposable
{
    private readonly object _gate = new();
    private readonly FileStream _stream;
    private readonly SessionRecording.Writer _writer;
    private readonly long _started = Stopwatch.GetTimestamp();
    private bool _failed, _disposed;
    private int _nextGameCall, _openGameCalls;

    private SessionRecorder(FileStream stream)
    {
        _stream = stream;
        _writer = new(stream);
    }

    public bool Failed { get { lock (_gate) return _failed; } }

    public static SessionRecorder Create(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read, BufferSize = 16 * 1024
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var stream = new FileStream(path, options);
        try { return new(stream); }
        catch { stream.Dispose(); throw; }
    }

    public bool TryAppend(SessionRecordKind kind, ReadOnlySpan<byte> payload)
    {
        lock (_gate) return AppendLocked(kind, payload);
    }

    private bool AppendLocked(SessionRecordKind kind, ReadOnlySpan<byte> payload)
    {
        if (_disposed || _failed) return false;
        try
        {
            long offsetMs = Stopwatch.GetElapsedTime(_started).Ticks / TimeSpan.TicksPerMillisecond;
            if (_writer.TryAppend(offsetMs, kind, payload)) return true;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        _failed = true;
        return false;
    }

    internal void FailCapture() { lock (_gate) _failed = true; }

    internal int NextGameCallId()
    {
        lock (_gate)
        {
            if (_disposed || _failed) return 0;
            if (_nextGameCall == SessionRecording.MaxEntries)
            {
                _failed = true;
                return 0;
            }
            _openGameCalls++;
            return ++_nextGameCall;
        }
    }

    internal bool TryAppendGameResult(ReadOnlySpan<byte> payload)
    {
        lock (_gate)
        {
            if (!AppendLocked(SessionRecordKind.GameResult, payload)) return false;
            _openGameCalls--;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _failed |= _openGameCalls != 0;
            try { if (!_failed) _writer.Finish(); }
            catch (IOException) { _failed = true; }
            catch (UnauthorizedAccessException) { _failed = true; }
            finally
            {
                try { _stream.Dispose(); }
                catch (IOException) { _failed = true; }
                catch (UnauthorizedAccessException) { _failed = true; }
            }
        }
    }
}
