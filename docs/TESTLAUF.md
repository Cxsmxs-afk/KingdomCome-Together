# Geführter Testlauf auf zwei PCs

Der Testlauf führt Host und Joiner durch die Zwei-PC-Abnahme. Die Spieler führen die Aktionen aus und prüfen das sichtbare Ergebnis auf ihren Bildschirmen. Die LogPack-Ampel bewertet die technischen Messungen. Die Ausführung im Spiel steht aus.

## Beide PCs vorbereiten

1. Auf beiden Windows-PCs dieselbe Version der KCD2 Modding Tools und dasselbe geprüfte Drop-in verwenden. Einen Wegwerfstand mit freier Sicht, einer kurzen Strecke mit Treppe oder Hang und Platz für einen Kampf auswählen.
2. Beide Launcher und Agents schließen. Auf jedem PC den Testschalter in einer PowerShell setzen und den Launcher aus demselben Fenster starten.

   ```powershell
   $env:KCDMP_TEST_GUIDED_RUN = '1'
   & "$Anwendungsordner\KCDMP_launcher.exe"
   ```

   `$Anwendungsordner` ist der Ordner mit `KCDMP_launcher.exe` aus dem Drop-in. Auf getrennten PCs `mp_rp_measure_clock off` verwenden.

3. Der Host lädt den Wegwerfstand und genau ein Joiner tritt bei. Sobald beide in derselben Welt spielen, auf beiden PCs in der Spielkonsole starten.

   ```text
   mp_testlauf --test-guided-run start
   ```

## Stationen

Vor dem Weitergehen auf beiden PCs die angezeigte Station abgleichen. Das beobachtete Ergebnis jeweils mit `weiter ja` oder `weiter nein` festhalten. Bei einer Abweichung kann die Station wiederholt oder der Lauf abgebrochen werden.

| Station | Aktion und Beobachtung |
| --- | --- |
| Sehen | Beide Henrys in Sichtweite bringen. Auf jedem Bildschirm die Partnerfigur, ihre Lage und Blickrichtung prüfen. |
| Laufen | Nacheinander gehen, laufen, Kurven und eine Treppe oder einen Hang nehmen. Die andere Seite beobachtet Gangart, Bodenkontakt und sichtbare Versetzungen. |
| Kämpfen | Im Teststand gegen einen Banditen kämpfen. Nacheinander schlagen, blocken, parieren und eine Trefferreaktion auslösen. Die sichtbare Reaktion und den Schaden in der zuständigen Spielinstanz vergleichen. |
| Umfallen | Im Wegwerfstand einen Henry KO gehen lassen. Auf der Gegenseite prüfen, ob genau seine Partnerfigur fällt und liegen bleibt. |
| Aufstehen | Dieselbe Figur aufstehen lassen. Die andere Seite prüft, ob sie am Ort des Sturzes aufsteht. Anschließend die Beobachtungsrichtung wechseln. |
| Neu laden | Der Host lädt den Wegwerfstand erneut. Der Joiner folgt in die neue Host-Welt. Erst nach Abschluss des Ladeablaufs die Partnerfiguren und ihre Lage erneut prüfen. |
| Trennen | Unterbrecht die Verbindung des Joiners zum Test-Relay kurz, während Spiel und Agent auf beiden PCs offen bleiben. Prüft am Host und am Joiner, was bei der Trennung und der Rückkehr passiert. Für die technische Prüfung wartet ihr auf die bestätigte Rückkehr in dieselbe Welt. Spiel und Launcher bis zur Ausgabe der lokalen Berichte geöffnet lassen. |

Die Spielkonsole zeigt Stand und nächste Aktion an. `wiederholen` zeigt die aktuelle Station erneut an. Die fünf Sicht- und Spielstationen brauchen eine ausdrückliche Beobachtung. Bei **Neu laden** und **Trennen** kann `weiter` ohne Zusatz einen auf dem jeweiligen PC vorliegenden technischen Nachweis übernehmen. Beim Laden sind das ein frischer Ladebeginn und der bestätigte Startzustand der neuen Generation. Beim Verbindungsschnitt sind es der erkannte Schnitt und die bestätigte Rückkehr in dieselbe Welt und Generation innerhalb von 30 Sekunden ohne erneutes Laden. `weiter ja` oder `weiter nein` dokumentiert an beiden Stationen eine manuelle Beobachtung.

