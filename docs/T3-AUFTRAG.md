<!-- Vom Nutzer am 18.11.2026 zur vollstaendigen Ausfuehrung uebernommener Auftrag; Status siehe T3-STATUS.md. -->

# Codex All-in-Auftrag: Kingdom Come: Together — Fixes aus Test 3

Inoffizieller Koop-Mod für Kingdom Come: Deliverance 2. Repo: https://github.com/Cxsmxs-afk/KingdomCome-Together

**Stand 18.11.2026 (Europe/Berlin):** Nichts gemergt. Spitze ist PR #17 `codex/loot-ownership-polish` (`339d78c`), gestapelt auf #16 → #15 → #14 → #13 → #12 → #11.

**Ziel:** Koop, der sich nativ anfühlt (wie Elden Ring Seamless Co-op). Zwei Spieler zuerst, so flüssig und synchron wie möglich. Modding Tools bleiben Pflicht. DLL ersetzen ist ok, die Installation bleibt einfach (Zip drüber, Launcher starten).

**Test 3 (0.65.2), was kaputt war:** Beim Joiner sind alle NPCs unsichtbar, und der Ladebildschirm endet mit „Loading screen timeouted while still in post load reconstruction!“. Aufheben friert den Joiner ein („waiting for host“). Nach dem Tod Teleport weit weg und Falltod. Kampf ruckelt. Leichen und Grab-Marker fehlen teils. Der Bogen geht nicht, Spieler sind teils unsichtbar.

## Arbeitsweise (All-in, eine Session)

- Arbeite die Pakete **nacheinander in dieser Session** ab: **P0 → P1 → P3 → P4 → P5 → P6 → P2** (P2 nur unter Bedingung, siehe dort).
- Pro Paket eine eigene Branch und ein eigener PR, gestapelt:
  - P0 `codex/t3-p0-processor-stall` auf `origin/codex/loot-ownership-polish`, PR gegen diese Branch.
  - Jedes weitere Paket auf der Branch des vorigen, PR gegen diese.
  - Branch-Namen: `codex/t3-p1-intellect-gate`, `codex/t3-p3-pickup-freeze`, `codex/t3-p4-respawn-wake`, `codex/t3-p5-corpses-marker`, `codex/t3-p6-bow`, `codex/t3-p2-frame-budget`.
- **Wiederaufnahme:** Die Verbindung bricht manchmal ab. Zu Beginn (und nach jedem Abbruch) auf GitHub nach den Branches und PRs `codex/t3-p*` schauen.
  - Fertige Pakete (PR offen, CI grün) nicht neu bauen.
  - Ein angefangenes Paket auf seiner Branch fortsetzen, dann mit dem nächsten unfertigen weitermachen.
- Ein Paket ist erst fertig, wenn CI im PR grün ist. Dann kurzer deutscher Bericht und direkt das nächste Paket. Am Ende eine Gesamtzusammenfassung.
- **Verboten:** mergen, force-push, rebase/amend/reset, irgendetwas nach `main`. Keine Secrets in Repo, Logs oder Chat. UI-Texte ohne das Wort „KI“.
- **CI pro PR grün:** `.github/workflows/build-windows.yml`.
  - Das Artifact `kingdom-come-together` enthält `KingdomComeTogether-DropIn.zip`. Darin liegt `install-verify.txt` mit erster Zeile `PASS`.
  - `dotnet test` (Relay/Client) und die nativen Tests nicht still überspringen.
  - `Wo207Fault_a_step_rule_with_an_occurrence_waits_for_that_time` darf als Timing-Flake einmal wiederholt werden.
- **Neue Schalter:** jeweils ein Konsolenbefehl `mp_…` mit dem genannten Standardwert.
- **Zähler:** Alle neuen Zähler kommen in eine 60-s-Zeile **`MP-T3-STATS`** (Agent und DLL, beide Rollen) und ins Collect-Logs-Bundle.
- Was nur im Spiel beweisbar ist, offen so benennen und nicht als erledigt melden.

---

## P0 — Frame-Prozessor des Joiners darf nie hängen (höchste Priorität)

**Belege:**
- S2: `23:54:15 WO166-WEATHER joiner … profile=foggy_storm` → `23:54:40 MP-W206 STALL the frame processor has been on one frame for 25,0 s: type 117 (0x75)`.
- S3: genauso, 00:02:22 → 00:02:37.
- Danach kommt nie `STALL over`. `MP-W207-Q waiting=12` bleibt eingefroren, die Queue läuft bis 426 s Verzug.
- `MP-WO136-STATS holding=0 held=226 replayed=0`, `MP-NPCWRITE-STATUS bound=0 binds_ok=0` die ganze Sitzung.
- Host-Antworten auf Pickups kommen nie an (`MP-WO134-STATS host_in=0`).
- S1 bekam im Load nur `profile=-` und hing nicht („load hold released after 235.5 s: 121 held frame(s) replayed“).

