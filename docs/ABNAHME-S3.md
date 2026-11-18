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
