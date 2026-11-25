---
name: kct-log-analyse
description: "Log-Bundles von Lasses Spieltests für „Kingdom Come: Together“ auswerten (.tzst-Bundles, agent*.log, app*.log, relay*.log, kcd.log, kcdmp-native*.log, MP-*-Zeilen). Laden, wenn Logs oder ein Testbericht vorliegen, wenn eine Ursache aus einem Live-Test belegt werden soll oder wenn Behauptung, Code und Log „Fleck für Fleck“ gegeneinander geprüft werden. Nicht zum Schreiben von Fixes (dafür kct-fix-workflow)."
---

# kct-log-analyse — Lasses Logs lesen und belegen

Ziel: Jede Aussage über den Spieltest stützt sich auf **Logzeile + Datei:Zeile im Code**, und die Zeiten stehen in Europe/Berlin. Warum: Nur so lassen sich Fixes und der nächste Zwei-PC-Test darauf aufbauen.

## 1. Bundle öffnen
- Ein Bundle ist `<name>.tzst` oder `<name>-partNofM.tzst` (tar + zstd, jede Datei ein eigener Frame, `dotnet/KcdMp.LogPack/Packer.cs`). Entpacken mit `tar --zstd -xf <part>` (so steht es in `MANIFEST.txt`). Vertrauenswürdige Teile aus demselben Bundle zusammen in einen Arbeitsordner entpacken. `MANIFEST.txt`, `BRIEFING.md` und `ERGEBNIS.txt` stehen in Teil 1.
- Ein Ordner pro PC/Start. Oft bekommst du Ordner mit Hash-Namen, z. B. `837e8bcb157e/`. Warum: Ohne die Zuordnung PC → Rolle sind alle weiteren Schlüsse wertlos.
- Lies zuerst `MANIFEST.txt`: Dateien, Größen, sha256, Zusammenfassungen. Identische Dateien sind nur einmal gespeichert. Die Manifestnotiz `same content as` verweist auf den gespeicherten Eintrag (`dotnet/KcdMp.LogPack/Packer.cs:142`, `:241`). Zusammengefasste Läufe stehen als Zeile `[kct-pack] …` mit Anzahl. `MP-MARK`-, `FAULT`- und `STATS`-/Summary-Zeilen bleiben immer vollständig, ebenso alles bis 5 Minuten um eine `mark_odd`-Markierung (`dotnet/KcdMp.LogPack/Collapser.cs:30`). Warum: Eine scheinbar fehlende Zeile kann zusammengefasst sein.
- Danach `BRIEFING.md` und `ERGEBNIS.txt` (die T1-Ampel aus `dotnet/KcdMp.LogPack/VerdictRules.cs`). GRAU heißt „keine Messung“, nicht „ok“. Warum: Ein „Gesamt GRÜN“ mit fast nur GRAU beweist nichts (Beispiel 18.11.: GRÜN, Rolle „unbekannt“).
- Werkzeug: `dotnet run --project dotnet/KcdMp.LogPack.Tool -- brief <ordner>` gibt das Briefing auf stdout aus. `dotnet run --project dotnet/KcdMp.LogPack.Tool -- anim-compare <host> <joiner> --no-write` vergleicht vorhandene Animationsproben ohne Änderungen an den Bundles. Ohne `--no-write` versucht das Werkzeug, den Abschnitt in beide bestehenden `BRIEFING.md` zu schreiben (`dotnet/KcdMp.LogPack.Tool/Program.cs:11–42`).

