// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace KcdMp.Client.Tests;

public sealed partial class JoinCalendarIntegrationTests
{
    [Fact]
    public Task Repeated_preload_aborts_reuse_the_playline_and_require_manual_retry_until_the_host_world_changes() =>
        RunAsync("H19-host", "H19-guest", dropFirstSnapshot: false, JoinProbe.RepeatAborts);

    private static async Task CheckFailedJoinRetriesAsync(SaveFixture fixture, GameBridge host, GameBridge guest,
        byte guestId, JoinCalendarTestPipe guestPipe, JoinCalendarTestEngine guestGame, CancellationToken ct)
    {
        const int manualAttempts = 5;
        string personalSave = fixture.GuestOwnSave;
        var personalFilesBefore = SaveChecksums();
        var placements = new ConcurrentQueue<string[]>();
        guestPipe.BeforeCalendarArm = () => placements.Enqueue(PlacedWorlds());
        string? firstPlayline = null;
        var joinIds = new HashSet<uint>();

        Call(host, "Wo135HostTick");
        await WaitAsync(() => Get<string?>(guest, "_peerHostBuild") is not null,
            "the host's game build over the relay", ct);

        for (int attempt = 1; attempt <= manualAttempts; attempt++)
        {
            // mp_join_request emits this event. The next hand-requested attempt is still allowed
            // while the periodic request is held for this host world.
            guestGame.Emit("join_request", "");
            await VerifyAbortAsync(attempt);
        }

        // A fresh announcement comes from the actual host relay connection and crosses the guest reader.
        var hostEpochs = Get<Wo206Epochs>(host, "_w206");
        uint newWorld = hostEpochs.BumpWorld();
        await InvokeAsync(host, "Wo206AnnounceAsync", "fixture-new-world", (byte?)guestId);
        await WaitAsync(() => Get<Wo206Epochs>(guest, "_w206").World == newWorld, "a new authenticated host world epoch", ct);
        int requestsBeforeNewWorld = Get<int>(guest, "_autoRequests");
        Set(guest, "_autoNextUtc", DateTime.MinValue);
        await InvokeAsync(guest, "Wo124JoinerTickAsync");
        Assert.Equal(requestsBeforeNewWorld + 1, Get<int>(guest, "_autoRequests"));
        await VerifyAbortAsync(manualAttempts + 1);

        string[] PlacedWorlds() => Directory.GetFiles(fixture.GuestSaves, "mpworld*.whs", SearchOption.AllDirectories);

        (string Path, string Sha256)[] SaveChecksums() =>
            Directory.EnumerateFiles(fixture.GuestSaves, "*.whs", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .Select(path => (Path.GetRelativePath(fixture.GuestSaves, path),
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))))
                .ToArray();

        async Task VerifyAbortAsync(int attempt)
        {
            await WaitAsync(() => placements.Count == attempt && Get<string>(guest, "_joinUiState") == "failed"
                && Get<object?>(guest, "_jj") is null && Get<object?>(host, "_hostJoin") is null,
                $"production placement, native refusal and completed attempt {attempt}", ct);
            Assert.False(guestGame.LoadIssued.IsCompleted, "A refused native latch allowed a load command.");
            string world = Assert.Single(placements.Last());
            string playline = Path.GetDirectoryName(world)!;
            firstPlayline ??= playline;
            Assert.Equal(firstPlayline, playline);
            Assert.NotEqual(Path.GetDirectoryName(personalSave), playline);
            Assert.True(uint.TryParse(Path.GetFileNameWithoutExtension(world).AsSpan("mpworld".Length),
                System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint id));
            Assert.True(joinIds.Add(id), "The attempt did not receive a new JoinId.");
            Assert.Empty(PlacedWorlds());
            Assert.Equal(personalFilesBefore, SaveChecksums());

            // Remove the wall-clock cooldown to prove it is the reason/host scope fence holding the
            // automatic request. The real tick must not advance either the request id or its count.
            int previousAutoRequests = Get<int>(guest, "_autoRequests");
            Assert.Equal(0u, Get<uint>(guest, "_joinOutId"));
            for (int tick = 0; tick < 3; tick++)
            {
                Set(guest, "_autoNextUtc", DateTime.MinValue);
                await InvokeAsync(guest, "Wo124JoinerTickAsync");
                Assert.Equal(0u, Get<uint>(guest, "_joinOutId"));
                Assert.Equal(previousAutoRequests, Get<int>(guest, "_autoRequests"));
            }
        }
    }
}
