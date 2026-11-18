# Test 3: Umsetzung und Wiederaufnahme

Auftrag: [T3-AUFTRAG.md](T3-AUFTRAG.md). Alle Änderungen bleiben auf gestapelten Branches; nichts mergen, kein Force-Push, Rebase, Amend oder Reset. Erst nach grüner Windows-CI inklusive Drop-in mit install-verify PASS das nächste Paket beginnen. Reale Spieltests werden getrennt ausgewiesen.

| Paket | Branch | Basis | Stand |
| --- | --- | --- | --- |
| P0 | codex/t3-p0-processor-stall | codex/loot-ownership-polish, 339d78c | [PR #18](https://github.com/Cxsmxs-afk/KingdomCome-Together/pull/18), CI 44208035940 grün, Drop-in install-verify PASS |
| P1 | codex/t3-p1-intellect-gate | P0, 780075b | [PR #19](https://github.com/Cxsmxs-afk/KingdomCome-Together/pull/19), Head ad16c5f, CI 44218390184 grün, Drop-in install-verify PASS |
| P3 | codex/t3-p3-pickup-freeze | P1, ad16c5f | Implementierung |
| P4 | codex/t3-p4-respawn-wake | P3 | offen |
| P5 | codex/t3-p5-corpses-marker | P4 | offen |
| P6 | codex/t3-p6-bow | P5 | offen |
| P2 | codex/t3-p2-frame-budget | P6 | nur nach Messung der Bedingung |

## Testmaterial

Die sechs Bundles aus `KCDMP-logs-20261117-234935.zip` sind geöffnet. Zwei Paare enthalten dieselbe Agent-Logdatei: 234935/235020 (S1), 000909/001025 (Host). S2 ist 235809, S3 ist 000930. Rohlogs bleiben außerhalb des Repositories. Befunde werden je Paket mit Datei, Zeile und Zeit beschrieben.

## Wiederaufnahme

1. GitHub-Branches/PRs `codex/t3-p*` und deren aktuellen Head/CI prüfen; grüne Pakete nicht erneut bauen.
2. Angefangenes Paket auf seiner Branch fortsetzen. Lokale Änderungen nicht überschreiben.
3. Nach jeder grünen CI Artifact/Drop-in-Verifikation dokumentieren, kurz deutsch berichten, dann das nächste offene Paket beginnen.