## 2. Dateien und Zeitformat
| Datei | Quelle | Zeitformat |
|---|---|---|
| `agent.log`, `agent.prev*.log` | Agent `KcdMp.Client` | `HH:mm:ss.fff m=<mono-ms> off=<±ms\|?> <text>`: Ortszeit des PCs, `off` = Relay-Uhr minus PC-Uhr, in ms (`dotnet/KcdMp.Client/TeeTextWriter.cs:68–71`, `docs/WO-98-log-format.md`) |
| `app<JJJJMMTT>[_NNN].log` | Launcher (Serilog) | `2026-11-18 22:10:19.406 +01:00 [INF] …` |
| `relay<JJJJMMTT>[_NNN].log` | Relay `KcdMp.Server` | wie `app`, mit Offset |
| `kcd.log`, `history/launch-*/kcd.log`, `logbackups/*.log` | Spiel + Lua `mp_log` | Kopf `Log Started at JJJJ-MM-TT HH:MM:SS`. `mp_log` hängt `t=<s>.<ms>` aus `os.clock()` an (`kdcmp/Data/Scripts/Startup/kdcmp.lua:200–210`). Siehe `SessionRule.LaunchTime` in `dotnet/KcdMp.LogPack/Packer.cs:22–30` und `docs/WO-98-log-format.md`. |
| `kcdmp-native.mirror[.N].log` | DLL | `[HH:MM:SS.mmm] t<thread> L<launch-id> …` (`native/KCDMP/log.h:116–119`) |
| `settings.json` | Launcher-Einstellungen | `GamePath`, `HostAllowSteam`, `StartSavePath`, `MenuTakeover` … |
| `crashes/crash-*/agent-statements.txt`, `anim/kcdmp-anim.jsonl` | Absturzberichte, Animations-Probe | – |
- Zeiten ohne Offset sind Ortszeit des jeweiligen PCs (bei Lasse Europe/Berlin). Konvertiere Zeitstempel mit Offset anhand ihres Datums nach Europe/Berlin und nenne die Zeitzone im Bericht. Kennzeichne unbekannte Zeitzonen ohne Offset. Für den Abgleich zweier Agenten mit bekannter Uhrkorrektur gilt `Hostzeit = Joinerzeit + off_joiner - off_host` (`docs/WO-98-log-format.md`). Bei `off=?` bleibt der exakte Uhrabgleich offen.
- `m=` ist eine monotone Uhr pro Prozess und eignet sich für Abstände innerhalb eines Logs, nicht zwischen PCs.

## 3. PC und Rolle zuordnen (immer zuerst)
1. Build: `MP-BUILD version=… build=…` (`dotnet/KcdMp.Client/Program.cs:95`) und `KCDMP.dll attached … build=` (`native/KCDMP/dllmain.cpp:87`). Prüfe die Build-ID beider PCs. Bei verschiedenen Builds den Unterschied als eigenen Befund festhalten und Codebelege je Build ermitteln.
2. PC: Windows-Benutzer/`GamePath` (`settings.json`, Pfade im Log), Steam-Name im Relay-Log (z. B. MORTIS = Lasse, Sayl = Freund am 18.11.). Die Relay-ID identifiziert den Client in der Sitzung. Prüfe die Rolle über `MP-HOST-CLAIM` und `MP-AUTHORITY-OWNER` (`dotnet/KcdMp.Server/Features/ClientHandling/ClientSession.cs:1132`, `dotnet/KcdMp.Server/Features/ClientHandling/ClientHandler.cs:368`).
3. Rolle **pro Sitzung**, nicht pro PC: `MP-W159 menu driver on (host|joiner)` (`Home.Wo159.cs:147`), `MP-LAUNCH connect gate closed (host: …|joiner: …)` (`Home.Wo154.cs:271`), `MP-W200-STATS role=1` Host, `role=2` Joiner, `role=0` ohne Rolle (`dotnet/KcdMp.Client/Wo200.cs:45`, `dotnet/KcdMp.Client/GameBridge.Wo200.cs:97`), `MP-LINK kinds who=host|joiner` (`dotnet/KcdMp.Client/GameBridge.Wo213.cs:61`). Die Launcher-Zeilen belegen die Auswahl. Relay und Agent belegen die wirksame Autorität. Warum: Am 18.11. hat Lasses PC um 22:33 gehostet und vorher gejoint.
4. Zeitachse bauen: pro Sitzung Start, Join-ID (`0x…`), Abbruch und Ende, beide PCs nebeneinander.

