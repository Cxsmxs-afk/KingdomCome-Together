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
        Directory.CreateDirectory(root);
        try
        {
            Environment.SetEnvironmentVariable("KCDMP_DATA_DIR", root);
            return await CheckAbortsAsync(root, suffix);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KCDMP_DATA_DIR", previousDataDir);
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

        // There is no running game in this probe. Use a private pipe name with no server
        // and handle console replies in process, so game resets cannot reach a real instance.
        await using var pipe = new CombatPipe("kcdmp-sim-abort-" + suffix);
        using var game = new GameConsole();
        await using var console = new HttpGameTransport("http://unused.invalid", 5000, game);
        var bridge = new GameBridge(new ClientConfig());
        var retained = bridgeType.GetField("_retainedJoinWorlds", PrivateInstance)!.GetValue(bridge)!;
        var keep = retained.GetType().GetMethod("Keep")!;
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

        var placedWorlds = new List<string>(Attempts);
        for (uint id = 1; id <= Attempts; id++)
        {
            string slot = Path.Combine(savesRoot, $"playline{id - 1}");
            Directory.CreateDirectory(slot);
            string name = $"mpworld{id:x8}";
            string placed = Path.Combine(slot, name + ".whs");
            File.WriteAllText(placed, "synthetic world at the already verified placement boundary");
            placedWorlds.Add(placed);

            object join = Activator.CreateInstance(joinType, nonPublic: true)!;
            joinType.GetField("JoinId")!.SetValue(join, id);
            joinType.GetField("Host")!.SetValue(join, (byte)0);
            joinType.GetField("PlacedPath")!.SetValue(join, placed);
            joinType.GetField("PlacedVerified")!.SetValue(join, true);
            joinType.GetField("Playline")!.SetValue(join, (int)id - 1);
            joinType.GetField("Name")!.SetValue(join, name);
            joinField.SetValue(bridge, join);
            keep.Invoke(retained, [placed, join]);
            // The session link is stubbed out. All cleanup goes through the production abort method.
            var aborted = (Task)abort.Invoke(bridge, [join, Protocol.JoinAbortLoadFailed, "clock-unavailable", "Simulated failed clock bootstrap.", false])!;
            await aborted.WaitAsync(TimeSpan.FromSeconds(10));
            if (joinField.GetValue(bridge) is not null || !Equals(where.GetValue(bridge), menu))
                throw new InvalidOperationException($"Join {id} did not terminate at the menu");
            if (!File.Exists(personalSave) || !File.ReadAllBytes(personalSave).AsSpan().SequenceEqual(original))
                throw new InvalidOperationException($"Join {id} changed the personal save");
        }

        var leftovers = Directory.EnumerateFiles(savesRoot, "*", SearchOption.AllDirectories)
            .Where(path => !StringComparer.OrdinalIgnoreCase.Equals(path, personalSave) &&
                StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(path), ".whs"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        int retainedSlots = leftovers.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Console.WriteLine($"JOIN-ABORT-GATE attempts={Attempts} retained_slots={retainedSlots} own_save_unchanged=1");
        if (retainedSlots <= 1 && leftovers.Count <= 1) return 0;
        return retainedSlots == Attempts && leftovers.SetEquals(placedWorlds) ? 19 : 1;
    }

    private sealed class GameConsole : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
    }
}