**Auftrag:**
1. Den blockierenden `await` im W164-Pfad finden und im Bericht belegen. Kandidaten: `Wo164OnFrameAsync` → `Wo166OnHostWeatherAsync`, `Wo151OnHostWeatherAsync`/`Wo151WeatherGateAsync`, `ApplyWeatherAsync`, `ExecuteNowAsync`, `ReceiveHostCalendarAsync`/`_hostCalendarGate`, CombatPipe `EnterGateAsync`/`EnsureConnectedAsync`.
2. Jeder Frame-Handler im Prozessor bekommt ein hartes Zeitlimit (Standard 2 s, `mp_proc_frame_timeout <ms>`). Danach wird der Frame aufgegeben, geloggt und gezählt, und der Prozessor läuft weiter.
3. Wetter-, Uhr- und andere Nebenkanal-Frames während Loading/Settle nur merken (der neueste gewinnt) und nach „Gameplay started“ + Settle anwenden.
4. `Wo136ReplayIfReleased` läuft unabhängig vom Prozessor (eigener Tick), damit gehaltene Frames immer nachgespielt werden.

**Zähler:** `proc_stalls`, `proc_stall_max_ms`, `proc_frames_abandoned`, `deferred_sidechannel`, `w136_held`, `w136_replayed`.

**Akzeptanz:**
- Test mit simuliert hängendem W164-Handler während des Loads: Der Replay passiert, und die Queue fällt nach Gameplay-Start innerhalb von 5 s unter 1 s Lag.
- `proc_stall_max_ms ≤ 2000`.

## P1 — Intellect-Gate erst nach Load/Rekonstruktion, NPCs nie pauschal verstecken

**Belege:**
- `WO216-CFG role 0 -> 2` wird schon vor dem Load gesetzt (23:28:30 / 23:52:19 / 00:00:07; `GameBridge.HostAuthority.cs:66-78`).
- Das Gate sperrt `C_NPCManager::UpdateIntellects` komplett (`wo216.cpp:79-113`, `wo216_rules.h:190`).
- S1: `hidden=446 bound=60` (23:32:52), `hidden=499 bound=5` (23:47:54).
- Nach dem ersten Gameplay vergehen jeweils 44–60 s bis zum echten „Gameplay started“. Das passt zum Post-Load-Timeout.
- `hide_unbound` (`wo216.cpp:411`, `:540`) versteckt jeden NPC, den der Host nicht gebunden hat.

**Auftrag:**
1. Rolle 2 vor dem Load nur vormerken. Das Gate greift erst nach „Gameplay started“, wenn der Ladebildschirm weg ist und die Post-Load-Rekonstruktion fertig ist bzw. mindestens 5 s Settle vorbei sind. Das gilt nach jedem Load neu.
2. Wenn sich Rekonstruktion und Smart-Entity-Init innerhalb von `UpdateIntellects` erkennen lassen, diese Arbeit auch bei aktivem Gate durchlassen. Sonst dokumentieren, warum nicht.
3. Schalter `mp_host_authority on|off` (Standard on). Bei off wird der Join nicht verweigert, sondern läuft mit dem Verhalten vor PR #15.
4. Failsafe für `hide_unbound`:
   - Solange weniger als 30 % der nahen NPCs (≤150 m) gebunden sind oder `bound==0` ist, wird nichts versteckt.
   - Ein versteckter NPC wird nach spätestens 10 s ohne Bindung wieder gezeigt.
5. Tote Körper und Körper mit unfertiger Rekonstruktion nie verstecken.

**Zähler:** `gate_armed_at_s` (relativ zu Gameplay-Start), `decision_blocked`, `decision_blocked_during_load` (muss 0 sein), `postload_timeouts`, `npc_near`, `npc_bound`, `npc_hidden`, `hide_failsafe_on`, `unhidden_unbound_10s`.

**Akzeptanz:**
- Tests: Rolle 2 vor dem Load → Gate inaktiv bis Gameplay + Settle. `decision_blocked_during_load==0`.
- Bei bound 0 → hidden 0.
- `mp_host_authority off` führt nicht zu „join refused“.
- Im Spiel offen: Meldung weg, NPCs sichtbar.

## P3 — Aufheben friert den Joiner nie ein

