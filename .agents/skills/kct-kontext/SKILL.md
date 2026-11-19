---
name: kct-kontext
description: "Projektkarte für „Kingdom Come: Together“ (KCD2-Koop-Mod). Zu Beginn JEDER Aufgabe in diesem Repo laden, bevor Code gelesen oder geändert wird, und immer dann, wenn unklar ist, welche Dateien für ein Thema (Join, Welt-Uhr, Laden, Körper, Kampf, Bogen, Dialog, Paket/Setup, Logs) zuständig sind und warum. Erschließt den Kontext gezielt; ersetzt das Lesen des Codes nicht."
---

# kct-kontext — Was das Projekt ist, wie es gebaut ist, wo was liegt

Stand: H22 auf dem grünen H0-Commit `a60c9a7`, Version `0.65.2` (`VERSION`), Protokoll `Protocol.Version = 10` (`dotnet/KcdMp.Protocol/Protocol.cs:800`). **Vor jeder Nutzung am aktuellen Tip nachprüfen.** Warum: Die Kette ist gestapelt, Zeilen wandern.

## So nutzt du diese Karte
1. Thema in der Tabelle „Thema → lesen“ suchen und **alle** genannten Dateien gründlich lesen, inklusive Aufrufer und Tests. Warum: passgenauer Code entsteht nur aus dem echten Kontext. Die Karte zeigt, wo du anfangen musst, sie ersetzt das Lesen nicht.
2. Eine Aussage aus dieser Karte gilt erst, wenn du sie am Code bestätigt hast. Warum: Hier steht nur das Ergebnis einer Prüfung am Tip, kein Beweis für deinen Stand.
3. Wenn du etwas änderst, das hier steht (Datei, Fluss, Falle), passt du diese Datei im selben PR an. Warum: Nur so bleibt der Kontext für das nächste Paket aktuell.

## Ziel (oberste Regel, `AGENTS.md`)
Ein Koop, der sich nativ anfühlt, auf dem Niveau von Elden Ring Seamless Co-op bzw. NightCoop. Zwei Spieler sind der Hauptfall, 3–4 müssen gut laufen. Truhen und Kräuter bleiben pro Spieler, Dialoge werden per Mehrheit entschieden, Friendly Fire ist an (`FriendlyFireDefault = true`, `GameBridge.Wo121.cs:42`). Am Ende gibt es **eine** Download-Datei (Drop-in-ZIP). Jede Aussage braucht Datei:Zeile, eine Logzeile oder einen CI-Lauf. Warum: Der Test vom 18.11. war in CI grün und im Spiel kaputt.

## Komponenten
| Teil | Ort | Rolle |
|---|---|---|
| Lua-Mod | `kdcmp/Data/Scripts/Startup/kdcmp.lua` (39 473 Zeilen) | Wird vom Spiel geladen: Menü-Übernahme, Spielstile, Engine-Aufrufe. Log in `kcd.log`. Lua-5.1-Grenze von 200 Locals (`tools/Test-WO110LuaLocals.ps1`) |
| Agent | `dotnet/KcdMp.Client` (137 Dateien zu `GameBridge` zusätzlich zum Kern `GameBridge.cs` mit 8 295 Zeilen) | Läuft pro Spieler. Spricht über Pipe/HTTP mit dem Spiel und über TCP oder Steam mit dem Relay. Verantwortlich für Join, Uhr, Weltsync und Kampfpfade |
| Protokoll | `dotnet/KcdMp.Protocol` (`Protocol.cs`, `ProtocolV8.cs`, `ProtocolWo200.cs`, `LinkScheduler.cs`) | Nachrichtentypen und Kinds, Körperblock, Link-Scheduler (für Zustände gilt „latest wins“) |
| Relay | `dotnet/KcdMp.Server` | Sitzungsprozess beim Host: TCP 7778, HTTP 5273 (`dotnet/KcdMp.Server/appsettings.json:57` und `:89`), Steam optional. Nicht die Spielwelt |
| Master | `dotnet/KcdMp.MasterServer` | Optionales Verzeichnis der Relays |
| Native DLL | `native/KCDMP` (287 Dateien) | Hooks im Spielprozess, Named Pipe (`pipe_server.cpp`), Main-Thread-Tasks (`main_thread.cpp`), Körpersteuerung (`body_drive.cpp`, `holder_rules.h`, `npc_drive.cpp`, `motion.cpp`), Präsentation (`rp_timeline*`) |
| Launcher | `KCDMP_launcher` (net8.0-windows, WPF/Blazor) | Startet Spiel, Agent und Relay. Rollenwahl, Connect-Gate, Menü-Treiber (`KCDMP_launcher/Pages/Home.Wo154.cs`, `KCDMP_launcher/Pages/Home.Wo159.cs`, `KCDMP_launcher/Models/MenuTakeover.cs`) |
| Setup | `dotnet/KcdMp.Setup`, `dotnet/KcdMp.SetupHost` | Erste Einrichtung, Checkliste („Ready! Host and Join unlocked“, `Home.Wo150.cs:97`) |
| LogPack | `dotnet/KcdMp.LogPack`, `dotnet/KcdMp.LogPack.Tool` | Log-Bundle `.tzst` mit `MANIFEST.txt`, `BRIEFING.md`, `ERGEBNIS.txt` (`Packer.cs`, `Briefing.cs`, `VerdictRules.cs`) |
| Paketierung | `tools/Build-Installer.ps1`, `tools/Publish-Release.ps1`, `tools/Package-DropIn.ps1`, `installer/` | Build, alle Gates, Installer, Drop-in-ZIP |
| Sim/Rig | `tools/sim/` (`run-sim`, `KcdMp.Sim`), `tools/rig/` | Netzsimulation ohne Spiel mit echtem Relay, 2/3/4 simulierte Spieler (`tools/sim/KcdMp.Sim/Program.cs`), Szenarien und Python-Tests |

