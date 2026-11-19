---
name: kct-fix-workflow
description: "Definition of Done für jede Code-Änderung in „Kingdom Come: Together“. Laden, sobald ein Bug gefixt, ein Paket (H*, Bogen_*, Nahkampf_*) umgesetzt oder ein PR/Bericht vorbereitet wird. Regelt Beleg → roter Test → Fix → grün → Volllauf → Performance-Check → Selbst-Review → Bericht. Nicht für reine Log-Auswertung (dafür kct-log-analyse)."
---

# kct-fix-workflow — Erst belegen, dann rot, dann fixen, dann beweisen

Gilt für jedes Paket. „Fertig“ heißt, dass alle Schritte erfüllt sind. Fehlt einer, lautet der Status „teilweise“, mit Grund. Warum: Am 18.11. war CI grün, und zwei Spieler konnten trotzdem nicht zusammen spielen.

## 0. Kontext laden
- Lade zuerst `kct-kontext` und lies alle Dateien, die dort für dein Thema stehen, gründlich, mit Aufrufern und vorhandenen Tests. Warum: Ein Fix, der den Ablauf nicht kennt, baut den nächsten Bug ein.
- Prüfe jede Zeilenangabe aus dem Prompt am aktuellen Tip (`git log -1`, `sed -n`/`rg -n`). Warum: Die Kette ist gestapelt, Zeilen wandern.

## 1. Ursache belegen (vor dem Code)
- Ursache = `Datei:Zeile` + Logzeile mit Zeitstempel (Europe/Berlin) + Erklärung, warum diese Zeile den Fehler erzeugt. Warum: Ein Fix auf Vermutung behebt oft das falsche Symptom.
- Bei mehreren Kandidaten die Ursache als „unbelegt“ markieren. Ein gezieltes Diagnose-Log oder ein Test am echten Pfad soll zeigen, welcher Zweig auftritt. Warum: Raten kostet einen weiteren Zwei-PC-Abend.
- Prüfe, ob der Fehler auch Host↔Joiner-vertauscht und mit 3–4 Spielern auftritt. Warum: 2 Spieler sind der Hauptfall, 3–4 müssen auch gehen.

## 2. Test zuerst rot
- Schreib einen Test, der den **echten Codepfad bis zu seiner Auswirkung** fährt, beispielsweise den Relay-Reader in `dotnet/KcdMp.Client/GameBridge.cs` mit Phase `Menu` und `WorldSyncReady=false`. Warum: Die getrennten Parse-, Queue- und Tabellentests waren am 18.11. grün.
- Lass ihn gegen den alten Code laufen: Er muss **rot** sein. Die rote Ausgabe kommt in den Bericht. Warum: Ein Test, der nie rot war, beweist nichts.
- Fahr beide Rollen, Abbruch- und Wiederholungsfälle (verspätet, verloren, 5× Abbruch) und wo sinnvoll 2/3/4 Spieler (`tools/sim` fährt 2, 3 und 4, `tools/sim/KcdMp.Sim/Program.cs`). Warum: Fehler stecken in den Rändern.
- Verboten: `Skip`, `[Ignore]`, abgeschwächte Asserts, längere Timeouts, um grün zu werden, Retry gegen Flakes. Warum: Das versteckt den Fehler, statt ihn zu beheben.
- C#-Tests kommen in die passenden Testprojekte unter `dotnet/`. Native Tests in `native/tests/` werden in `native/tests/CMakeLists.txt` eingetragen. Portable native Tests für den Linux-Sim-Job zusätzlich in `tools/sim/run-sim` aufnehmen. `tools/Build-Installer.ps1` führt alle PowerShell-Synthetic-Suiten unter `tools/` aus und verlangt mindestens 85 Suiten.

## 3. Fix
- Der effizienteste korrekte Weg, keine Zwischenlösung. Nenne, was der Fix **löscht** (Löschliste). Warum: Noch ein Schalter neben dem alten Weg ist das, was den Code fragil gemacht hat (`AGENTS.md`).
- Ein Owner pro Zustand. Zuverlässige Fakten (Tod, Treffer, Quest, Inventar, Zeit) laufen geordnet, Bewegung „newest wins“. Warum: Das Autoritätsmodell ist die Grundlage für synchrone Welten.
- Live-Zustand statt Momentaufnahme: den letzten Zustand wiederholen (auch „aus“). Den Zustand halten, solange aktuelle Meldungen kommen. Ein neuerer Zustand, Session-Reset oder echter Verbindungsverlust beendet ihn. Warum: So bleiben Hand, Block und Bogen sichtbar, solange sie gehalten werden.
- Jeder verworfene Pfad loggt einmal seinen Grund (gedrosselt). Warum: Das stille Verwerfen bei H20 war ohne Log nicht zu finden.
- Keine Engine-Behauptung ohne Beleg. Was nur im echten Spiel prüfbar ist, wird als „unverifiziert“ markiert. Warum: So steht es in `AGENTS.md`, und erfundenes Engine-Verhalten ist der teuerste Fehler.

