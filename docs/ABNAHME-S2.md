# Schritt 2 · Abnahme des zweiten Henry

## s2-1 · Vergleichsmessung auf einem PC

`rpmeasure` nutzt den Pfad, der im Build für den zweiten Henry bereits aktiv ist. Vor dem Start der Route liest das Rig die Position des lokalen Spielers am Empfänger. Drei endliche Koordinaten belegen, dass die Figur in der Welt erreichbar ist. Danach schaltet das Rig den gemeinsamen Rechner-Takt mit `mp_rp_measure_clock on` frei und wartet auf die Bestätigung von Agent und DLL. Der Standard ist `off`. Nach Abschluss und Log-Sammlung schaltet das Rig auf `off` zurück. Der Spieltest mit zwei PCs steht aus.

### Route und Start

Eine Treppe in der Testwelt auswählen. Die Wegpunkte außerhalb des Checkouts in einer JSON-Datei als `points` mit Zeilen `[Sekunden, x, y, z]` eintragen. Der erste Zeitpunkt ist `0`, der letzte `60`. `stairs` enthält Beginn und Ende des Treppenabschnitts in Sekunden. Beide sind Wegpunkte. Drei einzelne Höhenstufen oder mehr in derselben Richtung markieren. Gelände und Sicht vor Ort prüfen. Der Sender startet höchstens drei Meter vom ersten Wegpunkt entfernt. Den Empfänger so stellen, dass er Weg und Treppe sehen kann.

Die beiden folgenden Werte vor dem Start der Modding-Tools-Instanzen und ihrer Agents setzen. `KCDMP_RIG_RUN` benennt den Lauf für alle folgenden Rig-Aufrufe. Für jeden Vergleich einen eigenen Namen wählen.

```powershell
$env:KCDMP_TEST_RP_MEASURE = '1'
$env:KCDMP_RIG_TEST_ENV = 'KCDMP_TEST_RP_MEASURE=1'
$env:KCDMP_RIG_RUN = 'rpmeasure-normal'
python tools/rig/pair.py relay --netz normal
```

Beim üblichen `pair.py prepare`, `up`, `host` und `join` bleiben Save und Installationspfad lokale Testdaten. Mit zwei verbundenen Instanzen fährt der Joiner die 60-s-Route über eine Lua-Timerfolge. `--sender host` kehrt die Messrichtung um.

```powershell
python tools/rig/scenarios.py rpmeasure --test-rpmeasure --route $Route --netz normal
```

`--spieler` verwendet dieselbe Route mit einem Menschen an der Sender-Instanz. Der Start erscheint im Rig-Terminal. Der Spieler läuft ab diesem Zeitpunkt entlang der Wegpunkte. Das Werkzeug liest die Positionen und nutzt den WO-220-Knochensampler auf beiden Instanzen. Eine belegte Schrittperiode je Körper bei vergleichbarer Geschwindigkeit ergibt das Verhältnis der Perioden in `rp-step.log`. Die synthetische Lua-Folge und der Bot liefern Bahn- und Zeitdaten. Natürliche Schrittzyklen brauchen auf beiden Seiten echte Skelettmessungen.

Für die Bot-Variante bleibt die zweite Spielinstanz aus. Nur das Relay und einen geladenen echten Host starten. Der T4 Bot-Joiner lädt denselben Stand und verwendet dieselben Wegpunkte.

```powershell
$env:KCDMP_TEST_BOT = '1'
python tools/rig/scenarios.py rpmeasure --test-rpmeasure --route $Route --netz normal --bot
```

Für den Schlecht-Netz-Lauf die Instanzen und das Relay mit `pair.py down` beenden und mit neuem `KCDMP_RIG_RUN` wieder starten. In beiden Befehlen `--netz schlecht` setzen. Das Relay lädt den Testplan für beide Richtungen des Joiners mit 150 ms Zusatzlatenz und 0–35 ms Jitter. Das Szenario prüft im aktuellen Relay-Log, dass die Regel aktiviert wurde und Frames verzögert hat. Eine Änderung des Netzprofils braucht einen frischen Relay-Start.