## Autoritätsmodell (wer entscheidet)
- **Die Host-Welt ist die Welt.** Der Spielstand des Hosts ist maßgeblich. Eine Headless-Simulation des Spiels gibt es nicht (`AGENTS.md`). Der Joiner bekommt die Welt per Transfer und lädt sie.
- **Aktionen gehen zur Autorität, Werte kommen zurück** (`ProtocolWo200.cs:9–11`). Der Joiner schickt z. B. den Dialog-Out-Port, der Host spielt ihn ab und schickt Queststatus, Vitals, Reittier und Uhr zurück.
- **Host = Schadens-/Weltautorität:** `_isDamageAuthority` (Agent), Rollenwerte `RoleNone=0, RoleHost=1, RoleJoiner=2` (`Wo200.cs:45`). Die Host-Hoheit ist in `GameBridge.HostAuthority.cs`, `HostAuthorityLifecycle.cs` und `dotnet/KcdMp.Protocol/HostAuthorityFailure.cs` umgesetzt.
- **Single-Owner:** Jeder Zustand hat genau einen Schreiber. Für Körper gelten Halterregeln (`native/KCDMP/holder_rules.h`: `select` verweigert den Wechsel, solange `lease || engineActive`, `:123`). Das Gate `owner_violations=0` muss halten (`docs/ABNAHME-S2.md:37`).
- **Zwei Kanäle:** Bewegung ist „newest sample“ und darf verworfen werden. Tode, Treffer, Quests, Inventar und Zeit sind zuverlässig und geordnet, beides nie in einem FIFO (`AGENTS.md`). Die Debug-HTTP-Konsole und `kcd.log` sind kein Multiplayer-Transport.

