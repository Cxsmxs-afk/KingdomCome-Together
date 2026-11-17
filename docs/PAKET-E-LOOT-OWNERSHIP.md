# Paket E: Leichen-Loot und eigener Besitz

Basis: Paket D / PR #16. Diese Änderung hält D's portioniertes Ledger und dessen Welt-Epochen bei.

## Truhen und Kräuter

- Eine Truhe wird vor dem Öffnen erfasst; die letzte Änderung beim Weggehen wird noch gelesen. Ein fehlgeschlagener Inventarread ist kein leerer Bestand.
- Host-Entnahmen werden zurückgelegt, eigene Entnahmen entfernt. Fehlende, noch nicht gestreamte Container behalten ihre Zeilen. Die Anwendung wartet auf beide Ledgers statt nach 15 Sekunden endgültig nur die eigene Hälfte anzuwenden. Der Empfang wird vor Ready vorbereitet.
- Das eigene Kräuter-Ledger wird aus bestätigten Inventarzuwächsen nach `PickableArea.Gather` geschrieben. Abbruch allein erzeugt keinen Eintrag. Schlüssel sind Klasse und Position, keine temporären Entity-Namen. `Amount` ist laut Spielskript ein Koeffizient und wird nicht als gewährte Stückzahl interpretiert.
- Kräuter-Einträge leben im selben atomar geschriebenen, zurückgelesenen Henry-Sidecar wie die Truhen-Einträge. Nur die ausgewählte eigene Momentaufnahme wird wiederhergestellt. Die Host-Truhen-Nachricht transportiert keine fremden Kräuter-Einträge. Bis das eigene Kräuter-Ledger vollständig zurück ist, bleibt neues Erfassen gesperrt; danach entscheidet die Engine über die Verfügbarkeit. Alte Welt-Epochen dürfen nichts anwenden.

Quelle des Script-Vertrags: `Scripts/Entities/WH/Others/PickableArea.lua`, [öffentlich dokumentierte Spielskript-Fassung](https://github.com/muyuanjin/kcd2-mod-docs/blob/9cb067543245ec5cf4e27b3d21e32485a35db518/Scripts/Scripts/Entities/WH/Others/PickableArea.lua). Keine fremden Spielskripte werden mitgeliefert.

## Nachweis und Grenzen

`Test-WO134Synthetic.lua` prüft die tatsächliche Lua-Datei gegen Inventar- und Timer-Stubs: verzögertes Streaming, frühester/letzter Truhenklick, Lesefehler, bestätigtes/abgebrochenes Sammeln, eigene Wiederherstellung und veraltete Epochen. C# prüft tatsächliches Schreiben/Lesen des Snapshot-Sidecars. Die bestehenden D-Tests für Budget, Holds, verlorene Antworten und Wiederholungen bleiben erhalten.

Nicht im echten Spiel bestätigt: Kräuterflächen werden intern durch C++/Vegetation verbraucht. Diese Änderung erfindet keinen Serializer für diesen Zustand und kann keine durch den Host bereits entfernte Vegetation zurückerzeugen. Ebenso ist der natürliche Regrowth-Zeitpunkt im Lua-Vertrag nicht verfügbar; dafür wird keine Frist geraten. Bereits erfasste Einträge sperren natürlichen Nachwuchs ausdrücklich nicht dauerhaft. Vollständige visuelle Kräuter-Unabhängigkeit ist deshalb noch offen. Die Disk-Sidecars und die eigene Erfassung/Wiederherstellung sind davon getrennt geprüft.

Leichen-Loot benutzt weiterhin den vorhandenen Lua-Event-/Agent-/Relay-Weg; ein neues ungeprüftes Native-Lua-Binding wird nicht behauptet. Reale positive `loot_reqs_out`/`loot_verdicts_in` und native Inventar-/Outfitwirkung benötigen den Spieltest. Synthetische Tests müssen echte Requests und Verdicts mit finalem Readback erzeugen; keine Statistik wird künstlich erhöht.
