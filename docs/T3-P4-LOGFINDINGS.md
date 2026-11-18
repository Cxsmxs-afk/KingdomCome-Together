# P4: Todesursachen und Teleportbefunde aus Test 3

Die Ursachenangaben des Auftrags werden anhand der Rohlogs präzisiert. Die sechs Bundles liegen außerhalb des Repositories. S1 ist `KCDMP-logs-20261117-234935`; die spätere Exportkopie 235020 enthält denselben Agent-Log. Die Host-Exporte 000909/001025 enthalten ebenfalls denselben fortlaufenden Log.

## Tod 2: finaler bewaffneter Partnerhit, vorher Blutung

S1 `kcdmp-native.mirror.log:19694`, **23:44:18.494**: Friendly Fire auf den lokalen Spieler, HP **−16,11**, Stamina −40, `unarmed=0`, von Ghost 0, angewendet. Die Klassifikation folgt bei Zeile 19698 um **23:44:18.669** mit `recent_attackers=0`, `hostiles=0`, `bleeding=yes`. `agent.log:23671` zeigt unmittelbar davor um **23:44:17.951** HP **3,7**; Zeile 23701 zeigt um **23:44:18.592** HP **1,0**, danach DOWNED.

Der Host bestätigt den weitergeleiteten −16,11-Treffer in `KCDMP-logs-20261118-000909/kcdmp-native.mirror.log:8667`. Die bereits vorher vorhandene Blutung erklärt einen früheren langsamen Gesundheitsverlust. Der finale Abfall folgt jedoch unmittelbar dem bewaffneten Partnerhit; das ist kein belegter reiner Bleedout und kein Tod ohne Angreifer.

## Tod 3: wiederholtes bewaffnetes Friendly Fire

S1 Native-Zeile 22292, **23:48:43.498**: Friendly Fire **−11,23 HP**, Stamina −40, `unarmed=0`, Ghost 0, angewendet. Bei Zeile 22294 um **23:48:43.646** wird der Tod klassifiziert: Gegnerzählung 0, Blutung nein. Der Agent zeigt bei Zeile 28061 um **23:48:34.503** noch **4,0 HP**, bei Zeile 28242 um **23:48:43.598** **1,0 HP**, danach DOWNED.

Vorherige bewaffnete Partnerhits: −57,05 um 23:47:49.320, −11,44 um 23:48:05.556, −11,11 um 23:48:23.446 und −10,74 um 23:48:34.410. Der Host bestätigt die Weiterleitung. Die passende Ursache ist die Folge dieser Treffer mit dem finalen −11,23-Hit.

Der PvP-Apply-Pfad führt diese Treffer ohne NPC-Attacker-Soul aus. Die vorhandene PvP-Provenienz speichert Zeit, Peer und unarmed; der bisherige Classifier wertete sie nur für den Faust-KO-Sonderfall aus. Deshalb widerlegt `recent_attackers=0` die Partnerhits nicht.

## Wake und 115 m/s sind getrennte Ereignisse

S1 Native-Zeilen 19722–19726, **23:44:19.480**: Der Partner steht nur etwa 2,7 m vom Todesort, der ausgewählte Wake-Punkt jedoch etwa 167 m vom Partner und 169 m vom Tod entfernt; der Log meldet 16,4 m Höhenunterschied. Der damalige Code verlangt mindestens 100 m Abstand vom Tod. Der nächste Wake liegt ebenfalls ungefähr 166 m vom nahen Partner entfernt, bei 11,9 m Höhenunterschied. Diese Fernpunkte sind ein tatsächlicher Modpfad.

Das Ereignis **23:43:21.966**, `agent.log:21335`, meldet zuvor 115 m/s. Die Positionsfolge belegt einen schnellen Weg von ungefähr (1956, 2024, 60) nach (1917,3, 1985,8, 59,9). Anschließend bleibt XY annähernd gleich, während Z bis ungefähr 49,5 fällt und HP von 100 auf 20,5 sinkt. Das passt zu einem Fall; die konkrete Schadensart ist nicht protokolliert. Die Zeile „on its feet again“ verwendet eine Geschwindigkeitsregel und beweist keinen Bodenkontakt.

In diesem Ausschnitt fehlen ein passender `MP-LEASH pulled`, Respawn-Wake, Unstuck oder Home-TP. Der auslösende Writer bleibt offen. Der tatsächlich gefundene alte Home-TP-Bypass darf deshalb nicht nachträglich als bewiesene Ursache der 115 m/s bezeichnet werden.

## Neue Diagnose muss Gewissheit von Inferenz trennen

Pro Tod gehören tatsächliche angewendete Treffer mit Peer/Alter, Blutungszustand und seine Lesegültigkeit, Bewegungs-/Bodenwerte sowie der letzte eigene Mod-Teleport in den Log. Ein Tod innerhalb zehn Sekunden nach einem Mod-Teleport bekommt den geforderten Marker `T3-FALLDEATH-AFTER-TP`, zusätzlich mit tatsächlicher Ursache beziehungsweise ausdrücklich markierter Inferenz. Der Markername allein beweist keinen Fallschaden.

Der vorhandene `imm=1,upr=1`-Buff hält HP auf mindestens 1; er ist kein allgemeiner Null-Schaden-Schalter. Ebenso ist eine Navmeshprojektion kein physikalischer Bodenkontakt. Weder ein unbekannter Ground-/Combat-Wert noch eine unbekannte Waffenhit-Rückgabe darf als sichere Freigabe behandelt werden. Vollständige Vanilla-Schadensimmunität bleibt ohne frisch validierten Enginehebel unbewiesen; eigene Damage-Apply-Blätter und das Recoveryfenster werden separat geprüft.
