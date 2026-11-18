# Abnahme Schritt 3 · Weltübergänge und bestätigte Ereignisse

## s3-1 · Laden pro Weltgeneration

Die Spielprüfung steht aus. Für die beiden Rig-Szenarien einen Wegwerfstand im Spielprofil der Host-Kopie und dasselbe Build für Relay, Agent und DLL verwenden. Der reale Spielstand bleibt außerhalb des Checkouts. Das Setup von `pair.py` übernimmt den Stand für die isolierte Host-Kopie. Für `--save` den Dateinamen ohne `.whs` angeben.

```powershell
$env:KCDMP_TEST_LOAD_SEQUENCE = '1'
$env:KCDMP_RIG_TEST_ENV = 'KCDMP_TEST_LOAD_SEQUENCE=1'
$env:KCDMP_RIG_RUN = 's3-1-reload-normal'
python tools/rig/pair.py prepare
python tools/rig/pair.py relay --netz normal
python tools/rig/pair.py up A
python tools/rig/pair.py up B
python tools/rig/pair.py host A 4 save440
python tools/rig/pair.py join B
python tools/rig/scenarios.py reload --test-load-sequence --netz normal --playline 4 --save save440
python tools/rig/pair.py down s3-1-reload-normal
```

Für `weatherload` mit beiden Spielkopien dieselbe Vorbereitung in einem neuen Lauf verwenden. Nach dem Start des Hosts und Joiners den folgenden Befehl statt `reload` aufrufen. Das Rig aktiviert die Wetterprobe vor dem Reload. Der Host sendet das Profil nach der bestätigten Save-Übertragung und vor dem Ready des Joiners.

```powershell
python tools/rig/scenarios.py weatherload --test-load-sequence --netz normal --playline 4 --save save440 --profile foggy_drizzly_light
```

Für `reload --bot` nur den echten Host starten. Der T4 Bot-Joiner bestätigt je eine geprüfte Save-Übertragung vor und nach der Weltgeneration des Host-Reloads.

```powershell
$env:KCDMP_TEST_BOT = '1'
python tools/rig/scenarios.py reload --test-load-sequence --netz normal --playline 4 --save save440 --bot
```

Für `weatherload --bot` einen neuen Lauf beginnen und mit dem Rollentausch nur die Kopie A im Hauptmenü starten. Das Rig startet den T4 Bot-Host und verbindet A als echten Joiner. T4 sendet das Profil vor dem Ready des Joiners. `$TestSave` ist der Pfad zum externen Wegwerfstand, `$WorldTime` dessen `Calendar.GetWorldTime()` in Sekunden. Bei einem separat gebauten T4-Peer kann `--wire-version` die Relay-Version einschließlich Build-ID vorgeben.

```powershell
$env:KCDMP_TEST_BOT = '1'
$env:KCDMP_RIG_HOST = 'B'
$env:KCDMP_RIG_RUN = 's3-1-weather-bot-normal'
python tools/rig/pair.py prepare
python tools/rig/pair.py relay --netz normal
python tools/rig/pair.py up A
python tools/rig/scenarios.py weatherload --test-load-sequence --netz normal --bot --bot-save "$TestSave" --world-time $WorldTime --profile foggy_drizzly_light
python tools/rig/pair.py down s3-1-weather-bot-normal
Remove-Item Env:KCDMP_RIG_HOST
```

Jede Variante in einem neuen Lauf mit `--netz schlecht` wiederholen. Relay und Szenario brauchen dasselbe Profil. Der Fehlerplan verzögert Pakete um 150 ms plus 0–35 ms Jitter. `reload.json` und `weatherload.json` enthalten die beobachteten Phasen, lokalen Generationen, Host-Welt, Statistik und Bundles. Die Zwei-Spiel-Variante meldet den gemeinsamen Weltwechsel, ein neues Ready und eine passende Host-Freigabe. Die Bot-Varianten messen ausschließlich die Engine der realen Spielkopie. Ein abgewiesener alter Frame ist in `stale_gen_dropped` oder `stale_gen_native_dropped` nur bei beobachteter Abweisung belegt. Die automatischen Gegenproben verwenden synthetische Daten.

