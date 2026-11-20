// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using KcdMp.Client;

if (!OperatingSystem.IsWindows() || args.Length != 4)
    throw new InvalidOperationException("Expected Windows and four paths: payload, linker map, native tests, temporary run.");

string payload = Path.GetFullPath(args[0]);
string run = Path.GetFullPath(args[3]);
string instance = "edge-" + Guid.NewGuid().ToString("N")[..16];
Environment.SetEnvironmentVariable("KCDMP_INSTANCE", instance);
Environment.CurrentDirectory = run;
string agentPath = typeof(CombatPipe).Assembly.Location;
string build = typeof(CombatPipe).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
    .Single(attribute => attribute.Key == "KcdMpBuildId").Value!;
Require(Digest(agentPath) == Digest(Path.Combine(payload, "KcdMpClient.dll")), "The agent assembly differs from the payload.");

// Use a byte-identical copy so the DLL's runtime log cannot enter the payload.
string dll = Path.Combine(run, "KCDMP.dll");
File.Copy(Path.Combine(payload, "KCDMP.dll"), dll);
_ = NativeLibrary.Load(Path.Combine(Path.GetFullPath(args[2]), "Framework.dll"));
var importer = NativeLibrary.Load(Path.Combine(Path.GetFullPath(args[2]), "WHGame.dll"));
var pulse = Marshal.GetDelegateForFunctionPointer<Pulse>(NativeLibrary.GetExport(importer, "PulseKctSmokeFrame"));
var module = NativeLibrary.Load(dll);
var symbols = new NativeSymbols(File.ReadAllText(args[1]), module);
var nativeBuild = symbols.Find<BuildId>("?build_id@wo211@kcdmp@@YAPEBDXZ");
Require(Marshal.PtrToStringAnsi(nativeBuild()) == build, "DLL and agent Build-IDs differ.");
Require(symbols.Find<NativeStart>("?install@main_thread@kcdmp@@YA_NXZ")(), "The native frame hook refused the fixture importer.");
Require(symbols.Find<NativeStart>("?start@pipe@kcdmp@@YA_NXZ")(), "The native listener failed to start.");

// CrySystem stays absent. The SDK frame fixture drives our frame hook once the
// real listener has admitted the request and published its waiting receipt.
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
var ct = stop.Token;
using var dllPipe = new NamedPipeClientStream(".", "kcdmp-" + instance, PipeDirection.InOut, PipeOptions.Asynchronous);
await dllPipe.ConnectAsync(ct);
string tapName = instance + "-tap";
using var tap = new NamedPipeServerStream(tapName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
await using var agent = new CombatPipe(tapName) { BatchOn = false };
var accepted = tap.WaitForConnectionAsync(ct);
Require(await agent.EnsureConnectedAsync(ct), "The agent did not reach the pipe tap.");
await accepted;
var replies = new ReplyTap();
Task forward = tap.CopyToAsync(dllPipe, ct);
Task back = replies.CopyAsync(dllPipe, tap, ct);
try
{
    Require(await agent.ProbeEdgeAsync(ct), "The shipped DLL did not negotiate Edge.");
    var request = new byte[47];
    request[0] = 2;
    request[1] = 1;
    request[2] = 1;
    BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(3), 222);
    BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(7), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(15), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(19), 1);
    BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(23), 1);
    Guid.Parse("10000000-0000-0000-0000-000000000009").TryWriteBytes(request.AsSpan(31));
    var waiter = agent.EdgeRequestAsync(0x41, request, 0xBB, EdgeLane.Realtime, ct, agent.EdgeConnectionRevision);
    while (agent.EdgeBusyReplies == 0 && !waiter.IsCompleted) await Task.Delay(5, ct);
    Require(agent.EdgeBusyReplies > 0 && !waiter.IsCompleted, "The agent did not wait on an admitted hand request.");

    // All fixture frames come from one thread, like the game's module updates.
    var frameThread = new Thread(() =>
    {
        while (!ct.IsCancellationRequested)
        {
            pulse();
            Thread.Sleep(5);
        }
    }) { IsBackground = true };
    frameThread.Start();
    try
    {
        var reply = await waiter.WaitAsync(ct);
        Require(reply.Outcome == EdgeOutcome.Completed &&
            reply.Payload is [0, 0, 2, 3, 0, ..] && reply.Payload.Length == 37,
            "The waiter did not receive the native version-2 hand refusal for its unbound actor.");
        Require(await agent.PingAsync(ct), "The pipe did not complete a round trip after the hand reply.");
        await Task.Delay(50, ct);
        Require(replies.Terminals == 1 && replies.Unwrapped == 0 && replies.PendingId != 0 &&
            replies.PendingId == replies.TerminalId, "The hand request did not receive exactly one matched Edge reply.");
        Console.WriteLine($"Windows hand reply PASS: build={build} nested=0x41->0xB8/0xBB id={replies.TerminalId} pending=1 terminal=1 unwrapped=0 engine=unbound");
    }
    finally
    {
        stop.Cancel();
        Require(frameThread.Join(TimeSpan.FromSeconds(2)), "The native frame fixture did not stop.");
    }
}
finally
{
    stop.Cancel();
    try { await Task.WhenAll(forward, back); }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
}

static string Digest(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
static void Require(bool condition, string failure)
{
    if (!condition) throw new InvalidOperationException(failure);
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
[return: MarshalAs(UnmanagedType.I1)]
delegate bool NativeStart();
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate void Pulse();
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
delegate IntPtr BuildId();

sealed class NativeSymbols(string map, IntPtr module)
{
    private readonly long _preferredBase = Convert.ToInt64(
        Regex.Match(map, @"Preferred load address is ([0-9a-f]+)", RegexOptions.IgnoreCase).Groups[1].Value, 16);

    public T Find<T>(string symbol) where T : Delegate
    {
        var match = Regex.Match(map, @"^\s+[0-9a-f]+:[0-9a-f]+\s+" + Regex.Escape(symbol) + @"\s+([0-9a-f]+)\s", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (!match.Success) throw new InvalidOperationException($"No linker-map entry for {symbol}.");
        long rva = Convert.ToInt64(match.Groups[1].Value, 16) - _preferredBase;
        if (rva <= 0 || rva > int.MaxValue) throw new InvalidOperationException($"Invalid linker-map entry for {symbol}.");
        return Marshal.GetDelegateForFunctionPointer<T>(IntPtr.Add(module, (int)rva));
    }
}

sealed class ReplyTap
{
    public int Terminals, Unwrapped;
    public uint PendingId, TerminalId;

    public async Task CopyAsync(Stream from, Stream to, CancellationToken ct)
    {
        var header = new byte[3];
        while (!ct.IsCancellationRequested)
        {
            await from.ReadExactlyAsync(header, ct);
            var body = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1))];
            await from.ReadExactlyAsync(body, ct);
            if (header[0] == 0xBB) Interlocked.Increment(ref Unwrapped);
            if (header[0] == 0xB8 && body.Length >= 6)
            {
                if (body[4] == 6) PendingId = BinaryPrimitives.ReadUInt32LittleEndian(body);
                if (body[4] == 0 && body[5] == 0xBB)
                {
                    TerminalId = BinaryPrimitives.ReadUInt32LittleEndian(body);
                    Interlocked.Increment(ref Terminals);
                }
            }
            await to.WriteAsync(header, ct);
            await to.WriteAsync(body, ct);
        }
    }
}
