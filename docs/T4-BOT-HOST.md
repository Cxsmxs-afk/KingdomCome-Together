# Skriptgesteuerte Gegenstelle für den Koop-Test

`tools/bothost/bot-joiner` startet die vorhandenen Avatar- und Welt-Peers am lokalen Relay. Der Testmodus erfordert `KCDMP_TEST_BOT=1` und `--test-bot`. Das Werkzeug startet mit Python und dem .NET 8 SDK. Die Test-Peers werden aus dem Checkout gebaut.

## Echter Host und Bot-Joiner

Im Rig zuerst nur die Host-Instanz und das Relay starten. Danach den Joiner aufrufen.

```powershell
$env:KCDMP_TEST_BOT = '1'
python tools/rig/scenarios.py botjoiner
```

Der Bot holt die angebotene Welt, wartet auf die bestätigte Freigabe und spielt eine feste Route mit Gangwechsel, Kurve und Höhenstufen. Es folgen Angriff, Block, Treffer, Niederschlag, Aufstehen, Tod, Respawn und Bogenschritte. Die Position zu Beginn wird einmalig vom Host abgefragt. Danach läuft der Plan weiter, während der Bot nur die für den Verbindungsablauf nötigen Nachrichten verarbeitet. Jede Station hält Sollzeit und Istzeit auf dem gemeinsamen monotonen Rechner-Takt fest. Die bestehenden Netzwerkformate bestimmen, welche Pakete einen Zeitstempel mitführen.

Ohne Rig kann `python tools/bothost/bot-joiner --test-bot` direkt gestartet werden. `--port` wählt den Relay-Port. `--origin=x,y,z` setzt den Startpunkt und `--scenario` ersetzt die Route. Beim Bogen liefern die installierten Modding Tools die Item-Klasse. Den Pfad zur Tabelle kann `--tables-pak` vorgeben. `--bow-class` setzt eine bekannte Item-Klasse gezielt. Bei einem separat erstellten Testartefakt desselben Quellstands kann `--wire-version` dessen Kennung `VERSION+BUILD_ID` angeben. Das Rig liest sie aus dem Agent-Log.

## Bot-Host und echter Joiner

Den Bot als ersten Teilnehmer am lokalen Relay starten. `--save` erwartet einen externen Test-Save außerhalb des Checkouts. `--origin` nennt die feste Startposition. `--world-time` übernimmt den mit dem Spielstand aufgezeichneten Wert von `Calendar.GetWorldTime()` in Weltsekunden.

```powershell
$env:KCDMP_TEST_BOT = '1'
python tools/bothost/bot-joiner --test-bot --role host --port 7790 --save $TestSave --origin=$Startposition --world-time $Weltsekunden
```

Anschließend die Joiner-Instanz mit dem Relay verbinden. Der Bot sendet vor dem Weltangebot einen eingefrorenen Kalender für genau diese Join-ID. Nach der Freigabe folgen der laufende Kalender und der getaktete Positionsstrom. Ein optionaler Plan unter `--plan` verwendet `at <Sekunden> <Befehl>` für den Weltstrom und `setup` für die Testgegenstände. `witem` bereitet einen vorhandenen Gegenstand vor, `witemtaken` einen bereits entnommenen. Ein Gegenstand ohne passende Regel bleibt `unknown`.

Die fünf bisherigen PowerShell-Bots sind über `--legacy walk|weapons|item|claim|dice` erreichbar. Ihre eigenen Argumente folgen nach `--`.

## Auswertung

Das Rig legt `bot-t4.log` beim Szenario ab und übernimmt es in das Host-Bundle. `bot_plan_complete` wird bei belegtem Start und Abschluss grün. Ein protokollierter Startfehler, ein Abbruch oder ein Lauf ohne Abschluss ergibt Rot. Ohne Messung bleibt die Regel grau. Die sichtbare Spielfigur, die tatsächliche Trefferreaktion und der Übergang aus dem Test-Save bleiben im Spiel zu prüfen.