## Hauptflüsse
**Join (Joiner im Hauptmenü → gemeinsame Welt)**
1. Launcher: `SessionLaunchPlan` hält gewählte Rolle und Ziel. Der Direct-Test liefert eine Diagnose und kann ein Join-Ziel merken. Erst die Launcher-Wahl oder bestätigte Menüanfrage setzt den Plan und das passende Gate. Der Menütreiber erzeugt daraus `role=host|join` über `MenuTakeoverRule.ModelCall`. Bei einem Wechsel im Hauptmenü wartet Lua auf die konkrete `roleAck`; der Treiber prüft den aktuellen Plan beim Ausführen von Laden oder Join erneut. Das gleiche Host-Bit steuert `--hosting`. [Rollenpfad](../../../docs/ABNAHME-H22.md)
2. Host: Join-Hold, dann gefrorener Kalender **vor** dem Angebot: `SendHostCalendarAsync(j.Joiner, frozen: true, joinId)` (`GameBridge.Wo123.cs:517`). Bei Fehlschlag wird kein Angebot gesendet (`:520`).
3. Übertragung: Kalender als `W164Down` mit Kind 18 = `Protocol.W164Clock` (`ProtocolWo200.cs:42`).
4. Joiner-Reader (`GameBridge.cs:4872–4881`): Bei aktiver Ladesequenz markiert `LoadSequenceWorldFrame` die Kategorie `LoadScoped`, einschließlich `W164Clock` (`GameBridge.LoadSequence.cs:233–234`). Danach folgen `LoadSequenceFrameCurrent`, `T3TryDeferSideChannel` und für verbleibende Frames `if (!WorldSyncReady && inFrame.LoadScoped) continue` (`GameBridge.cs:4881`). Der Helfer verarbeitet erkannte Uhrpakete bereits vor diesem allgemeinen Gate. Er prüft Weltstempel und Absender und ruft erst dann `CacheHostCalendar` auf (`GameBridge.T3SideChannels.cs:43–51`). Welche Abzweigung im Live-Test die Uhr verworfen hat, muss H20 über den echten Reader belegen.
5. `CacheHostCalendar` prüft Rolle, Absender, Parse-Ergebnis und Generation (`GameBridge.HostCalendar.cs:50–59`). Es setzt `_hostCalendarBootstrap` nur bei `IsJoinSnapshot` (`GameBridge.HostCalendar.cs:63`) und zählt `clock_in` (`:65`).
6. Welt empfangen, verifizieren und platzieren (`playlineN/mpworld<joinId>.whs`), `_retainedJoinWorlds.Keep` (`GameBridge.Wo124.cs:739`).
7. `PrepareHostCalendarAsync` wartet 5 s auf `ReadyForJoin` (`GameBridge.HostCalendar.cs:141–161`). Kommt nichts, folgt `no current host calendar -- load not issued` (`:159`) → `ABORTED (clock-unavailable)` (`GameBridge.Wo124.cs:789–792`) → **H20**.
8. Abbruch: `AbortJoinerJoinAsync` (`GameBridge.Wo124.cs:1131–1158`) → `RemovePlacedWorldAsync` lässt die Welt liegen, solange `MayRemove` nicht `Released` ist (`JoinWorldFiles.cs:25–28`, Log `GameBridge.Wo124.cs:867`). Auto-Retry nach `+30 s` (`:1149`) belegt die nächste Playline, bis „All five save slots are full“ (`Wo175Coop.cs:33`) → **H19**.

**WorldSync / Ladesequenz:** Phasen `Menu, Offer, Load, Reconstruct, Settle, Play, Exit` (`WorldLoadSequence.cs:4`). Bei aktivem WorldSync und außerhalb der Baseline-Anwendung ist `WorldSyncReady` im Menü falsch. `WorldSyncCurrent` schließt die Phasen `Menu`/`Exit` aus (`GameBridge.WorldSync.cs:35–43`). Alles, was vor dem Laden nötig ist, darf deshalb nicht hinter diesem Gate hängen.

**Körper / Live-Zustand:** Body State 2 = 12-Byte-Block plus Events (`ProtocolV8.cs:12–26`), Heartbeat `BodyState2HeartbeatMs = 1000` (`:295`). Der Sender meldet Änderungen, auch auf null. Den Heartbeat von unveränderten Zuständen sendet er nur bei `nonZero` (`GameBridge.Wo121.cs:235–243`). Der Empfänger setzt den Zeitstempel des Zustands ab `kStateFreshMs = 1500` zurück (`rp_timeline_rules.h:16`, `:177–178`) → **H17/H4**. Ziel von H17/H4: Auch „aus“ wiederholen und den letzten Zustand bis zum Verbindungsausfall anwenden.

**Kampf / Bogen:** Darstellung über `GameBridge.PresentationCombat.cs` und `CombatPipe.PresentationCombat.cs`, native `rp_combat*`/`rp_timeline_action*`. Bogenhand: `GameBridge.Wo214Hands.cs`, `GameBridge.Wo201Ranged.cs`, native `wo214*`, `wo201_ranged.cpp`. Den gemessenen Fernkampfschaden verarbeiten `GameBridge.P6Missile.cs` und `p6_missile.cpp`. Der Animationspfad zeigt die Aktion. Lanes: Bogen #46, Nahkampf #47 (gleiche Datei wie H15/H16 → abstimmen).

