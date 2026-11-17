# Paket B: Host-Kalender auf dem Joiner

Basis: `codex/join-save-retention`, PR #13, `9689c34`. Kein Merge.

## JOINHOLD

Die im Zweispielertest gemessenen 208 Sekunden sind keine feste Wartezeit. `WO151-JOINHOLD off after …` protokolliert die tatsächliche Dauer. Der native Halt beginnt vor der Übertragung des verifizierten Saves. Das erste gültige Ready des Joiners beendet ihn; Abbruch, Fehler und Verbindungsverlust lösen ihn ebenfalls. Die native Deadline ist ein Ausfallschutz, keine Mindestdauer.

Paket A schützt die verifizierte Join-Datei und vermeidet wiederholte Vollscans bei SaveList-Timeouts. Es überträgt keine Weltänderungen, die der Host nach seinem Snapshot ausführen würde. Der Halt bis Ready bleibt deshalb bestehen. Die Datei-Aufräumarbeiten laufen bereits nach Ready. Es gibt hier keine zusätzliche feste Nachwartezeit zu entfernen.

## Spielprüfung mit zwei Rechnern

1. Host und Joiner mit demselben Drop-in-Zip starten. Join aus dem Hauptmenü testen; danach einen Join aus einer laufenden eigenen Welt.
2. Vor dem Load-Befehl muss die native Kalender-Vorbereitung bestätigt sein. Bei fehlendem oder ungültigem Hostwert darf der Join keinen unvorbereiteten Load starten.
3. Die ersten nativen Uhrzeilen nach dem Laden prüfen: Host-Kalender statt Start bei 0, Rücklesewert bestätigt. Die erste tatsächliche Scheduler-Auswertung der Engine lässt sich nur im Spiel prüfen.
4. Nach Ready normal spielen, dann Host-Dialog, Pause und einen kurzen Lade-/Reconnect-Halt wiederholen. Nach der Freigabe muss die Joiner-Uhr still beim Host liegen. `gap_game_s=+122` darf nicht wieder auftreten; `slews` soll bei 0 bleiben. Kalenderfaktor 15 ist das normale Spieltempo. Er darf nicht mit dem Welt-Simulationsfaktor verwechselt werden; die Synchronisierung darf die Welt nicht beschleunigen.
5. Gemeinsamen Schlaf/Warten prüfen: während des eigenen Zeitraffers keine Kalenderkorrektur; nach dessen Ende wieder beim aktuellen Hostwert beginnen.
6. Join abbrechen, Verbindung trennen und eine eigene Welt laden. Es darf kein alter Host-Kalender in die eigene Welt übernommen werden.
7. `JOINHOLD on → off`, SaveList-Versuche und `Gameplay started → Ready` mit dem alten Lauf vergleichen. Eine Verkürzung gegenüber den gemessenen 208 Sekunden ist erst mit diesem Lauf belegt.

CI kann die Zustandsregeln, Paketformate, Quellverdrahtung und Builds prüfen. Es bestätigt keine tatsächliche Engine-Ladezeit und keinen Zweirechner-Spieltest.
