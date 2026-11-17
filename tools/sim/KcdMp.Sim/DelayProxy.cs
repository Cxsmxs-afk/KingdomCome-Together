using System.Net;
using System.Net.Sockets;

namespace KcdMp.Sim;

/// <summary>A loopback TCP proxy that delays, jitters and drops whole frames. No root, no tc.</summary>
public sealed class DelayProxy : IAsyncDisposable
{
    private readonly TcpListener _listen;
    private readonly int _relayPort;
    private readonly Profile _profile;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _links = new();
    public int Port { get; }
    private long _dropped, _forwarded;
    public long Dropped => Interlocked.Read(ref _dropped);
    public long Forwarded => Interlocked.Read(ref _forwarded);

    public DelayProxy(int relayPort, Profile profile)
    {
        _relayPort = relayPort;
        _profile = profile;
        _listen = new TcpListener(IPAddress.Loopback, 0);
        _listen.Start();
        Port = ((IPEndPoint)_listen.LocalEndpoint).Port;
        _ = AcceptLoop();
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listen.AcceptTcpClientAsync(_cts.Token);
                _links.Add(Task.Run(() => Pump(client)));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task Pump(TcpClient client)
    {
        using var _ = client;
        client.NoDelay = true;
        using var relay = new TcpClient { NoDelay = true };
        await relay.ConnectAsync(IPAddress.Loopback, _relayPort);
        var a = Copy(client.GetStream(), relay.GetStream(), _cts.Token);
        var b = Copy(relay.GetStream(), client.GetStream(), _cts.Token);
        await Task.WhenAny(a, b);
    }

    private async Task Copy(NetworkStream from, NetworkStream to, CancellationToken ct)
    {
        var header = new byte[3];
        int passed = 0;
        while (!ct.IsCancellationRequested)
        {
            if (!await ReadExact(from, header, ct)) return;
            int len = header[1] | (header[2] << 8);
            var payload = new byte[len];
            if (len > 0 && !await ReadExact(from, payload, ct)) return;
            var frame = new byte[3 + len];
            header.AsSpan().CopyTo(frame);
            if (len > 0) payload.AsSpan().CopyTo(frame.AsSpan(3));
            passed++;
            // The handshake and its answer go straight through. Impairment starts on the traffic.
            if (passed <= 2)
            {
                await to.WriteAsync(frame, ct);
                Interlocked.Increment(ref _forwarded);
                continue;
            }
            if (DropNow())
            {
                Interlocked.Increment(ref _dropped);
                continue;
            }
            int delay = _profile.DelayMs + (int)((_profile.Rng.NextDouble() * 2 - 1) * _profile.JitterMs);
            if (_profile.Burst && _profile.Rng.NextDouble() < 0.04) delay += 400;
            if (delay < 0) delay = 0;
            _ = Task.Run(async () =>
            {
                try
                {
                    if (delay > 0) await Task.Delay(delay, ct);
                    await to.WriteAsync(frame, ct);
                    Interlocked.Increment(ref _forwarded);
                }
                catch { }
            }, ct);
        }
    }

    private bool DropNow()
    {
        if (_profile.Loss <= 0) return false;
        if (_profile.Burst && _profile.Rng.NextDouble() < 0.04) return _profile.Rng.NextDouble() < 0.5;
        return _profile.Rng.NextDouble() < _profile.Loss;
    }

    private static async Task<bool> ReadExact(NetworkStream s, byte[] buf, CancellationToken ct)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(got), ct);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listen.Stop();
        try { await Task.WhenAll(_links).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}

public sealed class Profile
{
    public string Name { get; init; } = "";
    public int DelayMs { get; init; }
    public int JitterMs { get; init; }
    public double Loss { get; init; }
    public bool Burst { get; init; }
    public Random Rng { get; } = new(17);

    public static Profile[] All { get; } =
    [
        new() { Name = "good", DelayMs = 20 },
        new() { Name = "typical", DelayMs = 80, JitterMs = 20, Loss = 0.005 },
        new() { Name = "bad", DelayMs = 150, JitterMs = 60, Loss = 0.02 },
        new() { Name = "awful", DelayMs = 300, JitterMs = 150, Loss = 0.05, Burst = true },
    ];
}
