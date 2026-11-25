# Fehlerdiagnose, Join-Resume und CPU nach Draft #74

## Zwei-PC-Abnahme

1. Auf beiden PCs dasselbe Drop-in installieren und die Build-ID aus `MP-BUILD` und aus der nativen DLL vergleichen. Basis und Kandidat mit denselben Spieleinstellungen und am selben Kontrollpunkt messen. Den Host einmal tauschen.
2. Im Leerlauf, während einer normalen Koop-Runde und während Abbruch und Rejoin je drei vollständige 60-Sekunden-Fenster aus `kcdmp-native.log` erfassen. Die `FRAME`-Zeile enthält `ours_us_p95` in Mikrosekunden. `Get-Content .\kcdmp-native.log | Select-String 'FRAME window_s='` liefert die Fenster mit Zeitstempel. Aufnahme vor der Aktion starten und mindestens ein vollständiges Fenster danach abwarten.
3. Pro PC, Rolle und Fenster `ours_us_p95 < 2000`, `rp_owner_n > 0` und `owner_violations=0` prüfen. `slow_frames` und `frame_ms_worst` mit erfassen. Zusätzliche Frames über 50 ms erst nach Zuordnung zum Mod bewerten. Bei Bedarf in einer gleichartigen Wiederholung `mp_main_cost on` für die Kosten je Aufgabe aktivieren. [Frame-Messung](../native/KCDMP/frame_meter.h)
4. Einen Join während der Pause abbrechen, danach erneut beitreten. Die Zeilen `MP-JOIN resume-confirmed`, `attempt` und `deferred` anhand derselben Join-ID mit anlaufender Uhr und native DLL-Freigabe abgleichen. Spätes Lua-Feedback darf eine neuere Pause nicht lösen.
5. In einer Steam-Runde zuerst verbunden ohne Spielverkehr, dann mit Bewegung in beiden Rollen messen. Die Pumpe prüft im Leerlauf im 10-ms-Takt, nach bestätigten Nutzdaten für 100 ms im 1-ms-Takt. Auf Windows gilt die WinMM-Anforderung während dieses Aktivitätsfensters. Die echte SDR-Latenz und die Windows-Timerauflösung bleiben Spielmessungen.

## Agent und Relay unter Linux messen

Beide Checkouts in Release bauen. Jeweils die Produktprozesse mit demselben Messskript starten. Das Skript verwendet echtes TCP und lokale HTTP-/Log-Doubles für das Spiel. Der Leerlauf hat einen Agent im Menü. Der zweite Lauf hat vier verbundene Agenten mit Bewegung. Das Skript bricht bei ausbleibenden Positionspaketen ab.

```bash
dotnet build dotnet/KcdMp.Client -c Release
dotnet build dotnet/KcdMp.Server -c Release
python3 tools/sim/measure-process.py /pfad/zur/basis /tmp/kct-cpu-basis
python3 tools/sim/measure-process.py /pfad/zum/kandidaten /tmp/kct-cpu-kandidat
```

Die CPU-Zeit entspricht je Prozess der Summe aus User- und Systemzeit aus `/proc/<pid>/stat`, auf 60 Sekunden normiert. Die Agent-Summe enthält bei vier Spielern alle vier Prozesse. Die Zeit der Game-Doubles läuft separat. Beide Messungen ohne konkurrierenden Build wiederholen. Für den Simulator den gleichen `--timeline both`-Befehl einmal aufwärmen und danach den Median der fünf CPU-Summen berichten.

## Besitzer und Protokolle

| Pfad | Besitzer | Signal |
| --- | --- | --- |
| Relay-Session | `ClientSessionRunner` vergibt die Diagnose-ID vor dem Handshake. `ProcessFaultLog` beobachtet den Task. | `MP-PROCESS-FAULT operation=relay-session session=… type=… at=… message=…` |
| Wiederholte Diagnose | `ProcessFaultLog` führt je Vorgang Zähler und Sperrfrist. | `suppressed=N` beim nächsten zulässigen Eintrag |
| Host-Pause | `HostJoin` besitzt Join-ID und Check-Token. Lua bestätigt die wiederhergestellte Kalender-Rate über das produktive Log. | `MP-JOIN resume-confirmed` oder `resume-unconfirmed` mit `fallback=mod-safety-timer` |
| Staging | `WorldReceiver` hält die Datei bis zum erfolgreichen Flush und Close. | `staging flush/close failed` und Join-Abbruch mit I/O-Grund |
| Support-Bundle | Eine Alias-Tabelle je Archiv. Loopback, unspezifizierte Adressen und beschriftete Versionswerte bleiben lesbar. | `ip-N` sowie Auslassungsgrund im `MANIFEST.txt` |
| Transport | `LinkScheduler` berechnet Arbeitstermine. Ein Einmal-Timer pro Prozess weckt die fälligen Writer. Je Link liefert ein `LinkWriter` zuverlässige Ereignisse und jeweils den neuesten Zustand. | `MP-LINK` mit Sendungen, Repeats, Rückstau und Alterswerten |

Entfallen sind der Relay-Timer pro Verbindung, der feste Leerlauftakt des Agent-Writers, der ungefilterte ZIP-Fallback und die beiden einmaligen Resume-Aufrufe. [Catch-Audit](REVIEW-74-catch-audit.md)
