// Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
using KcdMp.Wire;

namespace KcdMp.Client;

public enum GuidedRunStation { See, Walk, Fight, Down, Up, Reload, Disconnect }
public enum GuidedRunEvidence { ManualYes, ManualNo, Observed }

public readonly record struct GuidedRunStationResult(GuidedRunStation Station, GuidedRunEvidence Evidence, uint World,
    ulong Generation, DateTimeOffset RecordedAtUtc, uint? BaselineSnapshot, long? ReconnectMs);

public sealed record GuidedRunReport(Guid RunId, string Role, uint StartWorld, ulong StartGeneration,
    DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc, IReadOnlyList<GuidedRunStationResult> Stations);

internal enum GuidedRunAction { Start, Status, Next, Repeat, Abort }
internal readonly record struct GuidedRunCommand(GuidedRunAction Action, bool? Answer = null)
{
    public const string Flag = "--test-guided-run";

    public static bool TryParse(string input, string? environment, out GuidedRunCommand command)
    {
        command = default;
        if (environment != "1") return false;
        string[] tokens = input.Split(' ');
        if (tokens.Length is < 2 or > 3 || tokens[0] != Flag) return false;
        GuidedRunAction? action = tokens[1] switch
        {
            "start" => GuidedRunAction.Start, "status" => GuidedRunAction.Status, "weiter" => GuidedRunAction.Next,
            "wiederholen" => GuidedRunAction.Repeat, "abbruch" => GuidedRunAction.Abort, _ => null
        };
        if (action is not { } selected) return false;
        if (tokens.Length == 2) { command = new(selected); return true; }
        if (selected != GuidedRunAction.Next || tokens[2] is not ("ja" or "nein")) return false;
        command = new(selected, tokens[2] == "ja");
        return true;
    }
}

internal sealed record GuidedRunContext(string Role, WorldSyncScope Scope, byte Partner, Stream Link, uint? BaselineSnapshot);
internal readonly record struct GuidedRunReply(string Text, Guid? Started = null, GuidedRunStationResult? Station = null,
    GuidedRunReport? Completed = null, Guid? RunId = null, string? AbortedRole = null);

internal sealed class GuidedRun
{
    private const string Prefix = "mp_testlauf --test-guided-run ";
    private static readonly GuidedRunStation[] Stations = Enum.GetValues<GuidedRunStation>();
    private readonly object _gate = new();
    private Active? _active;
    private Guid? _collecting;
    private string? _last;

    private sealed class Active(GuidedRunContext start, DateTimeOffset utc)
    {
        public readonly Guid Id = Guid.NewGuid();
        public readonly GuidedRunContext Start = start;
        public readonly DateTimeOffset StartedAtUtc = utc;
        public readonly List<GuidedRunStationResult> Results = new(Stations.Length);
        public GuidedRunContext Context = start;
        public ulong ReloadGeneration;
        public bool LostLink;
        public long? ReconnectAttempt, ReconnectMs;
        public GuidedRunStation Station => Stations[Results.Count];
    }

    public GuidedRunReply Command(GuidedRunCommand command, GuidedRunContext? current, DateTimeOffset utc)
    {
        lock (_gate)
        {
            if (command.Action == GuidedRunAction.Start)
            {
                if (_active is { } active) return new("Ein Testlauf läuft bereits. " + Prompt(active));
                if (_collecting is not null) return new("Die Auswertung des letzten Testlaufs läuft noch.");
                if (current is null || !current.Scope.IsValid || current.Role is not ("host" or "joiner"))
                    return new("Start erst in einer gemeinsamen spielbereiten Welt mit genau einem Partner.");
                _active = new(current, utc);
                _last = null;
                return new(Prompt(_active), Started: _active.Id, RunId: _active.Id);
            }

            if (_active is not { } run) return new(_last ?? "Kein aktiver Testlauf. Start mit " + Prefix + "start.");
            if (command.Action == GuidedRunAction.Abort)
            {
                _active = null;
                _last = "Testlauf abgebrochen.";
                return new(_last, RunId: run.Id, AbortedRole: run.Start.Role);
            }
            if (command.Action is GuidedRunAction.Status or GuidedRunAction.Repeat)
                return new(Prompt(run), RunId: run.Id);
            if (command.Action != GuidedRunAction.Next) return new(Prompt(run), RunId: run.Id);

            if (!TryRecord(run, current, command.Answer, utc, out var recorded, out string? blocked))
                return new(blocked + " " + Prompt(run), RunId: run.Id);

            run.Results.Add(recorded);
            if (run.Results.Count < Stations.Length)
                return new(EvidenceText(recorded.Evidence) + " festgehalten. " + Prompt(run), Station: recorded, RunId: run.Id);

            var report = new GuidedRunReport(run.Id, run.Start.Role, run.Start.Scope.HostWorld, run.Start.Scope.Generation,
                run.StartedAtUtc, utc, Array.AsReadOnly(run.Results.ToArray()));
            _active = null;
            _collecting = report.RunId;
            _last = "Testlauf abgeschlossen. Logs und Ampel werden lokal erstellt.";
            return new(EvidenceText(recorded.Evidence) + " festgehalten. " + _last, Station: recorded, Completed: report, RunId: run.Id);
        }
    }