## 4. Wichtige Zeilen (Bedeutung → Codeort)
| Zeile | Bedeutung | Code |
|---|---|---|
| `setup: Ready! Host and Join unlocked` | Setup fertig | `Home.Wo150.cs:97` |
| `MP-LAUNCH session role selected (host|join) source=… gamePid=… menuSeq=…` | Der Launcher hat Plan und passendes Gate übernommen. Bei einem Wechsel aus dem Spiel dieselbe Sequenz in `WO159-MENU role receipt seq=… target=… accepted=true` suchen. Anschließend den Ladebefehl und Host-Connect oder die beim Agent eingereichte Join-Wahl belegen. | `KCDMP_launcher/Pages/Home.H22.cs`, `tools/rig/scen_session_role.py` |
| `MP-W159 start saves installed: none\|soldier, adviser, scout` | Start-Saves vorhanden? | `Home.Wo159.cs:87` |
| `MP-JOIN host: join 0x… world=playlineN/… verify=ok` | Host hat den Save für das Angebot verifiziert. Den weiteren Versand separat prüfen. | `dotnet/KcdMp.Client/GameBridge.Wo123.cs:525` |
| `MP-JOIN host: join 0x…: native calendar unreadable -- offer not sent` | Host-Uhr nicht lesbar | `GameBridge.Wo123.cs:520` |
| `MP-CALENDAR … snapshot=sent/repeated`, `bootstrap accepted`, `receipt=accepted` | Host-Send, Cache beim Gast im Hauptmenü und zurückgekehrte Quittung sind getrennte Belege. Dieselbe Join-ID und Welt-Epoche zuordnen. | `dotnet/KcdMp.Client/GameBridge.JoinCalendarHost.cs`, `dotnet/KcdMp.Client/GameBridge.HostCalendar.cs` |
| `MP-CALENDAR … reason=different-world/inactive-attempt/world-not-frozen`, `bootstrap retired` | Ablehnungen nach Join-ID und Sende-Epoche einordnen. Host-Abbruch und Host-Verlust sperren den Latch-Pfad. Dass der Gast den Latch gesetzt hat, belegt erst `pre-update latch armed before load`. | `dotnet/KcdMp.Client/GameBridge.HostCalendar.cs` |
| `MP-CALENDAR … waiting for the frozen host snapshot`, `frame dropped … reason=`, `native calendar latch refused` | Vor dem Ladebefehl wartet der Gast auf die Uhr. Ein abgewiesener Snapshot oder ein abgewiesener nativer Latch hat einen eigenen Grund. Historisches `no current host calendar` kennzeichnet den alten Fünf-Sekunden-Abbruch. | `dotnet/KcdMp.Client/GameBridge.HostCalendar.cs`, `docs/ABNAHME-H20.md` |
| `MP-JOIN joiner: join 0x… ABORTED (<grund>)` | Abbruch dieser Join-ID. Verbleib wird am Zeilenende geloggt, die gesonderte Zeile `ABORTED after the load` bezeichnet den Abbruch nach dem Ladepfad. | `dotnet/KcdMp.Client/GameBridge.Wo124.cs:1152`, `:1163` |
| `… mpworld….whs deleted=on`, `… is kept until no load can be reading it`, `automatic retry blocked for world …` | Für dieselbe Join-ID zwischen freiem Slot, Schutz vor Engine-Zugriff und blockiertem Auto-Retry unterscheiden. Historisches `no join=in or player cancel yet` war der Abbruch mit liegen gebliebener Datei. | `dotnet/KcdMp.Client/GameBridge.Wo124.cs`, `dotnet/KcdMp.Client/GameBridge.Wo154Join.cs` |
| `All five save slots are full…` | Alle Join-Plätze voll | `Wo175Coop.cs:33` |
| `MP-JOIN host: … aborted while loading`, `resume … phase_at_end=` | Host-Sicht des Abbruchs | `GameBridge.Wo123.cs:640`, `:709` |
| `MP-HOST-ABSENT pause/resume`, `MP-AUTHORITY-OWNER … reason=host-absent`, `MP-RELAY-DROPS … host-absent` | Dieselbe Welt-Epoche und Run-ID bei Rückkehr prüfen. Im Relay kennzeichnet `why=superseded` den Wechsel zur neuen Verbindung, `why=disconnect` einen physischen Verbindungsabgang. Vor der ersten Weltmeldung zeigt `reason=awaiting-host-claim` einen wartenden Rollenentscheid. Relay-Pause, Spielhinweis, bestätigter nativer Hold und sichere Heimkehr sind getrennte Belege. Der Relay-Zähler erfasst auch verworfene Welt-, Kampf- und Dialognachrichten. | `ClientHandler.cs`, `GameBridge.Wo124.cs`, `GameBridge.Wo211.cs` |
| `MP-AUTHORITY-OWNER … reason=awaiting-early-host`, `MP-HOST-ABSENT restarted`, `MP-HOST-ABSENT restart-offer` | Bei einer gewählten Autorität mit explizitem Host-Claim oder bestätigtem Modus auf dieselbe zurückkehrende Identität prüfen, auch am dedizierten Relay. Nach einem Neustart die alte und neue Run-ID zwischen Relay und Gast abgleichen. Der nächste Engine-Load beim Gast folgt erst nach seiner ausdrücklichen Rückkehr in die eigene Welt und dem erneuten Join. Die privaten Belegdaten erscheinen in keinem dieser Einträge. | `ClientHandler.cs`, `GameBridge.HostRestart.cs` |
| `F4-RESOURCES`, `F4-HANDLE-TYPES` | Absolute Testhost-Threads, Handles und Timer je Runde vom selben Mess-Thread. Zyklus 0 ist die Basismessung nach dem Warmup. Jede Messung folgt den nativen Übergaben für Load, Kalender und Autorität. Nach fünf Runden gelten +4 für Prozess-Threads und +1 für aktive Timer. Windows protokolliert Typen sowie lebende, terminierte und fremde Thread-Referenzen als Anzahlen. | `JoinCalendarIntegrationTests.F4.cs`, `F4WindowsHandleTypes.cs`, `tools/Required-TestResults.ps1` |
| `F4-POOL-WARMUP` | `primed` zählt die aufgewärmten Worker und `reached` die erreichte Pool-Größe. Unter Windows gehört ein abgeschlossener lokaler TCP-Austausch zum Warmup. `worker_min` und `worker_max` sind die ursprünglichen Prozessgrenzen während der Messung. | `JoinCalendarIntegrationTests.F4.cs` |
| `F4-HANDLE-ORIGINS`, `F4-HANDLE-BUDGET`, `F4-HANDLE-TWIN` | Ein geöffnetes und geschlossenes Prüfhandle belegt die Reihenfolge der Windows-Historie. Die Probe prüft die jüngste Öffnung und die konkrete Herkunft. Belegte Laufzeitzuordnungen sind vollständige Winsock-Event-/Thread-Paare je aktiver OS-Thread und API, das einzelne Event des statischen .NET-Regex-Monitors und der CLR-Start eines lebenden Pool-Workers mit seinen zwei Events. `before` und `after` sind die absoluten Prozesswerte. `compared_after` darf um +4 wachsen. Die Gegenprobe hält zehn Dateihandles über fünf Leaves offen und erwartet die Ablehnung am Handle-Budget. | `JoinCalendarIntegrationTests.F4.cs`, `F4WindowsHandleTrace.cs` |
| `F4-TIMER-DRAIN`, `F4-TIMER-ORIGIN`, `F4-TIMER-TWIN` | Eine Folgemessung wartet nachweislich zu `ProcessorFrameScope` gehörende Einmalfristen bis 1.950 ms bis zum Ende ihrer Freigabe ab. Ein Fristablauf lässt die Prüfung scheitern. Der Endwert zählt alle aktiven Timer. Der Leave-Gegentest hält fünf Timer bis zur Budgetprüfung und erwartet deren Ablehnung. | `JoinCalendarIntegrationTests.F4.cs`, `F4ProcessorDeadlines.cs` |
| `MP-W200-STATS … clock_out= clock_in=` | Erfolgreiche Kalender-Sendezyklen des Hosts bzw. akzeptierte und **gecachte** Samples des Joiners. Anwendung im Spiel separat prüfen. | `dotnet/KcdMp.Client/GameBridge.Wo200Values.cs:168`, `dotnet/KcdMp.Client/GameBridge.HostCalendar.cs:44–46`, `:65` |
| `MP-LINK kinds who=… in=<kind>:<B/s>/<msg/s>` | Eingangsraten nach Nachrichtentyp. `w164:18` ist der Typname für die Host-Uhr, der vollständige Eintrag enthält danach B/s und msg/s. | `dotnet/KcdMp.Protocol/ProtocolWo213.cs:113–118`, `:254–258`, `dotnet/KcdMp.Protocol/ProtocolWo200.cs:41` |
| `MP-T3-STATS origin=agent role=…` | Prozessorwerte ohne `section`, weitere Werte mit `section=load`, `section=events` oder `section=recovery` | `dotnet/KcdMp.Client/GameBridge.T3.cs:280–284`, `dotnet/KcdMp.Client/GameBridge.WorldRecoveryStats.cs:94` |
| `MP-W207-Q lag_ms= waiting= …` | Rückstand der Verarbeitungs-Queue mit Partner etwa jede Sekunde, `MP-W207-QK` mit Details alle 10 s. | `dotnet/KcdMp.Client/GameBridge.Wo207.cs:27–38` |
| `FRAME window_s=60 … frame_ms_worst= ours_us_max= ours_us_p95=` | Frame-Kosten der Mod (DLL, immer an) | `native/KCDMP/frame_meter.h:74–81`, `native/KCDMP/main_thread.cpp:75–86` |
| `MP-H14-DRIVE … state=quarantined … attempts=`, `WO221-DRIVE end … proof=` | Bei derselben EID zählen `stop-accepted`, `new-controller` oder `entity-removed` als bestätigte Freigabe. Beim nächsten gültigen Bewegungsframe den aktiven Owner und die sichtbare Pose prüfen. | `native/KCDMP/body_drive.cpp`, `native/KCDMP/holder_rules.h` |
| `[version] ghost N is on release …` | Release des Partners | `GameBridge.cs:5241` |
Such die Zeile mit `rg -n "<fester Textteil>"` im zum Log gehörigen Build. Fehlt ihr Codebeleg, kennzeichne die Zuordnung als unbelegt. Logtext kann aus einem älteren Build stammen.

