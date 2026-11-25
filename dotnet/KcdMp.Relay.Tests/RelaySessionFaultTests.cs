// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using System.Threading.Channels;
using KcdMp.Server.Features.ClientHandling;
using KcdMp.Server.Features.Interactions;
using KcdMp.Server.Features.Tcp;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit.Abstractions;

namespace KcdMp.Relay.Tests;

public sealed class RelaySessionFaultTests(ITestOutputHelper output)
{
    [Fact]
    public async Task A_crashing_relay_session_logs_one_scrubbed_fault_with_location_and_session_id()
    {
        const string address = "192.0.2.153";
        string steam = "76561" + new string('0', 12);
        var sink = new FaultSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var configuration = new ConfigurationBuilder().Build();
        var clients = new ClientHandler(logger, configuration);
        var runner = new ClientSessionRunner(logger, configuration, clients,
            new TcpBroadcastService(configuration, clients), new SessionManager(logger));
        using var stream = new CrashingStream($"fixture read at {address} steamid:{steam} password=fixture-private");
        var session = runner.Create(new RelayConnection(stream, "fixture", false, "tcp", stream.Dispose));

        runner.Start(session);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string fault = await sink.Lines.Reader.ReadAsync(deadline.Token);
        Assert.Equal(0, clients.ClientCount);
        Assert.Contains("type=InvalidOperationException", fault);
        Assert.Contains("ClientSession.ReadExactAsync", fault);
        Assert.Matches(@"session=[0-9a-f]+", fault);
        Assert.Contains("fixture read", fault);
        Assert.Contains("password=<redacted>", fault);
        Assert.DoesNotContain(address, fault);
        Assert.DoesNotContain(steam, fault);
        Assert.DoesNotContain("fixture-private", fault);
        Assert.False(sink.Lines.Reader.TryRead(out _));
        output.WriteLine(fault);
    }

    private sealed class FaultSink : ILogEventSink
    {
        public Channel<string> Lines { get; } = Channel.CreateUnbounded<string>();

        public void Emit(LogEvent logEvent)
        {
            if (logEvent.Properties.TryGetValue("Fault", out var property) &&
                property is ScalarValue { Value: string fault })
                Lines.Writer.TryWrite(fault);
        }
    }

    private sealed class CrashingStream(string fault) : MemoryStream
    {
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromException<int>(new InvalidOperationException(fault));
    }
}
