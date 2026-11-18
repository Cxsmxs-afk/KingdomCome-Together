# Lane 3 – Einheitliche Markerfarben

Basis `5fc9133e` auf `codex/s3-1-load-sequence`.

Der Host vergibt Rot (Slot 1) für sich und reserviert Violett, Blau und Grün (Slots 2–4) für die ersten Gastnamen in seiner Relay-Teilnehmerliste. Er beobachtet neue Teilnehmer beim nächsten Abgleich. Innerhalb desselben Abgleichs entscheidet die Relay-ID. Ein Rejoin mit demselben Relay-Namen behält den Slot in derselben Host-Sitzung auch bei geänderter Relay-ID. Unterschiedliche Personen brauchen unterschiedliche Relay-Namen. Weitere Teilnehmer erhalten Slot 0 und damit den Companion-Marker.

Der Host verteilt die bis zu drei Gastnamen Base64-kodiert als `marker-slots-v1|…` über den bestehenden zuverlässigen `W164MarkNote`-Kanal. Der eigene Nachrichtenpräfix unterscheidet die Tabelle von gewöhnlichen Markierungsnotizen. Spätere Teilnehmer erhalten die Tabelle über die periodische Wiederholung. Joiner übernehmen sie vom bestätigten Relay-Host. Bis zu dessen Zuteilung sehen sie Gäste mit dem Companion-Marker. Karten- und Kompass-Marker verwenden denselben Slot.

## Ampel

| Prüfung | Stand |
|---|---|
| Unit- und Integrationstests zu Sichtreihenfolge, Rejoin, Absender und Slot 0 ab der fünften Person | Grün |
| Synthetisches Log mit drei Peers | Grün · beide Beobachter sehen Host `1`, Ann `2` und Bob `3`, `marker_slot_mismatch=0` |
| Windows-CI und Drop-in-`install-verify` | [CI-Status des Lane-Branches](https://github.com/Cxsmxs-afk/KingdomCome-Together/actions/workflows/build-windows.yml?query=branch%3Acodex%2Flane3-marker-host-slots) |
| Live mit drei bis vier Spielern | Grau · kein entsprechendes Spiel-Rig verfügbar |

Der Agent zählt `marker_slot_mismatch` nach bekannter Host-Zuteilung am vom nativen Kanal bestätigten Bildcode. Die sichtbare Darstellung im Spiel bleibt ein Live-Test.