### Zwei-PC-Checkliste

1. Auf beiden PCs dasselbe Build in einem Wegwerfstand verbinden. Vor dem Reload beide Figuren sehen und die aktuelle Phase `Play` sowie die Host-Welt notieren.
2. Der Host lädt den Teststand erneut. Der Joiner folgt in dieselbe neue Host-Welt. Auf beiden Seiten erst nach Laden, Rekonstruktion und Settle wieder steuern. Den Verlauf in `MP-LOAD-SEQUENCE` und der nativen Statistik abgleichen.
3. Für den Wetterlauf den Host-Agent mit `KCDMP_TEST_LOAD_SEQUENCE=1` starten. Vor dem Reload in der Host-Konsole `mp_test_weatherload --test-load-sequence foggy_drizzly_light` ausführen. Das Profil wird nach der Save-Übertragung gesendet. Beim Joiner die zurückgestellte Meldung, danach die bestätigte Anwendung ab `Play` im Spielprotokoll und im Spiel vergleichen.
4. Beide Läufe mit 150 ms plus Jitter wiederholen. Beim Reload die alte Partnerfigur aus der Szene verschwinden sehen und nach der Rekonstruktion die neue Figur an der richtigen Stelle prüfen.
5. Beide LogPack-Bundles sammeln. `owner_violations` bleibt null. Phasenzeit und getrennte Zähler für alte Agent- und native Frames mit den beobachteten Ladewechseln abgleichen. Einen Lauf ohne beobachtete Abweisung als fehlende Messabdeckung erfassen.

## s3-2 · Baseline und bestätigte Ereignisse

Das Rig wartet nach dem gemeinsamen Laden auf eine angewendete Baseline der Version 1 und ihre Bestätigung am Relay. Die Schritte laufen in Wegwerfständen. Vor dem Start beider Agents die Testumgebung setzen. Das normale Relay-Profil eignet sich zum Einrichten des Teststands. Die Abnahme umfasst auch `--netz schlecht` mit 150 ms plus Jitter.

```powershell
$env:KCDMP_TEST_BASELINE_EVENTS = '1'
$env:KCDMP_RIG_TEST_ENV = 'KCDMP_TEST_BASELINE_EVENTS=1'
python tools/rig/pair.py relay --netz schlecht
python tools/rig/scenarios.py pickup --test-baseline-events --netz schlecht
python tools/rig/scenarios.py corpse --test-baseline-events --netz schlecht
python tools/rig/scenarios.py dialog --test-baseline-events --netz schlecht
```

Zuerst beide Spielkopien wie unter s3-1 starten und verbinden. Nach der Aufforderung im Terminal die jeweilige Aktion normal im Spiel ausführen. Das Rig prüft je Ereignis den Relay-Versand, den angewendeten Spielzustand und die danach geschriebene und vom Relay empfangene Bestätigung. `pickup` erwartet einen bestätigten losen Weltgegenstand. `corpse` erwartet einen Gegenstand aus der Leichenansicht mit bestätigter Aufnahme ins Inventar. `dialog` erwartet zwei verschiedene Stimmen für dieselbe offene Entscheidung. Die Berichte `pickup.json`, `corpse.json` und `dialog.json` enthalten Welt, Generation, Snapshot, Ereignis-ID und bestätigte Grenzen. `evt_gap`, `evt_dropped`, `evt_sender_dropped` und `owner_violations` bleiben null. `evt_retx` zeigt wiederholte Übertragungen an.

### T4-Varianten

Für `dialog --bot` Relay und nur den geladenen echten Host starten. Der Host beginnt im Spiel ein Gespräch mit mindestens zwei Antwortoptionen. Der Bot-Joiner folgt dem Gespräch und sendet zwei Stimmen. Die Bestätigung des Symbols im HUD stammt vom echten Host.