Das Ergebnisverzeichnis enthält `rpmeasure.json` mit der ersten frischen 60-s-Statistik nach der Route und der Messzeile des empfangenen Henry. Die Relay-ID des Senders stammt aus dem Agent-Log oder aus dem laufenden Bot. `rp_e2e_n` muss im frischen Messfenster für genau diese Figur steigen. Ein Verbindungswechsel während des Laufs oder ein `owner_violations`-Messwert über null ist ein Fehler. Die gebündelte T1-Wertung umfasst den Spielstart. Mit zwei Instanzen liegt auch `ERGEBNIS-PAAR.md` beim Szenario. Fehlende Zyklen, Aktionen oder Besitzerbelege bleiben grau. Die Latenz-Ampel für p95 ist unter 150 ms grün, unter 300 ms gelb und ab 300 ms rot.

### Messgrenzen

| Messwert | Beobachtung |
| --- | --- |
| `rp_e2e_ms_p50`, `rp_e2e_ms_p95`, `rp_e2e_ms_max` | Vom Original-Zeitstempel bis zum erfolgreichen Positionsschreibaufruf auf dem Empfänger. Der Sender-Zeitstempel muss zur gemeinsamen Rechneruhr gehören. Die Bildausgabe bleibt im Spiel zu prüfen. |
| `rp_agent_hop_ms_p95` | Vom vollständig gelesenen Relay-Frame im Agenten bis zum ersten gelesenen Pipe-Byte in der DLL. Die folgende DLL-Wartezeit steckt in einem anderen Teil der Strecke. |
| `rp_corr_n`, `rp_corr_cm_p95`, `rp_corr_cm_max`, `rp_hard_place` | Bestehende Korrekturerkennung des Writers bei erfolgreicher Platzierung. Die Zählgrenze für einen harten horizontalen Sprung liegt bei 8 m. |
| `rp_action_lag_ms_p95` | Von der Pipe-Ankunft bis zum angenommenen nativen Swing- oder Ranged-Request. Sichtbare Animationsphasen brauchen eine Spielmessung. |
| `rp_step_ratio`, `owner_violations` | Der Schrittvergleich braucht zwei beobachtete Skelette mit vergleichbarer Geschwindigkeit. Der Besitzer-Zähler erhält seine prüfbare Abdeckung in s2-2. |

Beim Einschalten beginnt ein frisches Messfenster. Bei einem bereits geladenen Host, dessen Agent erst später startet, beginnt die Erfassung mit der ersten erfolgreich auf die bestätigte Partnerfigur angewendeten Pose. Ein Lade-, Rollen- oder Pipe-Wechsel beendet die Freigabe. Für einen neuen Lauf muss das Rig sie erneut aktivieren. Die DLL schreibt pro beobachteter Figur eine Messzeile, der 60-s-Bericht enthält die Zusammenfassung der Rolle. Die Simulation misst separat auf beiden Seiten bis zum Positionspaket am Relay-Client. Die Schwelle von 150 ms prüft dabei den Nachweis von guter und verschlechterter Testleitung.

### Zwei-PC-Checkliste

1. Dieselbe Modding-Tools-Version und denselben Drop-in auf beiden PCs starten. Einen ruhigen Test-Save und die ausgewählte Treppe verwenden.
2. Verbinden und prüfen, dass jede Seite den anderen Henry in der Welt sieht.
3. Der Joiner läuft die 60-s-Route mit der Treppe. Das Tempo auf einem ebenen Abschnitt für mehrere Sekunden halten. Beide Logs sammeln.
4. Die Route mit dem Host als Sender wiederholen. Sichtbare Korrekturen, harte Versetzungen und Laufbild pro Richtung festhalten.
5. Pro Bundle Messquelle und Stichproben prüfen. Absolute Ende-zu-Ende-Zeit braucht eine belegte gemeinsame Uhr oder synchronisierte Zeitbasis. `mp_rp_measure_clock` ist die lokale Testfreigabe für einen gemeinsamen Rechner-Takt. Die Freigabe bleibt auf getrennten Rechnern `off`.

