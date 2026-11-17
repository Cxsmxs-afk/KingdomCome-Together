// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
// GPLv3 section 7 additional terms: NOTICE. This project's own code only; Kingdom Come: Deliverance II and its
// content belong to Warhorse Studios and PLAION. Unofficial, free, not affiliated with or endorsed by them.
using System.Security.Cryptography;
using System.Text;
using KcdMp.Client;

namespace KcdMp.Sim;

/// <summary>
/// Deterministic join-file lifecycle checks using the production discovery and retention code.
/// The profile names select missing/delayed list replies here; this is not an engine load,
/// a SaveList timing measurement, or another network simulation. Program's relay runs measure the link.
/// </summary>
public static class JoinDiscovery
{
    private sealed record Scenario(string Name, int Unreadable, int Missing);
    private static readonly Scenario[] Scenarios =
    [
        new("good", 1, 1), new("typical", 2, 1),
        new("bad", 3, 2), new("awful", 4, 3),
    ];

    public static async Task<(bool Pass, string Report)> RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "kcdmp-sim-join-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var report = new StringBuilder("\n## Join-save discovery and retention\n\n");
        report.AppendLine("Deterministic lifecycle checks with synthetic bytes and injected SaveList replies. No game or engine timing is measured; the profile labels select reply sequences. The network runs above still use the real relay.");
        report.AppendLine();
        report.AppendLine("| Players | Reply sequence | Joiners | Same-file retries | Exhausted retry kept | Cancel kept until authorized | Pass |");
        report.AppendLine("|---|---|---:|---:|---|---|---|");
        bool pass = true;
        try
        {
            foreach (int players in new[] { 2, 3, 4 })
            foreach (var scenario in Scenarios)
            {
                int calls = 0;
                bool rowPass = true;
                string detail = "";
                for (int joiner = 1; joiner < players; joiner++)
                {
                    string dir = Path.Combine(root, $"p{players}-{scenario.Name}-j{joiner}");
                    Directory.CreateDirectory(dir);
                    try { calls += await CheckJoinerAsync(dir, scenario); }
                    catch (Exception ex)
                    {
                        rowPass = false;
                        detail = ex.GetType().Name + ": " + ex.Message;
                    }
                }
                pass &= rowPass;
                report.AppendLine($"| {players} | {scenario.Name}: {scenario.Unreadable} unreadable, {scenario.Missing} missing, listed | {players - 1} | {calls} | {(rowPass ? "yes" : "failed")} | {(rowPass ? "yes" : "failed")} | {(rowPass ? "PASS" : "FAIL")} |");
                if (!rowPass) report.AppendLine($"\nFailure: {detail}\n");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
        return (pass, report.ToString());
    }

    private static async Task<int> CheckJoinerAsync(string dir, Scenario scenario)
    {
        // The transfer's checksum/WhsSave verification is outside this lifecycle model.
        // These bytes stand for its already verified, placed result and must not change during discovery.
        byte[] bytes = Encoding.ASCII.GetBytes("synthetic placed host world - " + scenario.Name);
        string path = Path.Combine(dir, "mpworld1116a6a9.whs");
        await File.WriteAllBytesAsync(path, bytes);
        byte[] sha = SHA256.HashData(bytes);
        var files = new JoinWorldFiles<object>();
        var owner = new object();
        files.Keep(path, owner);
        const int playline = 2;
        const string name = "mpworld1116a6a9";
        var operations = new List<byte>();
        int calls = 0;
        var listed = await JoinSaveList.WaitAsync((op, _) =>
        {
            operations.Add(op);
            Check(files.Protects(path), "discovery lost sweep protection");
            Check(!files.MayRemove(path, owner) && !files.TryDelete(path, owner), "discovery deleted the placed world");
            Check(files.End(joined: false, cancelled: false).Length == 0, "failed list authorized cleanup");
            Check(SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(sha), "discovery changed the placed file");
            calls++;
            CombatPipe.SaveListReport? reply = calls <= scenario.Unreadable ? null
                : new(calls > scenario.Unreadable + scenario.Missing, 0, 1, playline, playline, 0, name);
            return Task.FromResult(reply);
        }, () => true, delay: NoDelay);
        Check(listed is { Listed: true } && listed.ContinuePlayline == playline && listed.ContinueName == name, "discovery did not find the same world");
        Check(calls == scenario.Unreadable + scenario.Missing + 1, "unexpected number of discovery attempts");
        Check(operations[0] == 1 && operations.Skip(1).Take(scenario.Unreadable).All(op => op == 3), "a timed-out rescan was followed by another full rescan");
        Check(File.Exists(path) && !files.TryDelete(path, owner), "GameplayStarted alone must not release the world");
        Check(files.End(joined: true, cancelled: false).Single() == owner, "join=in did not authorize its world");
        Check(files.Protects(path), "authorized cleanup lost protection before deletion");
        Check(files.TryDelete(path, owner) && !File.Exists(path) && !files.Protects(path), "join=in cleanup failed");

        // No usable list ever arrives: bounded retries resume the host, but retain the verified file.
        await File.WriteAllBytesAsync(path, bytes);
        files.Keep(path, owner);
        int exhausted = 0;
        var missing = await JoinSaveList.WaitAsync((_, _) =>
        {
            exhausted++;
            return Task.FromResult<CombatPipe.SaveListReport?>(null);
        }, () => true, delay: NoDelay);
        Check(missing is null && exhausted == JoinSaveList.MaxAttempts, "unreadable discovery was not bounded");
        Check(files.End(joined: false, cancelled: false).Length == 0 && !files.TryDelete(path, owner)
              && File.Exists(path) && files.Protects(path), "exhausted discovery removed the retry source");

        // A late successful reply after the player cancels cannot start a load.
        bool active = true;
        int cancelledCalls = 0;
        var cancelled = await JoinSaveList.WaitAsync((_, _) =>
        {
            cancelledCalls++;
            active = false;
            return Task.FromResult<CombatPipe.SaveListReport?>(new(true, 0, 1, playline, playline, 0, name));
        }, () => active, delay: NoDelay);
        Check(cancelled is null && cancelledCalls == 1 && File.Exists(path) && !files.TryDelete(path, owner), "late list survived cancellation or deleted its source");
        files.End(joined: false, cancelled: true);
        Check(files.Protects(path) && files.TryDelete(path, owner) && !File.Exists(path), "player cancel cleanup failed");
        return calls;
    }

    private static Task NoDelay(TimeSpan _, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
