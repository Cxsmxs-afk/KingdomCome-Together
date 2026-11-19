// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Net;
using System.Reflection;
using KcdMp.Client;

namespace KcdMp.Sim;

internal static class JoinAbortRegression
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const int Attempts = 5;

    public static async Task<int> RunAsync()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string root = Path.Combine(Path.GetTempPath(), "kcdmp-sim-abort-" + suffix);
        string? previousDataDir = Environment.GetEnvironmentVariable("KCDMP_DATA_DIR");
        string? previousSavesDir = Environment.GetEnvironmentVariable("KCDMP_JOIN_SAVES_DIR");
        Directory.CreateDirectory(root);
        try
        {
            Environment.SetEnvironmentVariable("KCDMP_DATA_DIR", root);
            Environment.SetEnvironmentVariable("KCDMP_JOIN_SAVES_DIR", Path.Combine(root, "saves"));
            return await CheckAbortsAsync(root, suffix);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KCDMP_DATA_DIR", previousDataDir);
            Environment.SetEnvironmentVariable("KCDMP_JOIN_SAVES_DIR", previousSavesDir);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<int> CheckAbortsAsync(string root, string suffix)
    {
        var bridgeType = typeof(GameBridge);
        var joinField = bridgeType.GetField("_jj", PrivateInstance)!;
        var joinType = joinField.FieldType;
        var abort = bridgeType.GetMethod("AbortJoinerJoinAsync", PrivateInstance)!;
        var where = bridgeType.GetField("_where", PrivateInstance)!;
        var selectPlayline = bridgeType.GetMethod("Wo175JoinPlayline", PrivateInstance)!;
        var publish = bridgeType.GetMethod("TryPublishJoinWorld", PrivateInstance)!;
        var submit = bridgeType.GetMethod("TryBeginJoinLoad", PrivateInstance)!;

        // There is no running game in this probe. Use a private pipe name with no server
        // and handle console replies in process, so game resets cannot reach a real instance.
        await using var pipe = new CombatPipe("kcdmp-sim-abort-" + suffix);
        using var game = new GameConsole();
        await using var console = new HttpGameTransport("http://unused.invalid", 5000, game);
        var bridge = new GameBridge(new ClientConfig());
        bridgeType.GetField("_combat", PrivateInstance)!.SetValue(bridge, pipe);
        bridgeType.GetField("_transport", PrivateInstance)!.SetValue(bridge, console);
        object menu = Enum.Parse(where.FieldType, "Menu");
        where.SetValue(bridge, menu);

        string savesRoot = Path.Combine(root, "saves");
        string personalDir = Path.Combine(savesRoot, "playline0");
        Directory.CreateDirectory(personalDir);
        string personalSave = Path.Combine(personalDir, "permanent001.whs");
        byte[] original = "synthetic personal save"u8.ToArray();
        File.WriteAllBytes(personalSave, original);

        byte[] placedBytes = "synthetic world at the already verified placement boundary"u8.ToArray();
        string hostTag = WhsSave.SeedTag(0x12345678);
        int? firstPlayline = null;
        for (uint id = 1; id <= Attempts; id++)
        {
            if (selectPlayline.Invoke(bridge, [savesRoot, hostTag, id]) is not int playline || playline is < 0 or > 4)
                throw new InvalidOperationException($"Join {id} could not allocate a co-op playline");
            firstPlayline ??= playline;
            if (playline != firstPlayline) throw new InvalidOperationException($"Join {id} did not reuse playline{firstPlayline}: selected playline{playline}");

            var (join, _) = Publish(id, playline);
            await Abort(join, "clock-unavailable");
            if (joinField.GetValue(bridge) is not null || !Equals(where.GetValue(bridge), menu))
                throw new InvalidOperationException($"Join {id} did not terminate at the menu");
            AssertPersonalUnchanged(id);
        }

        var leftovers = Directory.EnumerateFiles(savesRoot, "*", SearchOption.AllDirectories)
            .Where(path => !StringComparer.OrdinalIgnoreCase.Equals(path, personalSave)
                && StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(path), ".whs"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int retainedSlots = leftovers.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        // Another attempt shares the personal save's playline as in the legacy playline mode.
        // Once a load could read its file, abort may retire the attempt but has to preserve the bytes.
        var (submitted, submittedPath) = Publish(Attempts + 1, 0);
        if (submit.Invoke(bridge, [submitted, savesRoot]) is not true)
            throw new InvalidOperationException("The production load gate did not pin the submitted world");
        await Abort(submitted, "probe-after-submit");
        if (!File.Exists(submittedPath) || !File.ReadAllBytes(submittedPath).AsSpan().SequenceEqual(placedBytes))
            throw new InvalidOperationException("Aborting a submitted load changed the world file while the engine may still read it");
        AssertPersonalUnchanged(Attempts + 1);
        Console.WriteLine("JOIN-ABORT-LIFECYCLE playline_reused=1 submitted_preserved=1");

        Console.WriteLine($"JOIN-ABORT-GATE attempts={Attempts} retained_slots={retainedSlots} own_save_unchanged=1");
        return retainedSlots == 0 ? 0 : 1;

        (object Join, string Path) Publish(uint id, int playline)
        {
            string slot = Path.Combine(savesRoot, $"playline{playline}");
            Directory.CreateDirectory(slot);
            string name = $"mpworld{id:x8}";
            string placed = Path.Combine(slot, name + ".whs");
            object join = Activator.CreateInstance(joinType, nonPublic: true)!;
            joinType.GetField("JoinId")!.SetValue(join, id);
            joinType.GetField("Host")!.SetValue(join, (byte)0);
            joinType.GetField("Playline")!.SetValue(join, playline);
            joinType.GetField("Name")!.SetValue(join, name);
            joinType.GetField("WorldTag")!.SetValue(join, hostTag);
            joinField.SetValue(bridge, join);
            if (publish.Invoke(bridge, [join, savesRoot, placed, placedBytes, hostTag]) is not true)
                throw new InvalidOperationException($"Join {id} could not publish its verified save");
            if (!File.ReadAllBytes(placed).AsSpan().SequenceEqual(placedBytes))
                throw new InvalidOperationException($"Join {id} did not publish the fixture bytes");
            joinType.GetField("PlacedVerified")!.SetValue(join, true);
            return (join, placed);
        }

        Task Abort(object join, string cause) => ((Task)abort.Invoke(bridge,
            [join, Protocol.JoinAbortLoadFailed, cause, "Simulated join failure.", false])!).WaitAsync(TimeSpan.FromSeconds(10));

        void AssertPersonalUnchanged(uint id)
        {
            if (!File.Exists(personalSave) || !File.ReadAllBytes(personalSave).AsSpan().SequenceEqual(original))
                throw new InvalidOperationException($"Join {id} changed the personal save");
        }
    }

    private sealed class GameConsole : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
    }
}