## s2-2 · Besitzerwechsel pro Körper

Für das Rig mit zwei Spielinstanzen einen Wegwerfstand mit Bank und Gelegenheit für eine Prügelei laden. Beide Henrys bleiben in Sichtweite. `ownerhandover` meldet die einzelnen Schritte im Terminal. Der Spieler am Sender führt sie im Spiel aus. Das Rig liest den Sitzwechsel aus dem Agent-Log und den KO, das Aufwachen und den Pferdestatus über die Spielkonsole. Beim Empfänger sucht es im nativen Log nach den passenden Übergängen derselben Partnerfigur und Körper-ID. `--reiten` ergänzt Auf- und Absteigen, wenn ein Pferd da ist. `--tod` ergänzt als letzten Schritt einen Tod im Wegwerfstand. `--sender host` vertauscht die Messrichtung.

```powershell
$env:KCDMP_TEST_OWNER_HANDOVER = '1'
python tools/rig/pair.py relay --netz normal
python tools/rig/scenarios.py ownerhandover --test-ownerhandover --netz normal
```

Für den Bot nur das Relay und den geladenen Host starten. `$Anlauf` enthält die Koordinaten `x,y,z`. Der Bot geht von dort drei Meter in Richtung `+Y` zur Bank. `$BankGuid` enthält die 16 Hex-Ziffern `obj=` aus `WO164-SITSTATE enter` des echten Sitzversuchs im selben Teststand. Der T4 Bot-Joiner sendet Sitz-, KO- und Aufstehzustände. `--horse` mit dem Namen eines erreichbaren Pferds ergänzt das Reiten. `--tod` sendet am Ende den Tod. Der Bot belegt den Versand der Skriptzustände. Belege für einen Wechsel des Halters kommen aus der echten Empfänger-DLL.

```powershell
$env:KCDMP_TEST_BOT = '1'
python tools/rig/scenarios.py ownerhandover --test-ownerhandover --netz normal --bot --origin $Anlauf --seat $BankGuid
```

Beide Varianten mit einem neuen `KCDMP_RIG_RUN` und einem frischen Relay für `--netz schlecht` wiederholen. Das Rig prüft 150 ms Zusatzlatenz und 0–35 ms Jitter anhand des im aktuellen Relay geladenen Plans und der verzögerten Frames. `ownerhandover.json` enthält die angeforderten Schritte, Belege des Senders, native Übergänge und die Differenz der Zähler vor und nach dem Lauf. Das LogPack-Bundle enthält den T1-Bericht. `owner_violations` über null ist rot. Null mit belegten Setter-Prüfungen ist grün, fehlende Abdeckung grau. `owner_handovers` und `owner_denied_sets` zeigen die beobachteten Übergaben und abgewehrten Setter. Der Lauf braucht zusätzlich alle angeforderten Übergaben derselben Partnerfigur. Die tatsächlichen Bewegungen am Übergang bleiben im Spiel zu prüfen.

### Zwei-PC-Checkliste

1. Denselben Build verwenden. Beide Henrys in einem Wegwerfstand bei Bank und Pferd verbinden.
2. Der Sender geht, setzt sich und steht wieder auf. Am Empfänger den Wechsel vom Writer zum Sitz und zurück prüfen. Auf sichtbare Sprünge oder Rutschen achten.
3. Der Sender geht in einer Prügelei KO und steht wieder auf. Der Empfänger prüft, dass dieselbe Figur zu Boden geht, liegen bleibt und dort wieder aufsteht.
4. Auf- und absteigen, wenn ein Pferd verfügbar ist. Den Übergang von `mount` zurück zum Writer und die Stelle des Abstiegs prüfen.
5. Einen realen Tod im Wegwerfstand auslösen. Prüfen, dass die alte Figur liegen bleibt. Danach den Stand neu laden.
6. Rollen tauschen und die Logs beider PCs sammeln. `owner_violations` bleibt bei null, `rp_owner_n` steigt. Abgewehrte Setter und zusätzliche Halterwechsel mit den beobachteten Aktionen abgleichen.
