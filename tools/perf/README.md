# tools/perf — the frame-rate soak and the stat-stack reader (WO-151)

## Latenzabnahme mit zwei PCs

`latency_gate.py` wertet bereits korrelierte Beobachtungen aus. Die Rohbelege für dieselbe Build-ID, Szene, Hardware, Framerate und Netzphase gehören zur Abnahme. Sichtbare Bewegung und Aktionsbeginn brauchen synchronisiertes Video. Bei einzeln zugeordneten Treffern den Engine-Readback der Ziel-Vitals und dieselbe Kontakt-ID vom Capture bis zur Wirkung sichern. Die Fensterdeltas einer Trefferserie haben eine eigene Auswertung. Beginn und Rückkehr des TCP-Writes sowie die lokale Annahme durch Steam getrennt von der Reaktion auf dem Zielsystem aufzeichnen.

Pro Rolle, Transport, Teilnehmerzahl und Netzprofil einen Datensatz erfassen. Beim Rollentausch gewinnt der andere physische PC die Host-Rolle. Die JSON-Datei beschreibt zwei Messstationen `A` und `B`. Weitere Teilnehmer können mit eigenen Messpaaren erfasst werden. Verwendet werden `host_pc`, `players`, `transport`, `source`, `profile`, `pcs`, `clock`, `rtt_ms`, optional `jitter_ms` und `loss_percent` sowie `directions`.

- In `pcs.A` und `pcs.B` stehen `build`, `fps_min`, `ours_us_p95`, `rp_owner_n`, `owner_violations` und `extra_slow_frames`. Die Zurechnung zusätzlicher Frames über 50 ms beruht auf einem Szenenvergleich mit Vanilla und den betroffenen Einzel-Frames.
- `clock.method` beschreibt die Zeitbasis. `clock.error_ms` ist die obere Unsicherheit der ausgerichteten Differenz einschließlich Kameraauflösung oder Uhrenabgleich. WAN-Laufzeit und lokale Mod-Wartezeit separat aus den Rohstufen ausweisen.
- `directions.host_to_joiner` und `directions.joiner_to_host` enthalten je drei Reihen `movement_visible`, `action_visible` und `isolated_hit_effect`. Ein Eintrag je Versuch ist ein Objekt mit `id`, `capture_ms` und `effect_ms` auf derselben monotonen Skala. Für fehlende Zielbeobachtungen bleibt der Versuch erhalten und `effect_ms` ist `null`. Die IDs bezeichnen die zugehörigen Log- und Videomarker.

```sh
python tools/perf/latency_gate.py --build <Push-Build-ID> pc-a-host.json pc-b-host.json
```

Das Tool gibt p50, p95, p99, Maximum, Stichprobenzahl und fehlende Werte je Reihe aus. `profile=reference_lan` und `source=live` setzen die Referenzbedingungen mit RTT bis 10 ms und einer gemessenen Mindestbildrate von 60 FPS voraus. Der konservative p95 einschließlich Uhrenunsicherheit hat 40 ms als Obergrenze. Das Ziel unter 30 ms erscheint separat. Exitcode 1 markiert eine verletzte Schranke. Exitcode 2 markiert offene Nachweise, einschließlich eines fehlenden vergleichbaren Rollentauschs. Exitcode 0 bewertet die gelieferten Gruppen. Funktionale Spielparität und die Abdeckung weiterer Netzprofile brauchen eigene Rohbelege.

Why: 0.42.5–0.42.7 lost the frame rate in every fight and crowd (75 → 4.5 FPS within
minutes) and nothing measured it; the cause was a reflection argument that outlived its
value, faulting inside the game's stat getter thousands of times behind a silent guard
(docs/WO-148-findings.md §7). These tools make a decline visible before an installer.

Everything here is read-only on the game except the soak's scene (spawned, never saved,
removed at the end), and runs only on a **throwaway save** (the soak reads which save is
loaded from `kcd.log` first and refuses any other playline).

## The soak (required before every installer)

```
python tools/perf/soak.py run --label mod1 --throwaway playline4            # the game WITH the mod (pak + DLL)
python tools/perf/soak.py run --label van1 --throwaway playline4 --vanilla  # the same save, the game WITHOUT the mod
python tools/perf/soak.py verdict --mod tools/perf/runs/mod1.json --vanilla tools/perf/runs/van1.json --version <x.y.z>
```

* 10 minutes: three commoners 3 m around the player (no AI, never saved: two or more
  souls within 15 m, the 0.42.5 leak's trigger), from minute 3 a fight beside them (two AI
  commoners, the game's own attack interrupt re-sent every 15 s). No input, no focus.
* Every 10 s: the frame rate over those 10 s, the depth of the game's stat stack on its
  main thread, the `FAULT` lines in `kcdmp-native.mirror.log` since the start.
* PASS: the mod's last 2 minutes within 10 % of its first 2, both within 10 % of the same
  windows without the mod, the stack 0 in every row, no `FAULT` line, the code committed.
* `verdict` writes `tools/perf/soak-record.json` (commit it). It carries the git trees of
  the code the soak ran (`native/KCDMP`, `kdcmp/Data/Scripts`, `kdcmp/Data/Libs`,
  `kdcmp/mod.manifest`, the agent, the relay, the protocol); `tools/Build-Installer.ps1`
  runs `soak.py check` first and stops when the record is missing, says FAIL, or names
  other trees than the ones being built.
* The game without the mod: close the game, move `Mods\kdcmp` out of the Modding Tools
  folder (and do not inject the DLL), run, put it back.
* **The window in the same state for both runs** (the 0.43.0 soak: focused all the way).
  A window that loses focus mid-run caps the game's frame rate: an early 0.43.0 run fell
  from 68 to 25 FPS after the game was alt-tabbed out of, and stayed there after the scene
  was removed, while the DLL's own cost (`FRAME ... ours_us_mean`) stayed at 0.7 ms; the
  focused rerun held 71 FPS to the end.
* A dropped console connection is retried (the first 0.43.0 attempt died at 72 s on one).
* **WO-154 (0.45.0): the maintainer's call, mod only.** `verdict --mod <run.json> --mod-only`
  makes no comparison with the game without the mod and says so in the record; PASS needs the
  mod's last 2 minutes within 10 % of its first 2, the stack 0 in every row, no `FAULT` line,
  enough rows and the code committed.
* **WO-154: a joined partner** (`run --partner-ctl <peer.ctl>`): the same scene with a scripted
  partner (`tools/wo121/avatarpeer`; the relay, the host's agent and the peer started beside the
  run) who stands 4 m from the player, paces 4 m back and forth all the run and, from the fight
  on, lands an attributed hit on each pair's attacker every 15 s. `--record` keeps its verdict
  apart (`tools/perf/soak-record-partner.json`).

## The stat-stack reader

```
python tools/perf/statstack.py            # the running game, its main thread: depth=N
```

The stack must be 0 between frames. A depth that grows is a leak (WO-148 §7.3). The
offsets are the Modding Tools 1.5.5 RPGModule's; the reader checks that module's PE
identity and refuses any other build.
