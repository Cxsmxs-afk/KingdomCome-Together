---
name: kct-performance
description: "Performance-Regeln und Messwege für „Kingdom Come: Together“. Laden bei jeder Änderung an Code, der pro Frame, pro Paket oder pro NPC läuft (native DLL-Hooks, Pipe, Agent-Reader/Prozessor, Lua-Ticks, Netz-Sender/-Empfänger, Interpolation), bei Rucklern, FPS-Einbrüchen, Latenz oder Bandbreite, und als Pflichtschritt 5 in kct-fix-workflow. Messen statt raten, mit den vorhandenen Messzeilen."
---

# kct-performance — Flüssig, nicht nur korrekt

Ein Koop fühlt sich nur nativ an, wenn die Mod keine Frames frisst, der Partner ohne Sprünge läuft und Treffer ohne spürbare Verzögerung ankommen. Warum: So steht es in der obersten Regel (`AGENTS.md`). Ein korrekter Fix, der ruckelt, ist nicht fertig.

Zahlen am H0-Stand `6c8a63e` geprüft. Am aktuellen Tip erneut prüfen.

## 1. Wo die Zeit hängt (Architektur)
- **Spiel-Thread (kritisch):** Die DLL hängt sich in `C_ModulesManager::Update` (IAT-Hook, `native/KCDMP/main_thread.cpp:151`, `:259`). Alles dort läuft im Frame des Spiels, bei ~76 fps sind das ~13 ms pro Frame (Live-FRAME 18.11.: `fps=76.9 frame_ms_mean=13.0`).
- **Vorhandene Budgets, nicht aushebeln:** Pipe-Batches haben eine Obergrenze von `kRequestsPerFrame = 8` und ein Zeitbudget von `kBudgetUs = 2000` µs (`native/KCDMP/wo218_rules.h:25–26`, `:80–81`). Edge-Lanes haben Realtime `1250` µs / 30 Elemente, Bulk `750` µs / 50 Elemente (`native/KCDMP/edge_lanes_rules.h:20–21`, `:105–108`). Die Zeitprüfung greift zwischen Elementen, das erste Element darf laufen. Ein einzelner teurer Handler kann das Budget überschreiten. `MAIN-COST` ordnet ab `kSlowOursUs = 1500` µs die teuerste Einzelaufgabe des Frames zu (`native/KCDMP/main_thread.cpp:101–104`, `:231`).
- **Agent:** Der Reader-Pfad für T3-Seitenkanäle macht keine HTTP-/native Arbeit. Der Host-Kalender für den Join wird im Reader gecacht, seine Anwendung läuft getrennt (`dotnet/KcdMp.Client/GameBridge.T3SideChannels.cs:42–51`). Bewegungsproben und zuverlässige Fakten gehören in getrennte Pfade (`AGENTS.md`). Warum: Ein langsamer Handler darf Positionen nicht stauen.
- **Lua:** Eine sehr große Datei, Arbeit pro Tick klein halten, Inventar-/Ledger-Arbeit portioniert (`docs/PAKET-D-FRAME-BUDGET.md`: höchstens 8 Operationen pro 50-ms-Fenster).

## 2. Regeln für den Hot Path (pro Frame, Paket, NPC)
1. **Keine blockierende I/O im Spiel-Thread:** keine Pipe-Writes, Dateien, HTTP oder `Sleep`. Produzenten kopieren nur in einen Puffer, ein Writer-Thread schreibt (`docs/PAKET-D-FRAME-BUDGET.md`). Warum: Eine wartende Pipe hält den ganzen Frame fest.
2. **Keine Allokation pro Frame:** in C++ kein `new`/`std::string`/wachsender `vector` im Tick, vorab reservieren oder Ring/Array nehmen. In C# kein LINQ, keine Closures, kein `string.Format`/Interpolation im Paketpfad außer gedrosselten Logs. Warum: GC- und Heap-Spitzen werden als Ruckler sichtbar.
3. **Keine Locks, die der Spiel-Thread länger hält oder auf die er wartet:** kurze Abschnitte, keine I/O unter Lock, lock-free bzw. Austausch per Index, wo es schon so gebaut ist. Warum: Lock-Konkurrenz mit Pipe/Agent blockiert den Frame.
4. **Arbeit begrenzen und verteilen:** feste Obergrenze pro Frame, Rest im nächsten Frame, nie „alles auf einmal“ nach einem Load oder Reconnect. Warum: Spitzen nach dem Join sind genau die Hänger, die Spieler bemerken.
5. **Logs gedrosselt:** pro Grund höchstens einmal pro Fenster, Zähler statt Zeile pro Ereignis. Warum: Logging im Hot Path kostet selbst Frames (der DLL-Log läuft im Spielprozess).
6. **Gedrosselte Engine-Aufrufe:** pro Frame so wenige Engine-Calls wie möglich, Ergebnisse cachen, wenn die Engine das erlaubt, und nie einen Vollscan pro Frame (Beispiel `UpdateSaveGameDescriptions`, `docs/PAKET-D-FRAME-BUDGET.md`).