**Dialog:** `ApplyConfirmedDialogVoteAsync` (`GameBridge.Wo204Conv.cs:197`). Mit `WorldSyncEffective` laufen bestätigte Stimmen über den WorldSync-Pfad. Ohne ihn nimmt `Wo204ConvOnVote` die Legacy-Stimme für die Anzeige an (`GameBridge.Wo204Conv.cs:365–367`).

**Start-Saves / Paket:** `StartSavePath = "start-save"` (`AppModels.cs:233`). `Publish-Release.ps1:173` liefert ohne `assets/start-save/<stil>` keine Saves aus. Fehlt ein Save, ist der Spielstil im Menü grau (`kdcmp.lua:30390–30392`). Prüfung: `tools/Validate-StartSave.ps1` → **H21** (Weg 2: geprüfte Saves im privaten Repo).

## Thema → lesen (und warum)
| Thema | Gründlich lesen | Warum |
|---|---|---|
| Rolle/Menü | `KCDMP_launcher/Pages/Home.H22.cs`, `KCDMP_launcher/Pages/Home.Wo154.cs`, `KCDMP_launcher/Pages/Home.Wo159.cs`, `KCDMP_launcher/Models/LauncherSessionRole.cs`, `KCDMP_launcher/Models/MenuTakeover.cs`, `kdcmp.lua` um `MP_StartGame`, `dotnet/KcdMp.Client.Tests/H22LauncherSessionRoleTests.cs` und `tools/Test-WO159Synthetic.lua` | Erfolgreiche Auswahl, Gate, Menübestätigung und ausgeführte Aktion für beide Rollen |
| Join/Uhr | `GameBridge.Wo123.cs` (Host), `GameBridge.Wo124.cs` (Joiner), `GameBridge.HostCalendar.cs`, `GameBridge.T3SideChannels.cs`, `GameBridge.cs` Reader um `:4860–4890`, Tests `HostCalendarTests`, `T3SideChannelsTests`, `Wo123Tests`, `Wo124Tests`, `T4BotRelayTests` | Gemeinsam prüfen, welche Reader-, Scope- und Cache-Entscheidung den Bootstrap erreicht |
| Slots | `JoinWorldFiles.cs`, `GameBridge.Wo124.cs` (`AbortJoinerJoinAsync`, `RemovePlacedWorldAsync`), `Wo175Coop.cs`, `JoinWorldFilesTests` | Freigabe- und Aufräumlogik |
| Laden/WorldSync | `WorldLoadSequence.cs`, `GameBridge.LoadSequence.cs`, `GameBridge.WorldSync.cs`, `CombatPipe.LoadSequence.cs`, native `load_sequence*` | Phasen-Gates entscheiden, welche Pakete gelten |
| Körper | `GameBridge.Wo121.cs`, `ProtocolV8.cs`, native `body_drive.cpp`, `holder_rules.h`, `npc_drive.cpp`, `motion.cpp`, `rp_timeline_rules.h` | Ein Owner pro Körper, Live-Zustand |
| Leistung | `native/KCDMP/main_thread.cpp`, `native/KCDMP/main_thread.h`, `native/KCDMP/frame_meter.h`, `native/KCDMP/pipe_server.cpp`, `GameBridge.Wo207.cs`, `GameBridge.Wo213.cs`, `GameBridge.Wo218.cs`, `GameBridge.Wo218Pipe.cs`, `docs/PAKET-D-FRAME-BUDGET.md` | Frame-Budget und Messzeilen (siehe Skill `kct-performance`) |
| Design | `AGENTS.md`, `docs/DESIGN-AUDIT.md` (Abschnitt 4.10 = Vertrag für Kopien), `docs/ARCHITECTURE-shared-world.md` (aktueller Autoritätsgrundsatz im Abschnitt „The standing rule“), `docs/DECISIONS-coop-design.md`, `docs/NETWORKING.md`, `docs/WO-98-log-format.md` | Architekturgrundsatz, verbindliche Koop-Regeln und historische Entscheidungen |