    private static bool TryRecord(Active run, GuidedRunContext? current, bool? answer, DateTimeOffset at,
        out GuidedRunStationResult result, out string? blocked)
    {
        result = default;
        blocked = null;
        var previous = run.Context;
        bool same = current is not null && current.Role == previous.Role && current.Scope == previous.Scope &&
            current.Partner == previous.Partner && ReferenceEquals(current.Link, previous.Link);
        var scope = previous.Scope;
        uint? baseline = null;
        long? reconnectMs = null;
        switch (run.Station)
        {
            case GuidedRunStation.Reload:
                bool loaded = current is not null && current.Role == previous.Role && current.Partner == previous.Partner &&
                    ReferenceEquals(current.Link, previous.Link) && current.Scope.HostWorld > previous.Scope.HostWorld &&
                    current.Scope.Generation == run.ReloadGeneration;
                if (!loaded && !(answer == false && same))
                {
                    blocked = "Die Ausgangswelt oder eine dazugehörige neue Spielgeneration ist gerade nicht nachgewiesen.";
                    return false;
                }
                if (loaded)
                {
                    scope = current!.Scope;
                    baseline = current.BaselineSnapshot;
                }
                if (answer is null && baseline is null)
                {
                    blocked = "Für die automatische Erfassung fehlt die bestätigte Baseline der neuen Welt.";
                    return false;
                }
                if (loaded) run.Context = current!;
                break;
            case GuidedRunStation.Disconnect:
                bool returned = current is not null && current.Role == previous.Role && current.Scope == previous.Scope;
                if (answer is null && (run.ReconnectMs is null || !returned))
                {
                    blocked = "Es fehlt die bestätigte Rückkehr in diese Welt.";
                    return false;
                }
                if ((answer == true && !run.LostLink) || (answer == false && !run.LostLink && !same))
                {
                    blocked = "Für diese Station fehlt eine Verbindung oder ein beobachteter Abbruch aus diesem Testlauf.";
                    return false;
                }
                if (answer is null) reconnectMs = run.ReconnectMs;
                break;
            default:
                if (!same)
                {
                    blocked = "Diese Station gehört zur gemeinsamen Ausgangswelt und ihrer Verbindung.";
                    return false;
                }
                if (answer is null)
                {
                    blocked = "Bitte die eigene Beobachtung mit weiter ja oder weiter nein eintragen.";
                    return false;
                }
                break;
        }
        var evidence = answer switch { true => GuidedRunEvidence.ManualYes, false => GuidedRunEvidence.ManualNo, _ => GuidedRunEvidence.Observed };
        result = new(run.Station, evidence, scope.HostWorld, scope.Generation, at, baseline, reconnectMs);
        return true;
    }

    public void ObserveLoad(WorldLoadSnapshot loading, Stream link)
    {
        lock (_gate)
        {
            if (_active is { Station: GuidedRunStation.Reload } run && ReferenceEquals(run.Context.Link, link) &&
                loading.Phase == WorldLoadPhase.Load && loading.Generation > run.Context.Scope.Generation)
                run.ReloadGeneration = loading.Generation;
        }
    }

    public void ObserveLoss(WorldSyncScope scope, Stream link, byte? peer = null)
    {
        lock (_gate)
        {
            if (_active is { Station: GuidedRunStation.Disconnect } run && scope == run.Context.Scope &&
                ReferenceEquals(link, run.Context.Link) && (peer is null || peer == run.Context.Partner)) run.LostLink = true;
        }
    }

    public void ObserveReconnect(string role, WorldSyncScope scope, long attempt, string outcome, long elapsedMs, bool reload)
    {
        lock (_gate)
        {
            if (_active is not { Station: GuidedRunStation.Disconnect } run || run.Context.Role != role ||
                scope != run.Context.Scope || attempt <= 0) return;
            if (outcome == "started")
            {
                run.LostLink = true;
                run.ReconnectAttempt = attempt;
                run.ReconnectMs = null;
            }
            else if (outcome == "same-world" && !reload && run.ReconnectAttempt == attempt && elapsedMs is >= 0 and <= 30_000)
                run.ReconnectMs = elapsedMs;
        }
    }

    public bool Collected(Guid runId, string text)
    {
        lock (_gate)
        {
            if (_collecting != runId) return false;
            _collecting = null;
            _last = text;
            return true;
        }
    }

    private static string Prompt(Active run)
    {
        string instruction = run.Station switch
        {
            GuidedRunStation.See => "Sehen. Prüft auf beiden PCs, ob der andere Henry sichtbar ist und zur richtigen Figur gehört.",
            GuidedRunStation.Walk => "Laufen. Lauft nacheinander über ebenen Boden und eine Treppe. Achtet auf Gangart, Blick und Versetzungen.",
            GuidedRunStation.Fight => "Kämpfen. Prüft im Wegwerfstand Schlag, Block, Parade und Trefferreaktion. Achtet auf doppelten Schaden.",
            GuidedRunStation.Down => "Umfallen. Geht nacheinander KO. Prüft, ob der andere Henry die Figur an derselben Stelle liegen sieht.",
            GuidedRunStation.Up => "Aufstehen. Prüft, ob dieselbe Figur an dieser Stelle wieder aufsteht und sich bewegen lässt.",
            GuidedRunStation.Reload => "Neu laden. Der Host lädt den Wegwerfstand. Wartet, bis ihr beide wieder im Spiel seid und den anderen Henry seht.",
            GuidedRunStation.Disconnect => "Trennen. Unterbrecht die Testverbindung. Prüft die Rückkehr in dieselbe Welt ohne Ladebildschirm.",
            _ => ""
        };
        string answer = run.Station is GuidedRunStation.Reload or GuidedRunStation.Disconnect
            ? " weiter übernimmt vorhandene automatische Belege." : "";
        return $"Station {run.Results.Count + 1}/{Stations.Length}. {instruction} Mit {Prefix}weiter ja oder weiter nein bewertet ihr selbst.{answer}";
    }

    private static string EvidenceText(GuidedRunEvidence evidence) => evidence switch
    {
        GuidedRunEvidence.ManualYes => "Manuell bestätigt", GuidedRunEvidence.ManualNo => "Manuell verneint", _ => "Automatisch beobachtet"
    };
}