H6 benutzt pro Todesversuch den vorhandenen Main-Pipe-Pfad. Der Tracker prüft einen Actor-Bucket. Die Nachbeobachtung läuft im Abstand von 40 ms bis zu 2,5 Sekunden. Für den einmaligen Abbruchnachweis folgen höchstens 500 ms. Nach unklarem Apply liest derselbe Versuch nur den Engine-Zustand. Der Release-Hook läuft ohne wartendes Lock oder `Stop`-Aufruf. `FRAME ours_us_p95` bleibt das Live-Gate.

## 3. Netz: Rate, Bandbreite, Latenz
- **Zustand = latest wins pro Körper** (`dotnet/KcdMp.Protocol/LinkScheduler.cs`), Ereignisse zuverlässig und geordnet. Zustände nicht in die zuverlässige Queue legen. Warum: Veraltete Positionen nachzuliefern erzeugt Gummiband-Effekte.
- **Change-gated senden + Heartbeat:** Änderungen senden. Der aktuelle BodyState2-Heartbeat sendet nach 1000 ms bei Bewegung oder gesetzten Bits (`dotnet/KcdMp.Protocol/ProtocolV8.cs:295`, `dotnet/KcdMp.Client/GameBridge.Wo121.cs:235–243`). H17/H4 plant einen Heartbeat auch für den vollständig inaktiven Zustand. Weitere Takte: `PlayerStateMinIntervalMs = 250` (`Protocol.cs:1325`), `RotStateIntervalMs = 80` (`HttpGameTransport.cs:35`), Host-Uhr regulär im 2-s-Takt (`dotnet/KcdMp.Protocol/ProtocolWo200.cs:40–41`), zusätzlich ein beim Join erfasster Snapshot (`dotnet/KcdMp.Client/GameBridge.Wo123.cs:517`). Ein neuer Sender nennt Takt und Bytes. Warum: Jede neue Rate geht mal 3–4 Spieler.
- **Relay-Tabelle:** Jeder neue Nachrichtentyp braucht eine Relay-Regel mit Größenlimit, das Gate ist `tools/Test-RelayTable.ps1` (läuft in `Build-Installer.ps1`). Warum: In 0.62.0 sind 463 W164-Frames still am Relay verschwunden (Kommentar `tools/Build-Installer.ps1:182–187`).
- **Latenz verstecken wie Seamless/NightCoop:** Jeder steuert die eigene Figur lokal ohne Warten, Partner werden aus Zeitstempeln interpoliert bzw. gehalten (Präsentationsuhr `rp_timeline*`, `kPoseHoldMs = 3000`, `native/KCDMP/rp_timeline_rules.h:16`), Treffer gehen als Aktion zur Autorität. Lokale Eingabe ohne Roundtrip ist der Kern von „fühlt sich nativ an“. NightCoop dokumentiert etwa 30 Hz für eine vom SDK eingespeiste Pose. Das SDK unterscheidet Streams, Latest-only und kurzfristige Ratenbursts. [NightCoop SDK](https://nightcoop.net/sdk-limits-and-versions.html)
- **3–4 Spieler:** Jede Rate pro Peer mal (n−1) rechnen, NPC-Updates nach Distanzbändern (`MP-LINK npc-bands`, `GameBridge.Wo213.cs:20`).

## 4. Messen statt raten (vorhandene Messzeilen)
| Messung | Wo / wie einschalten | Worauf achten |
|---|---|---|
| `FRAME window_s=60 fps= frame_ms_mean= frame_ms_worst= slow_frames= ours_us_mean= ours_us_max= ours_us_p95=` | DLL, immer an, alle 60 s (`native/KCDMP/main_thread.cpp:75–86`, `native/KCDMP/frame_meter.h:74–81`) | `ours_us_p95`/`ours_us_max` = gemessene DLL-Hook-Zeit je Frame in µs. `slow_frames` zählt Frame-Intervalle über 50 ms (`native/KCDMP/frame_meter.h:22`, `:46`). |
| `MAIN-COST …` pro Task, 10-s-Fenster | `mp_main_cost on` (`kdcmp/Data/Scripts/Startup/kdcmp.lua:30906`, `native/KCDMP/main_thread.cpp:396`) | Welcher Task den Frame frisst, langsamste Einzelaufgabe |
| `WO218-PIPE … subs_per_frame_max=` | DLL (`native/KCDMP/pipe_server.cpp:720`) | höchstens 8 Subs pro Frame |
| `MP-W207-Q lag_ms= waiting= by=` / `MP-W207-QK` | Agent mit verbundenem Partner. `Q` etwa jede Sekunde, `QK` alle 10 s (`dotnet/KcdMp.Client/GameBridge.Wo207.cs:27–38`). | Rückstand des Prozessors hinter dem Reader |
| `MP-LINK kinds/out/arrival/npc-bands/steam` | Agent mit aktivem Link: `kinds`, `out`, `arrival` und die Joiner-Zeile `npc-bands` alle 10 s. `steam` bei vorhandenem Steam-Status alle 2 s (`dotnet/KcdMp.Client/GameBridge.Wo213.cs:189–204`, `:340–348`). | Bytes/s und Nachrichten/s pro Art, Wartezeit am Link-Lock, Ankunftsabstände |
| `MP-T3-STATS` (`proc_stall_max_ms`, `proc_awaits_per_frame_max` …) | Agent (`GameBridge.T3.cs`), ausgewertet in `BRIEFING.md`/`ERGEBNIS.txt` | GRAU = nicht gemessen |
| Sim-Bericht `sim.md`, `sim.json` (Ausgabeordner von run-sim) | `tools/sim/run-sim ci /tmp/kct-sim --timeline both` | 2/3/4 Spieler, Profile good/typical/bad/awful. Die `Feel`-Grenzen gelten für die 2-Spieler-Zeilen `typical`, `bad` und `awful`. `typical` prüft u. a. Höhe p95 < 10 cm, < 1 Bruch/min, Treffer-/Dialog-p95 ≤ 150 ms und maximal 1 ms über `before` (`tools/sim/KcdMp.Sim/Feel.cs:43–53`). |
| Frame-Rate-Soak | Unter Windows mit Spiel und Wegwerfsave gemäß `tools/perf/README.md`. `python tools/perf/soak.py check` prüft den vorhandenen Nachweis beim öffentlichen Release-Build (`tools/Build-Installer.ps1:157–160`). | FPS mit vs. ohne Mod, sofern der Vergleich enthalten ist. |

Live-Beispiel 18.11.2026, 22:10:29 Europe/Berlin (Freund-PC, native Log): `frame_ms_worst=1017.3 ours_us_max=981641 ours_us_p95=3950`. Im Fenster erreichte die DLL-Hook-Zeit knapp 982 ms, ihr p95 rund 4 ms. Das Maximum des gesamten Frames und das Hook-Maximum können aus unterschiedlichen Frames stammen. Das 2-ms-Pipe-Budget gilt für einen Teil der gemessenen Arbeit. Die Ursache ist **unbelegt**. Ein Kandidat ist der enginegebundene Vollscan vor dem Load (`docs/PAKET-D-FRAME-BUDGET.md`). Mit `mp_main_cost on` belegen, bevor gefixt wird.

## 5. Pflicht-Check für jeden Diff (Bericht-Feld „Performance“)
- [ ] Läuft neuer Code im Spiel-Thread, pro Frame, pro Paket oder pro NPC? Wenn ja: Obergrenze, keine I/O, keine Allokation, kein langes Lock.
- [ ] Neue oder geänderte Senderate? Bytes × Takt × (n−1) Peers angeben. Relay-Regel vorhanden?
- [ ] Zustand latest-wins, Ereignis zuverlässig, nicht vermischt?
- [ ] Interpolation/Halten statt Teleport oder Timeout-Drop beim Partner?
- [ ] Gemessen: Sim-Werte vorher/nachher (`sim.md`) und, falls native, `native-contracts`-Ausgabe. Im Spiel nicht messbar? Dann „unverifiziert“ + die genaue Messzeile, die Lasses Test liefern soll (`FRAME`, `MAIN-COST`, `MP-W207-Q`, `MP-LINK`).
- [ ] Keine Verschlechterung ohne Begründung. Eine Verschlechterung, die für Korrektheit nötig ist, steht mit Zahl im Bericht.