Bei H6 zeigen `WO221-ACTEND` mit `attempt`, `receipt`, `waiting` und `active` sowie `MP-LANE2-H6` mit `attempt`, `eid`, `stage`, `lifecycle` und `connection` denselben Versuch. `receipt=1` bestätigt die Aktionsfreigabe. `ready` → `death-dispatched` → `applied` belegt die Reihenfolge und den bestätigten Engine-Zustand. Das Gate `tools/rig/scen_lane2_h6.py` prüft beide Rollenzuordnungen.

Für H21 den zugehörigen `SaveGame`-Abschluss mit `MP-H21-SNAPSHOT result=paired`, Run, Generation, `host_seq` und dem gekürzten Hash abgleichen. Bei der Heimkehr belegen `MP-H21-HOME result=held` und das anschließende `result=confirmed loaded=True` den passenden privaten Load. Bei ersetztem Ziel sind `result=menu` und null private Load-Aufträge zu erwarten.

`MP-HENRY exit file retained reason=no-terminal-engine-receipt` kennzeichnet eine offene Ladeanforderung für eine eigene Menüdatei. `MP-H21-HOME place-check result=menu-exit` belegt bei fehlender Abschlussmeldung die aktuelle Bestätigung des Hauptmenüs. Danach die Freigabe `exit cleanup reservation_released=on reason=engine-terminal` und die aufgehobene Save-Sperre zum selben Rückweg prüfen. `result=pending` lässt die Sperre aktiv. `MP-HENRY sweep retained reason=legacy-ledger-has-no-bound-engine-file` kennzeichnet erhaltene Altdateien ohne gebundenen Nachweis.