**Lane 2 · H6/H15/H16 (#54):** `GameBridge.NpcDeathDispatch.cs` übergibt benannte, fatale und GUID-Todesmeldungen an denselben nativen `ApplyDeath`-Pfad in `pipe_server.cpp`. `QueueAction` reserviert vorher im `combat_actions`-Tracker. `npc_death_release.h` hält die Körperbindung über `Pending`, erlaubt die Mutation nach bestätigter Aktionsfreigabe und gibt nach einem nachweislich prämutativen Abbruch nur die lebende Originalbindung wieder frei. Der Scheduler `rp_timeline_actions.cpp` protokolliert den terminalen Ausgang. Eine frische akzeptierte Partnerpose in derselben Bindung kann bei fehlender nativer Zieluhr denselben typisierten Auftrag einmal freigeben. `ActionRowCatalog.cs` kann den unmarkierten Perfect Block bei nachgewiesenem Katalogkontext als gewöhnlichen Block darstellen. [Integrationsbelege](../../../docs/LANE2-H6-INTEGRATION.md)

**Lane 2 · Restfälle nach `824293a7`:** Die H6-Vorbereitung unterscheidet einen Fehler vor der ersten Mutation von einer möglicherweise teilweise ausgeführten Vorbereitung. Nach Teilwirkung beobachtet der Ledger nur denselben Körper. Beim Friendly Fire erfasst `hits.cpp` die ursprüngliche Messzeit für Nahkampf und Geschoss. Der Agent bildet `0x44` nur, wenn Ziel-Lebensnachweis und Körperbindung zu dieser Zeit bereits beobachtet waren. `0x97` ist die lokale DLL-/Agent-Schnittstelle. Relay und native Zielanwendung prüfen weiterhin den bereits gebundenen Kontakt. [Zwei-PC-Nachweise](../../../docs/ABNAHME-LANE2-NAHKAMPF.md)

## Bekannte Fallen (Live-Test 18.11.2026, 22:09–22:40 Europe/Berlin)
1. **Stilles Verwerfen:** `GameBridge.cs:4881` und Ablehnungen in `T3TryDeferSideChannel` (`GameBridge.T3SideChannels.cs:47–50`) haben dort keine Logzeile. Der Live-Test zeigte `MP-LINK in=w164:18` und `clock_in=0`. H20 muss den konkreten Ablehnungsgrund nachweisen. Regel: Jeder Drop loggt seinen Grund (gedrosselt).
2. **LoadScoped-Seitenkanäle in Phase Menu:** Alles, was vor dem Laden gebraucht wird (Uhr-Snapshot), darf nicht am WorldSync-Gate hängen.
3. **Slot-Leck:** Ein Abbruch vor dem Laden gibt die Welt nicht frei, der Retry nimmt die nächste Playline.
4. **Fehlende Start-Saves:** Der Build war grün, aber `start saves installed: none`.
5. **Rollen-Lock vor H22:** Der Launcher leitete die Startrolle aus dem Relay ab und graute im Menü die Gegenrolle aus. H22 wählt die Rolle pro Versuch und bestätigt Änderungen im Hauptmenü vor Lade- oder Join-Aktionen. Ein weiterlaufendes lokales Relay ändert die gewählte Join-Rolle nicht. Der Rollentausch auf zwei PCs bleibt offen.
6. **Tests prüften Teile, nicht den Ablauf:** `HostCalendarTests` (Parse), `T3SideChannelsTests` (Decode/Queue), `T4BotRelayTests` (Relay). Keiner fuhr Host↔Joiner durch den echten Reader im Menü.
7. **Zeitabhängige Tests:** Die Testuhr `dotnet/KcdMp.Client.Tests/ManualFrameTime.cs` steuert die Frist für zwei HostAuthority-Tests, den abgeschlossenen Processor-Frame sowie die Prüfungen auf späte HTTP-Schreibversuche und Wiederaufnahme nach einem Timeout. Die Tests lösen die Frist an einem belegten Wartepunkt aus. Der Test für regulären Abschluss lässt den Frame mit kontrollierter Uhr fertiglaufen. Bei weiteren Timing-Fehlern den Ablauf reproduzieren und die Assertions erhalten. [Nachweis](../../../docs/H0-SELBSTKONTROLLE.md)

## Git-Regeln
Gestapelte Draft-PRs, nichts direkt auf `main`, kein Force-Push, Rebase, Amend oder Reset, kein Merge ohne Lasses OK. Vor weiteren Änderungen den aktuellen Tip der PR-Kette prüfen.