## 4. Grün + Volllauf vor jedem Push
Unter Linux im Codex-Container ausführen (.NET 8 SDK, `c++`, `python3`, `pwsh`):
```bash
dotnet test dotnet/KcdMp.Client.Tests/KcdMp.Client.Tests.csproj -c Release
dotnet test dotnet/KcdMp.Relay.Tests/KcdMp.Relay.Tests.csproj -c Release
dotnet test dotnet/KcdMp.Setup.Tests/KcdMp.Setup.Tests.csproj -c Release
dotnet test dotnet/KcdMp.LogPack.Tests/KcdMp.LogPack.Tests.csproj -c Release
tools/sim/run-sim ci /tmp/kct-sim --timeline both   # = CI-Job "sim": native Regelsuiten, Rig-Python, Solo/Guided, Sim 2/3/4 Spieler
```
Die Client-Tests werden über ihr eigenes Projekt aufgerufen. `tools/Required-TestResults.ps1` prüft im Windows-Build die vier TRX-Berichte, die Mindestzahlen und das native Ergebnis. `tools/sim/run-required-python.py` führt jede Python-Pflichtsuite getrennt aus und meldet Skips, leere Läufe und Testfehler als Fehler.

Der Windows-Build braucht MSVC und Inno Setup. `tools/Build-Installer.ps1` führt das Relay-Table-Gate, die Synthetic-Suiten, die statischen Checks, `KCDMP_NativeTests.exe` und die beiden verpflichtenden Savegame-Python-Suiten mit demselben strengen Runner aus. Der separate Spieltest des Save-Roundtrips setzt laufende Modding Tools voraus. Danach kommen `tools/Package-DropIn.ps1` und „Verify drop-in contents“ über `tools/Verify-DropIn.ps1`. Kommandos und ausdrücklich markierte Übergangsprüfungen H20/H19/H21 stehen in `.github/workflows/build-windows.yml` und `docs/H0-SELBSTKONTROLLE.md`.
- Wenn ein benötigter lokaler Lauf fehlt, den Grund und den belegten Stand im Bericht nennen und die Vorgabe in `AGENTS.md` beachten. Die ausdrücklich freigegebene GitHub-Windows-Vorabprüfung gilt für H0. Einen erfolgreichen CI-Lauf mit Link als solchen ausweisen.
- Testanzahl je Suite mit der Basis vergleichen (Stand CI 44383488125: 2705 Client, 288 Relay, 285 LogPack, 110 Setup, 12488 nativ). Sinkt eine Zahl, wird das begründet. Warum: Verschwundene Tests sind stille Löcher.

## 5. Performance-Check (Pflicht)
Lade `kct-performance` und beantworte die Checkliste dort für deinen Diff: Main-Thread, Hot Path, Paketrate, Messzeile. Warum: Ein korrekter Fix, der ruckelt, erfüllt die oberste Regel nicht.

## 6. Selbst-Review des eigenen Diffs (`git diff <basis>...HEAD` und `git diff HEAD`)
Geh Punkt für Punkt durch und fixe jede Schwäche sofort, statt sie nur zu nennen:
- [ ] Neue `return`/`continue`/`catch {}` ohne Log und Grund?
- [ ] Race-/Timeout-Pfade: Was passiert, wenn das Paket zu früh, zu spät, doppelt oder gar nicht kommt?
- [ ] Aufräumen bei Abbruch: Slots, Dateien, Leases, Halter, Timer, Fader. Nichts bleibt liegen.
- [ ] Host **und** Joiner, Rollen vertauscht, 2 und 3–4 Spieler.
- [ ] Wiederverbindung/Session-Reset: Alte Generationen und Epochen werden verworfen.
- [ ] Alter Weg wirklich entfernt (Löschliste erfüllt), kein zweiter Owner, kein zweiter Schadenspfad.
- [ ] Spielregeln intakt: eigene Truhen/Kräuter, Mehrheit im Dialog, Friendly Fire an.
- [ ] Keine Secrets, keine rohen Steam-IDs (nur gehasht) im Code oder Log.
- [ ] `kct-kontext` aktualisiert, wenn Dateien, Flüsse oder Fallen sich geändert haben.

## 7. Spieltest-Bezug
Nenne die Logzeile, die im echten Zwei-PC-Test den Erfolg beweist. Für den Kalender zählen `clock_in>0` in `MP-W200-STATS` und derselbe `join`-Bezeichner in `pre-update latch armed before load`, `ready sent for join` und `resume join=… reason=ready`. Für Start-Saves zählt `MP-W159 start saves installed: soldier, adviser, scout`. Für Abbrüche zählen die verbleibenden Join-Dateien und unveränderte persönliche Saves. Prüfe in beiden Rollen `owner_violations=0` bei `rp_owner_n>0`. Warum: So kann Lasses Test den Fix bestätigen oder widerlegen.

## 8. Bericht (Pflichtfelder, Deutsch, kurz)
```
Paket: H.. – <Titel>          Status: fertig | teilweise (<Grund>)
Branch / PR / Commit-SHA: …   Basis: …
CI-Lauf: <Link>  Artifact: <Link>
Ursache: Datei:Zeile + Logzeile (Zeit Europe/Berlin)   unbelegt: …
Test rot→grün: <Testname> – rot: <Ausgabe/Lauf>, grün: <Lauf>
Volllauf: Suiten + Anzahl (Δ zur Basis), Sim-Ergebnis, lokal nicht gelaufen: …
Performance: Main-Thread/Hot Path/Rate – Messzeile + Wert oder „unverifiziert, misst im Spiel <Zeile>“
Selbst-Review: alle Punkte ✓ / gefunden+behoben: …
Löschliste: entfernt …   Single-Owner: …
Vergleich: wie NightCoop/Seamless/… es lösen, 1–2 Sätze mit Quelle
Spieltest-Beweis: Logzeile …
Skills aktualisiert: ja/nein, was (kct-kontext, kct-log-analyse, kct-performance)
Offene Risiken: ehrlich
```
Git: gestapelter Draft-PR, kein Force-Push, Rebase, Amend oder Reset, nichts auf `main`, kein Merge ohne Lasses OK.
