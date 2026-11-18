# Test 3: Umsetzung und Wiederaufnahme

Auftrag: [T3-AUFTRAG.md](T3-AUFTRAG.md). Alle Änderungen bleiben auf gestapelten Branches; nichts mergen, kein Force-Push, Rebase, Amend oder Reset. Erst nach grüner Windows-CI inklusive Drop-in mit install-verify PASS das nächste Paket beginnen. Reale Spieltests werden getrennt ausgewiesen.

| Paket | Branch | Basis | Stand |
| --- | --- | --- | --- |
| P0 | codex/t3-p0-processor-stall | codex/loot-ownership-polish, 339d78c | [PR #18](https://github.com/Cxsmxs-afk/KingdomCome-Together/pull/18), CI 44208035940 grün, Drop-in install-verify PASS |
| P1 | codex/t3-p1-intellect-gate | P0, 780075b | [PR #19](https://github.com/Cxsmxs-afk/KingdomCome-Together/pull/19), Head ad16c5f, CI 44218390184 grün, Drop-in install-verify PASS |
| P3 | codex/t3-p3-pickup-freeze | P1, ad16c5f | [PR #21](https://github.com/Cxsmxs-afk/KingdomCome-Together/pull/21), Head bfd9500b, CI 44223046972 grün, Drop-in 14392880463 install-verify PASS |
| P4 | codex/t3-p4-respawn-wake | P3, bfd9500b | [PR #22](https://github.com/Cxsmxs-afk/KingdomCome-Together/pull/22), Head 0cacd7a, CI 44230437849 grün, Drop-in 14395043289 install-verify PASS; vollständiger Vanilla-Schadensschutz offen |
| P5 | codex/t3-p5-corpses-marker | P4, 0cacd7a | [PR #23](https://github.com/Cxsmxs-afk/KingdomCome-Together/pull/23), Head 329e440, CI 44238201571 grün, Drop-in 14399380513 install-verify PASS; unkontrollierte Engine-Purge-/Map-Lebensdauer offen |
| P6 | codex/t3-p6-bow | P5, 329e440 | Implementierung |
| P2 | codex/t3-p2-frame-budget | P6 | nur nach Messung der Bedingung |

## Testmaterial

Die sechs Bundles aus `KCDMP-logs-20261117-234935.zip` sind geöffnet. Zwei Paare enthalten dieselbe Agent-Logdatei: 234935/235020 (S1), 000909/001025 (Host). S2 ist 235809, S3 ist 000930. Rohlogs bleiben außerhalb des Repositories. Befunde werden je Paket mit Datei, Zeile und Zeit beschrieben.

## Wiederaufnahme

1. GitHub-Branches/PRs `codex/t3-p*` und deren aktuellen Head/CI prüfen; grüne Pakete nicht erneut bauen.
2. Angefangenes Paket auf seiner Branch fortsetzen. Lokale Änderungen nicht überschreiben.
3. Nach jeder grünen CI Artifact/Drop-in-Verifikation dokumentieren, kurz deutsch berichten, dann das nächste offene Paket beginnen.