**Belege:**
- Joiner S2: `WO134-ITEM ask tok=1/2/3 … alias_zachrana_huntingSword000225` → jedes Mal „The host didn't answer“, dazu `WO134-BODY open … no answer from the host`.
- Der Host hat jedes Mal geantwortet (`WO134-ITEM host-take … from=1 -> unknown`, gleiche Abstände). Die Antworten steckten in der hängenden Queue (siehe P0).
- `MP-WO134 tick failed … HttpClient.Timeout of 0,8 seconds` taucht dutzendfach auf.
- Weitere Stellen:
  - Der 4-s-Timeout in `W134.loop` steht hinter `if KCD2MP_W136Held() then return end`.
  - PR #16 F1 hat den 3-s-Fader-Release entfernt.
  - PR #17 antwortet `busy`, wenn `h.peers[peer]` fehlt.

**Auftrag:**
1. Loot-Antworten (LootHostUp) bekommen eine eigene schnelle Spur und warten nie hinter NPC-/Ghost-Frames.
2. Timeouts für Item/Open/Take laufen immer, auch während des Holds. Danach wird die Use-Aktion sauber beendet und eine Meldung gezeigt.
3. Lose Welt-Items optimistisch lokal aufheben, der Host bestätigt nachträglich. Bei `gone` gibt es einen Rollback und eine Meldung. Bei `unknown` (Item gibt es beim Host nicht) lokal behalten, keine Frage-Schleife.
4. Input-Wächter: Sind `no_input`/`movement` länger als 3 s gesperrt, ohne dass ein bekannter Eigentümer sie hält (Szene, Dialog, Menü, Ladebildschirm), wird die Sperre gelöst. Dazu `T3-INPUT-RELEASE owner=? held_s=` loggen.
5. Leichen-Take ohne vorheriges Open: Den Peer implizit eintragen statt `busy` zu senden.
6. Kein Auto-Rejoin, solange die Welt unverändert ist. Jeden Rejoin-Auslöser mit Grund loggen.

**Zähler:** `pickup_asks`, `pickup_optimistic`, `pickup_rollbacks`, `pickup_unknown_kept`, `ask_timeouts`, `ask_latency_ms_p95`, `take_busy_refusals`, `input_held_max_s`, `input_releases`, `auto_rejoins_in_world`.

**Akzeptanz:**
- Lua-Synthetiktests (`tools/Test-WO134Synthetic.lua`, `Test-WO217Synthetic.lua`): Pickup im Hold → Timeout ≤ 5 s, Input frei.
- `gone` → Rollback. `unknown` → behalten.
- Take ohne Open → grant.

## P4 — Respawn-Wake: kein Absetzen weit weg, kein Falltod

**Belege:**
- Kein `MP-LEASH pulled`. Der „Teleport“ ist der `MP-RESPAWN`-Wake nach dem Tod:
  - 23:38:05: Wake 151 m entfernt, Fallhöhe 7,3 m.
  - 23:44:18: 170 m, 16,4 m.
  - 23:48:43: 168 m, 11,9 m.
- Tod 2 hatte `recent_attackers=0 hostiles=0 bleeding=yes` direkt neben dem Partner. Tod 3 hatte keine Angreifer und `bleeding=no`.
- Die Leash meldet `flying 115 m/s` um 23:43:21, Ursache unbekannt.
- Code: `respawn.cpp:676`, `:706-750`; Leash `GameBridge.Wo114.cs:599-623`, `join_native.cpp:70-108`; Unstuck `GameBridge.Wo164.cs:633`.

**Auftrag:**
1. Wake-Punkt:
   - Nahe beim Partner (≤ 30 m), außerhalb des Kampfs, auf dem Boden.
   - Fallhöhe ≤ 2 m, sonst Bodenprobe oder einen anderen Punkt nehmen.
   - Die Partnerposition darf höchstens 2 s alt sein.
2. Nach dem Wake: volle Gesundheit, Blutung entfernt, 5 s Schadensschutz. Fallschaden halten, bis der Spieler 1 s am Boden steht (max. 20 s).
3. Klären und im Bericht nennen, woran Tod 2 und 3 ohne Angreifer starben. Ab jetzt jede Todesursache loggen.
4. Leash und Unstuck: kein automatischer Teleport im Kampf oder bei KO. Zielposition höchstens 2 s alt, „exact spot“ nur bei Fallhöhe ≤ 2 m. Jeder Teleport loggt Quelle, Distanz, Fallhöhe und Positionsalter. Den 115-m/s-Fall mitloggen.
5. Ein Tod innerhalb von 10 s nach einem Mod-Teleport wird als `T3-FALLDEATH-AFTER-TP` geloggt.

**Zähler:** `tp_total`, `tp_by_source{leash,respawn,unstuck,join}`, `wake_dist_m_max`, `wake_drop_m_max`, `tp_blocked_combat`, `tp_refused_stale`, `tp_refused_drop`, `death_cause{hit,fall,bleed,other}`, `falldeath_after_tp` (Ziel 0).