`stage=apply-uncertain` kennzeichnet die weitere reine Beobachtung desselben H6-Versuchs nach einem unklaren Engine-Aufruf. `stage=aborted-before-apply` steht für die bestätigte Wiederfreigabe eines lebenden Körpers vor Guard- und Todesmutation. `stage=aborted-unadmitted` verbraucht eine zuvor nicht angenommene Versuch-ID ohne Freigabe an einem Körper.

`prepare-unchanged` belegt die Fehlergrenze vor der ersten Vorbereitungsmutation. Erst ein nachfolgendes `aborted-before-apply` belegt die Wiederfreigabe. Nach `prepare-uncertain` bleibt selbst `prepare-observed-dead` eine Zustandsbeobachtung ohne Todesgutschrift für den Versuch.

`WO121-HITS` über `measurement_id` an `MP-FF dir=out` anbinden. `publication=bounded` kennzeichnet ein laufendes Messfenster. `publication=final` kennzeichnet das Ende der Beobachtung. Positive Deltas und `missile` beziehen sich auf das ganze Fenster. Für eindeutig einzelne Kontakte getrennte Messwerte führen. `restore_incomplete` zählt Rücksetzversuche mit fehlender oder unvollständiger Rücklesung für HP und Ausdauer im Fenster. Die expliziten Tickfelder verwenden, da der Pipe-Writer zeitversetzt loggt. `trace_dropped` zeigt eine Lücke in der Messreihe.