```powershell
$env:KCDMP_TEST_BOT = '1'
python tools/rig/scenarios.py dialog --test-baseline-events --netz schlecht --bot
```

Für `pickup --bot` und `corpse --bot` den echten Joiner alleine im Hauptmenü starten. Im A/B-Rig dazu wie bei `weatherload --bot` vor der Vorbereitung `KCDMP_RIG_HOST=B` setzen und nur Kopie A starten. Der T4 Bot-Host serviert den externen Wegwerfstand und entscheidet anhand des Testplans. Der echte Joiner hebt den Gegenstand auf oder entnimmt ihn der Leiche. Teststand, Weltzeit und Plan stammen aus derselben vorbereiteten Spielsituation. Im Plan enthält `setup witem <Klassen-GUID> <x> <y> <z>` einen losen Gegenstand und `setup bodyset <Name> <Klassen-GUID>:<Anzahl>:<Zustand>` den Inhalt einer vorhandenen Leiche. Der Zustand ist normiert, zum Beispiel `1` für vollständig erhaltene Gegenstände. Eine zeitgesteuerte `at 0 pos <x> <y> <z>`-Zeile vervollständigt den Plan. Der Bot führt eine synthetische Inventarliste. Maßgeblich für die Abnahme bleibt das Inventar des echten Joiners.

```powershell
python tools/rig/scenarios.py pickup --test-baseline-events --netz schlecht --bot --bot-save "$TestSave" --bot-plan "$PickupPlan" --world-time $WorldTime
python tools/rig/scenarios.py corpse --test-baseline-events --netz schlecht --bot --bot-save "$TestSave" --bot-plan "$CorpsePlan" --world-time $WorldTime
```

Für jede Bot-Host-Variante einen frischen Lauf mit Relay und Joiner im Hauptmenü verwenden. Die automatische Prüfung verwendet synthetische Fixtures. Die Spielprüfung steht aus.

### Zwei-PC-Checkliste

1. Denselben Build verwenden und in einem Wegwerfstand verbinden. Nach dem Laden Baseline-Version 1 in der richtigen Weltgeneration und die Relay-Bestätigung prüfen.
2. Der Joiner hebt einen losen Weltgegenstand auf. Den bestätigten Gegenstand und seine Anzahl im Inventar mit dem bestätigten Pickup-Ereignis abgleichen.
3. Der Joiner nimmt genau einen Gegenstand aus einer Leiche. Sein Inventar und die danach auf beiden PCs aktualisierte Leichenliste prüfen.
4. Der Host hält eine Antwortauswahl offen. Der Joiner stimmt für zwei verschiedene Zeilen. Der Host sieht die Symbole in derselben Reihenfolge und entscheidet selbst.
5. Mit 150 ms plus Jitter wiederholen und beide Bundles sammeln. Ereignis-ID, Anwendung und spätere Bestätigung abgleichen. `evt_gap`, `evt_dropped`, `evt_sender_dropped` und `owner_violations` bleiben null.

## s3-3 · Gezielte Wiederherstellung

Die Spielprüfung steht aus. Für `disconnect` den Relay-Fehlerplan vor dem Verbinden starten. Das Relay trennt den echten Joiner nach seiner bestätigten Baseline für zehn Sekunden und nimmt danach seine Verbindung wieder an. Der Host bleibt in seiner laufenden Welt. Die Spielkopien und Agenten wie unter s3-1 vorbereiten. Mit jedem Netzprofil einen frischen Lauf starten.