**Akzeptanz:** Unit-Tests für die Regeln:
- Wake ≤ 30 m und Drop ≤ 2 m.
- Kampf blockt den Teleport, eine alte Position blockt.
- Nach dem Wake gilt der Schutz.

## P5 — Leichen bleiben, Marker auch ohne Grab

**Belege:**
- Leichen kamen in S1/S2 an: `WO217-CORPSE here=shown dead=true`, `MP-OWNERDEATH applied=dead placed=true`. In S3 fehlten sie wegen des Hängers (P0).
- Beim ersten Tod gab es ein Grab mit `marker=yes`. Spätere Tode: „nothing to bury -- no grave“ → kein Marker.
- Code: `respawn_actions.cpp:498-509`, `:669-673`.

**Auftrag:**
1. Leichen auf einer Kopie bleiben sichtbar und sind von Hide/Census ausgenommen.
2. Todesort immer markieren: ohne Grab oder AI-Linkable ein einfacher Kartenmarker. Ein Fehler überspringt nur diesen Marker und schaltet nicht alle Marker der Session ab.

**Zähler:** `corpses_visible`, `corpse_hidden_blocked`, `grave_markers_added`, `grave_marker_fallbacks`, `markers_off_session` (Ziel 0).

**Akzeptanz:** Tests: dead → nie hide. Ohne Grab wird ein Marker gesetzt.

## P6 — Bogen: Waffentyp mitsenden, Joiner sendet eigene Schüsse

**Belege:**
- Host: `WO201-RANGED out reloading/firing [n=1..13]`.
- Joiner: `WO201-RANGED in … weapon=unknown -> kcd2mp_0 'Load' tags='l_crossbow+medium+goatsfoot'` / `'Fire' tags='l_crossbow'`. Der Bogen wird also als Armbrust abgespielt (`GameBridge.Wo201Ranged.cs:50-70`, `_w201FigureProp` unbekannt).
- Der Joiner sendet keine eigenen `out`-Schritte.

**Auftrag:**
1. Den Waffentyp (bow/crossbow, Item-Klasse) im RangedStep mitsenden. Bei `unknown` nichts abspielen statt Armbrust.
2. Der Joiner sendet Spannen, Zielen und Schuss seines eigenen Bogens wie der Host. Der Treffer läuft über den bestehenden Host-Verdict-Pfad.

**Zähler:** `ranged_out`, `ranged_in`, `ranged_kind_unknown` (Ziel 0), `ranged_bow_played`, `ranged_hits_verdict`.

**Akzeptanz:** Tests: Ein Bogen-Step spielt Bogen-Tags, `unknown` spielt nichts, und der Joiner erzeugt `out`-Steps. Im Spiel offen.

## P2 — Main-Thread-Budget (nur unter Bedingung, zuletzt)

**Belege:** Die Pipe macht im Schnitt 2,0–2,7 Aufgaben pro Frame, höchstens 9. `subs_per_frame_max=8 carry_frames=1`. Das Ruckeln kam vor allem vom Hänger (P0).

**Bedingung:** Nur bauen, wenn nach P0–P6 in Tests oder Sim der 8-pro-Frame-Deckel (`wo218_rules.h` `kRequestsPerFrame=8`) oder der Head-of-Line-`break` in `main_thread.cpp` messbar Echtzeit-Arbeit verzögert. Sonst nur einen Bericht mit Messwerten liefern und keinen PR.

**Wenn gebaut:**
- Eine Echtzeit-Spur (Pose, Kampf, Treffer, KO, Schuss) mit Vorrang (≥ 32 Aufgaben bzw. 3 ms pro Frame) und eine Hintergrund-Spur.
- Kein Head-of-Line-Abbruch. Veraltete Samples ersetzen statt stauen.
- Schalter `mp_frame_budget <n>|off`.

**Zähler:** `rt_age_ms_p95`, `rt_age_ms_max`, `bg_age_ms_p95`, `hol_skips`, `subs_per_frame_p95`.

**Akzeptanz:** Bei 30 Echtzeit- + 50 Hintergrund-Requests pro Frame `rt_age_ms_p95 < 34`. `tools/sim/run-sim ci` grün.

---

## Bericht nach jedem Paket (deutsch, kurz)

Branch, PR-Link, Basis, was geändert wurde, neue Schalter mit Standardwert, neue Zähler, grüne Tests und CI, Artifact-Link, was nur im Spiel prüfbar bleibt. Danach das nächste Paket.

## Schlusszusammenfassung

Tabelle aller Pakete: Branch, PR, CI-Status, install-verify. Dazu die Testliste für Lasses nächsten Spieltest (Drop-in des obersten PRs) und offene Punkte. Nichts mergen.