```text
mp_testlauf --test-guided-run status
mp_testlauf --test-guided-run weiter ja
mp_testlauf --test-guided-run weiter nein
mp_testlauf --test-guided-run weiter
mp_testlauf --test-guided-run wiederholen
mp_testlauf --test-guided-run abbruch
```

Die ausführlichen Prüfpunkte für [den zweiten Henry](ABNAHME-S2.md) und [Weltübergänge und Ereignisse](ABNAHME-S3.md) gelten für die spätere Abnahme. Dazu gehören der Rollenwechsel, der Lauf mit 150 ms Zusatzlatenz und Jitter sowie die gesonderte Prüfung des Wiederverbindens.

## Zwei bereits verbundene Rig-Kopien verwenden

Für die lokale Probe vor dem Start der beiden Agenten zusätzlich `KCDMP_RIG_TEST_ENV` mit dem Eintrag `KCDMP_TEST_GUIDED_RUN=1` setzen. Den Testschalter auch in der PowerShell des Rigs setzen. Nach dem Beitritt startet der folgende Befehl beide geführten Läufe über die Spielkonsole. Ihr führt danach dieselben Stationen im Spiel aus.

```powershell
$env:KCDMP_TEST_GUIDED_RUN = '1'
$env:KCDMP_RIG_TEST_ENV = 'KCDMP_TEST_GUIDED_RUN=1'
python tools/rig/scenarios.py testlauf --test-guided-run
```

Der Lauf wartet bis zu 20 Minuten auf beide Abschlüsse. Er prüft die Rollen und die neuen Laufkennungen, öffnet die lokalen T1-Berichte und erstellt daraus die Paar-Ampel im Szenarioordner.

## Protokolle lesen

Nach dem letzten angenommenen `weiter` startet auf dem jeweiligen PC automatisch ein lokaler Collect-Logs-Lauf. Der Ausgabeordner unter `%LOCALAPPDATA%\KCDMP\Testlauf` hat das Format `testlauf-<Datum>-<Uhrzeit>-<Rolle>-<Lauf-ID>`. Darin liegen die geführten Beobachtungen in `TESTLAUF.json`, die Ampel in `ERGEBNIS.txt` und das Log-Bundle als ein oder mehrere `.tzst`-Teile mit `BRIEFING.md` im ersten Teil. `status` zeigt nach der Sammlung das Ergebnis an.

Im Ergebnis die Rolle des jeweiligen PCs und die Messabdeckung prüfen. `owner_violations` und `dmg_double` müssen bei beobachteten Aktionen null sein. Bei einem erfassten neuen Ladeablauf Welt und Generation prüfen. Grau kennzeichnet fehlende Messung oder Abdeckung. Die eigene Ampelzeile `guided_run_protocol` wird rot, sobald eine Station mit `nein` bewertet wurde. Das Stationsprotokoll setzt mit `start` ein. Die technische Ampel liest die Protokolle der aufgenommenen Spielausführung bis zur Sammlung.

Für die Paar-Ampel die beiden lokalen Ausgabeordner desselben Laufs auf einen Rechner kopieren. Es genügt, die beiden `ERGEBNIS.txt` in getrennten Ordnern zu haben. Aus dem Checkout zuerst den Host-Ordner und dann den Joiner-Ordner übergeben.

```powershell
python tools/verdict/pair.py "$HostBundle" "$JoinerBundle"
```

`ERGEBNIS-PAAR.md` entsteht im aktuellen Verzeichnis. Exitcode `0` steht für Grün, `1` für einen grauen, gelben oder roten Paarbefund und `2` für einen Eingabe- oder Schreibfehler. Die Sammlung speichert die Berichte auf dem jeweiligen PC. Für den Vergleich wählt ihr selbst, welche Dateien ihr austauscht.

**Halt vor der Abnahme.** Mit den Werkzeugen endet der Umsetzungsauftrag. Lasse startet die Zwei-PC-Abnahme, sobald beide PCs verfügbar sind. Die Löschung ersetzter Pfade und weitere NPC-Aktivitäten folgen erst nach dieser Abnahme.