```powershell
$env:KCDMP_TEST_WORLD_RECOVERY = '1'
$env:KCDMP_RIG_TEST_WORLD_RECOVERY = '1'
$env:KCDMP_RIG_TEST_ENV = 'KCDMP_TEST_WORLD_RECOVERY=1'
$env:KCDMP_RIG_RUN = 's3-3-disconnect-schlecht'
python tools/rig/pair.py prepare
python tools/rig/pair.py relay --test-world-recovery=disconnect --netz schlecht
python tools/rig/pair.py up A
python tools/rig/pair.py up B
python tools/rig/pair.py host A 4 save440
python tools/rig/pair.py join B
python tools/rig/scenarios.py disconnect --test-world-recovery --netz schlecht
python tools/rig/pair.py down s3-3-disconnect-schlecht
```

Für `desync` einen neuen Lauf mit dem regulären Relay-Profil starten. Nach dem Verbinden verfälscht das Rig die bestätigte lokale Darstellung eines Bereichs beim Joiner, standardmäßig das Loot-Ledger. Das Verfahren verändert die Daten für den Prüfsummenvergleich. Die Prüfsummen werden regelmäßig getrennt für Körper, NPCs, Leichen, Loot-Ledger, Zeit und Wetter verglichen. Die Reparatur muss denselben Bereich anwenden und der nächste Vergleich für die reparierte Revision wieder übereinstimmen. Die tatsächliche Spielwelt und sichtbare Objekte werden bei der Zwei-PC-Abnahme zusätzlich geprüft.

```powershell
python tools/rig/pair.py relay --netz schlecht
python tools/rig/scenarios.py desync --test-world-recovery --netz schlecht --section loot
```

Für die T4-Variante wie bei `weatherload --bot` vor dem Vorbereiten `KCDMP_RIG_HOST=B` setzen und nur Kopie A im Hauptmenü starten. Beide Szenarien starten einen T4 Bot-Host mit externem Wegwerfstand und zugehöriger Weltzeit. Seine Hostkennung bleibt über die neue Verbindung des echten Joiners erhalten. Die Belege zur Engine stammen von der echten Spielkopie.

```powershell
$env:KCDMP_TEST_BOT = '1'
python tools/rig/scenarios.py disconnect --test-world-recovery --netz schlecht --bot --bot-save "$TestSave" --world-time $WorldTime
python tools/rig/scenarios.py desync --test-world-recovery --netz schlecht --section loot --bot --bot-save "$TestSave" --world-time $WorldTime
```

Die Befehle getrennt starten. Der Relay-Aufruf für `disconnect` lädt den Zehn-Sekunden-Schnitt. `desync` nutzt das normale oder schlechte Profil ohne Schnitt. `disconnect.json` und `desync.json` enthalten die Scope-Zuordnung, Zähler und LogPack-Bundles. Für das normale Profil bei Relay und Szenario `--netz normal` setzen. Das schlechte Profil verwendet 150 ms plus 0–35 ms Jitter. Der Wiederanlauf nach einem separaten Verbindungsschnitt am Host bleibt im Spiel ungeprüft.

### Zwei-PC-Checkliste

1. Denselben Build verwenden und einen Wegwerfstand gemeinsam laden. Host-Welt, lokale Generationen und angewendete Baseline auf beiden PCs notieren.
2. Den Joiner durch den Relay-Fehlerplan für zehn Sekunden trennen lassen. Der Host bleibt in seiner Welt. Der Joiner nimmt die gleiche Welt innerhalb von 30 Sekunden ohne Ladebildschirm wieder auf.
3. Nach der Rückkehr beide Figuren und ein gewöhnliches gemeinsames Ereignis im Spiel prüfen. `reconnect_reload` und `owner_violations` bleiben null.
4. Mit aktiviertem Testschalter auf dem Joiner `mp_test_world_recovery loot` ausführen. Im Protokoll auf die absichtliche Abweichung, die Reparatur des Loot-Ledgers und die wieder übereinstimmende Prüfsumme warten. Die anderen Bereiche kontrollieren.
5. Beide Abläufe mit 150 ms plus Jitter wiederholen und beide Bundles sammeln. `digest_mismatch_loot` und `resync_loot` steigen beim Test jeweils um eins. Die Reparaturzähler der übrigen Bereiche bleiben während der Probe unverändert.
