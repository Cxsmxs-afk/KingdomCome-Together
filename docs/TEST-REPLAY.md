# T2 · Aufzeichnung und Replay

Für eine Aufnahme den Windows-Agent aus einem Wegwerfstand mit beiden Freigaben starten.

```powershell
$env:KCDMP_TEST_RECORD = '1'
.\KcdMpClient.exe --test-record --host 127.0.0.1 --port 7778
Remove-Item Env:KCDMP_TEST_RECORD
```

`--test-record` schreibt nach `%LOCALAPPDATA%\KingdomComeTogether\Recordings`. Mit `--test-record=Pfad.kcdrec` lässt sich eine eigene, noch freie Datei wählen. Ein normal beendeter Agent meldet `MP-T2-RECORD complete`. Der Reader prüft die Version, die Reihenfolge, die Rahmengrößen und den Abschlusszähler. Bei einem Schreibfehler, mehr als 64 MiB oder einem abrupten Ende kann die Datei unvollständig sein.

Die Aufzeichnung erfasst ausgewählte Spielzustände, Lade- und KO-Signale, Agent-Aufrufe und tatsächlich übertragene Relay-Frames. Ein aufgezeichneter Agent-Aufruf belegt die Rückkehr des Transportaufrufs. Die sichtbare Wirkung im Spiel ergibt sich aus der gesonderten Spielabnahme. Die Aufnahme kann Spielpositionen und Entitätsnamen enthalten und bleibt lokal. Voice, Handshake-Namen, Save-Transfer und freie Konsolentexte werden ausgelassen.

Für eine spätere Aufnahme mit zwei Spielinstanzen beide Agenten des Wegwerfstands mit demselben Relay und je einer eigenen Ausgabedatei starten. Der Host wechselt das Wetter, während der Joiner lädt. Nach dem Laden bestätigt der Joiner das Wetter. Beide Agenten regulär beenden und je eine abgeschlossene Datei prüfen. Der Ablauf im Spiel steht noch aus.

## Build-Prüfung

`ReplayScenarioTests` läuft im vorhandenen Client-Testschritt. `ReplayGameTransport` liest die aufgezeichneten Aufrufe und Antworten ohne laufendes Spiel. `ReplayPeer` führt die aufgezeichneten Netzwerkframes über den echten Relay-Dienst auf Loopback. Jedes synthetische Szenario hat eine Gegenprobe für den alten Pfad.

```powershell
dotnet test dotnet/KcdMp.Client.Tests/KcdMp.Client.Tests.csproj -c Release --filter FullyQualifiedName~ReplayScenarioTests
```

Die acht Szenarien im Build sind `weather-during-load`, `pickup-under-load`, `wolf-pack-fight`, `death-respawn`, `knockdown`, `bow-shot`, `remote-walk-stairs` und `remote-fight-down-up`. Der Test erwartet, dass der neue Pfad die Abnahmeregel erfüllt und die Gegenprobe diese Regel verletzt. Vor der Spielabnahme lässt sich damit prüfen, dass die Gegenprobe im Build überhaupt erkannt wird.