`MP-LANE2-FF-TIME` über `source`, `contact_seq` und `recipient_life` innerhalb derselben Sitzung verbinden. `*-write-start` markiert den Aufruf des Transportadapters. `*-write-complete` bestätigt dessen abgeschlossenen TCP-Write, `*-sdk-accepted` dessen lokale Annahme durch das Steam-SDK. Den tatsächlichen Empfang über die passende `*-read`-Zeile belegen. Je Prozess die erfassten `local_qpc_ms` für Write-Dauern verwenden. Bei Client-Zeilen ist `relay_ms` eine Schätzung mit `clock_rtt_ms`. `relay_ms=0` zeigt einen fehlenden oder für diese Zeitmarke nicht verwendbaren Abgleich an. `MP-LANE2-COMBAT event=contact source=pvp` liefert die lokale Ziel-Queue und Engine-Zeit zusammen mit dem Vital-Readback. [Messplan](../../../docs/ABNAHME-LANE2-NAHKAMPF.md)

## 5. „Fleck für Fleck“-Audit
Für jedes Feature (z. B. Bogen: Hand → Zielen → Schuss → Pfeil → Treffer; Join: Rolle → Uhr → Welt → Laden → In-Welt) eine Zeile:
```
Schritt | Behauptung (Bericht/PR) | Code (Datei:Zeile) | Log Host (Zeit) | Log Joiner (Zeit) | Urteil
```
- Urteil ist nur eines von: **belegt**, **widerlegt**, **unbelegt** (fehlt Log/Code), **nicht getestet**. Warum: Grüne Tests oder eine Behauptung allein sind kein Beweis.
- „Paket gesendet“ ≠ „empfangen“ ≠ „verarbeitet“. Prüfe alle drei getrennt (Beispiel 18.11.: `clock_out` steigt, der Typ `w164:18` erscheint in `in=`, `clock_in=0`). Warum: Genau dazwischen lag der Fehler.
- Fehlt eine Logzeile, wo der Code eine schreiben müsste: Der Pfad lief nicht, **oder** er ist still. Nenne beide Möglichkeiten und lies den Code.
- Zähle Wiederholungen (gleiche Folge bei mehreren Join-IDs). Warum: Ein Einzelfall und ein systematischer Fehler brauchen verschiedene Fixes.
- Vergleich mit dem letzten funktionierenden Build, falls Logs davon da sind (z. B. 17.11. `pre-update latch armed`). Warum: Daraus ergibt sich die Regression.

## 6. Ergebnisformat
```
Sitzung(en): Datum, Zeitraum Europe/Berlin, Build-ID beider PCs
Rollen: PC → Steam-Name → Rolle je Sitzung
Befunde (je: Symptom, Logzeilen mit Zeit, Codeort, Ursache belegt/unbelegt)
Fleck-für-Fleck-Tabelle
Warum CI das nicht fing (welcher Test fehlt)
Nächster Schritt: Paket/Fix + Logzeile, die den Erfolg im nächsten Test beweist
```
Keine Secrets, Tokens oder rohen Steam-IDs aus den Logs zitieren. Warum: Die Logs gehen in Chats und Prompts.
