# P0: Prozessor-Lebensdauer, Wetter und Load-Replay

Basis: PR #17, `339d78c`, `codex/loot-ownership-polish`.

## Befund aus allen sechs Bundles

| Sitzung | Agent-Beleg | Beobachtung |
| --- | --- | --- |
| S1, 234935/235020 | `agent.log:1364` | Kein STALL. Nach 235,5 s werden 121 gehaltene Frames wieder eingespielt. |
| S2, 235809 | `agent.log:588`, `:684` | 23:54:15.009 Wetter `foggy_storm`; 23:54:40.035 STALL, Typ 117/0x75. Kein späteres STALL-over. Letzter Netzwerk-Ping 23:54:15.599, Zeile 592. |
| S3, 000930 | `agent.log:673`, `:713` | 00:02:22.314 Wetter `foggy_storm`; 00:02:37.333 STALL, Typ 117/0x75. Kein späteres STALL-over. Letzter Netzwerk-Ping 00:02:23.104, Zeile 675. |
| Host, 000909/001025 | Agent über beide Joiner-Sitzungen | Kein entsprechender STALL. |

In S2 bleiben am Ende 223 gehaltene Frames ohne Replay, in S3 226. Hauptqueue- und Activity-Zähler frieren ein, während andere Agent-Schleifen weiterlaufen. Die im Auftrag als 426 s Queue-Verzug bezeichneten **425897 ms sind `act_lag_ms`** (`S3/agent.log:4990`); sie sind kein weiter ansteigender Hauptqueue-Lag. Dessen eingefrorener Wert und Waiting-Zähler beweisen keinen laufenden Prozessor.

Die Pipe antwortet später weiter: S2 `agent.log:2361` RTT Mittel 7/max. 83 ms, S3 `:4928` Mittel 8/max. 33 ms; jeweils failed=0. Native Mirror: S2 Zeile 4705 Drain Mittel 16/max. 52 µs, S3 Zeile 4943 Mittel 16/max. 103 µs. `pipe_ms=0` misst nur Gate-Wartezeit und identifiziert keinen HTTP-Leaf.

## Gefundener Abbruchpfad

Der Wetterpfad ist `Wo164OnFrameAsync` → `Wo166OnHostWeatherAsync` → `Wo151OnHostWeatherAsync`/`Wo151WeatherGateAsync` → `ApplyWeatherAsync`. Darin und im Kalender-/Authority-Pfad stehen Pipe- und `_transport.ExecuteNowAsync`-Awaits. Die HTTP-Konsole hat 800 ms Timeout. `HttpClient` meldet diesen als `OperationCanceledException`, auch wenn das Session-Token weiter gültig ist.

In der Basis liegt der einzige äußere `catch (OperationCanceledException)` um **den gesamten** `ProcessFramesAsync`-Reader. Ein solcher Fehler beendet ihn deshalb dauerhaft. Der Watchdog bekommt kein Processor-Off und meldet den letzten Frame weiter als STALL. Die Ping-Frames werden ebenfalls dort verarbeitet; ihr Ende 590/790 ms nach dem Wetter passt zu diesem Pfad. Nicht jede Wetter-/Kalender-Unterfunktion fängt gleich ab, teils läuft zusätzlich eine verzögerte Gameplay-Aufgabe.

**Die exakte damals blockierte Leaf-Await ist aus den vorhandenen Logs nicht beweisbar.** Es gibt weder Stackdump noch korrelierte Start-/Ende-Spans. Ein dauerhaft blockierter DLL-Aufruf ist damit nicht belegt. Der Prozessor-Abbruch bei einem nicht zur Session gehörenden HTTP-Timeout ist im Code vorhanden und durch einen Test des tatsächlichen Prozessors reproduziert.

## Änderung

- Jeder Prozessor-Handler läuft in einer widerrufbaren `ProcessorFrameScope`, einschließlich synchron blockierender Handler. Timeout oder Fehler geben den Prozessor frei; die Task wird beobachtet und ihre späteren Pipe-/HTTP-/Relay-Schreibversuche werden verworfen. Ein Timeout ist kein Session-Ende.
- Standardbudget 2000 ms, mit kleiner Ablaufreserve für Deadline-/Aufräumkosten; gemessene Dauer wird unverändert gezählt. Kein künstliches Abschneiden der Messwerte. Das Betriebssystem ist kein Echtzeitsystem.
- Scope-Cancellation reicht durch Pipe-Verbindungs-/Schreib-Gates, Batch-Einträge, HTTP-Queue/Retry und Relay-Scheduler bis zur letzten Prüfung vor dem Senden. Eine normale Handler-Completion lässt bereits legitim eingereihte Arbeit bestehen; das spätere Session-Ende widerruft sie. Dauerhafte Pumpen erben keine einzelne Frame-Lease. Bereits beim Spiel eingetroffene Engine-Aufrufe lassen sich nicht zurückrollen.
- Kalender und beide Wetterformate werden schon beim Lesen auf den neuesten Wert je Nebenkanal reduziert. Normale Anwendung erst nach Gameplay + 5 s Settle, fair abwechselnd. Join-Bootstrap/Frozen-Host-Snapshot dürfen vor dem Load laufen, sonst könnte der Join seine eigene Voraussetzung nicht herstellen. Load, Rollenwechsel und Verbindungsende widerrufen alte Nebenkanal-Arbeit.
- Ein eigener 100-ms-Tick führt WO136-Replay aus. Er hängt weder am Prozessor noch an der Konfigurationsabfrage oder einem Wetter-Await. Fehler einer Iteration beenden diesen Tick nicht. Replay ist an Writer und Receive-Lebensdauer gebunden.
- Trace-Ende erfolgt pro Frame genau einmal, auch bei verspätetem Handler-Ende. Ein abgebrochener Rollenwechsel bleibt erneut anwendbar; derselbe Role-Frame repariert die unvollständige Konfiguration.

Konsole: `mp_proc_frame_timeout <ms>`; Standard **2000**, zulässig 50–30000. Ohne Argument wird der Wert ausgegeben.

60-s-Zeile `MP-T3-STATS`: `proc_stalls`, `proc_stall_max_ms`, `proc_frames_abandoned`, `deferred_sidechannel`, `w136_held`, `w136_replayed`; zusätzlich `proc_frame_faults`, `proc_tick_faults`, `proc_lua_abandoned`, `proc_outbound_abandoned`. Der Agent schreibt je Rolle; der DLL-Spiegel schreibt validierte Zahlen als `origin=dll source=agent`. Dies sind dieselben Agent-Messwerte, keine unabhängig gemessene Engine-Latenz. Die vorhandene Collect-Logs-STATS-Ausnahme schützt diese Zeilen.

## Prüfung

Die Tests verwenden den tatsächlichen privaten Channel-Prozessor, einen absichtlich nie antwortenden HTTP-Handler, die echte WO136-Hold-/Replay-Logik und 100 nachfolgende Frames. Erste Messung: `proc_stall_max_ms=1951`, `catchup_ms=2034`, `backlog_after_ms=0`, `replayed=1`. Ein gültiger W164-Wetterframe während Loading führt zu keinem Spielaufruf; der folgende normale Frame wird verarbeitet. Weitere Tests prüfen verspätete HTTP-/Pipe-/Relay-Antworten, Session-Ende und Rollenkonfiguration.

Windows-CI, Drop-in und finale Testzahlen werden im PR dokumentiert. **Im Spiel offen:** beide PCs, Laden/Quicksave/Menü mit `foggy_storm`, fortlaufende Pings, Replay, sichtbare NPC-Bindung und Loot-Antworten. Retail ohne Modding Tools nicht geprüft.
