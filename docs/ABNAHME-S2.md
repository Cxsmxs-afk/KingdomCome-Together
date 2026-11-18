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

## s2-3 · Echtzeit- und Bulk-Spuren

Einen ruhigen Wegwerfstand auf einer freien, ebenen Fläche laden und beide Henrys verbinden. `lanes` startet auf jeder Spielinstanz eine native Testlast von 30 Echtzeit- und 50 Bulk-Aufträgen je Spielframe für 120 Frames. Die Probe belegt Reihenfolge, Durchsatz und Wartezeit in den nativen Spuren während der verbundenen Spielsitzung. Der Relay-Verkehr läuft parallel. Vor dem Start der Spielinstanzen und Agents die Testfreigabe setzen.

```powershell
$env:KCDMP_TEST_LANES = '1'
$env:KCDMP_RIG_TEST_ENV = 'KCDMP_TEST_LANES=1'
$env:KCDMP_RIG_RUN = 'lanes-normal'
python tools/rig/pair.py relay --netz normal
python tools/rig/scenarios.py lanes --test-lanes --netz normal
```

Für den Bot-Lauf nur Relay und geladenen Host starten. Der T4 Bot-Joiner bewegt seinen Skript-Henry in der Nähe des Hosts. Das Rig startet die native Testlast auf dem Host, sobald der Bot verbunden und bereit ist.

```powershell
$env:KCDMP_TEST_BOT = '1'
python tools/rig/scenarios.py lanes --test-lanes --netz normal --bot
```

Beide Varianten mit neuem `KCDMP_RIG_RUN` und frisch gestartetem Relay für `--netz schlecht` wiederholen. Das Relay verzögert Frames um 150 ms plus 0–35 ms Jitter. Die in `lanes.json` gemessene Spurenzeit beginnt mit der Einreihung in der DLL. Das Rig verlangt pro Spielinstanz 3.600 abgearbeitete Echtzeit- und 6.000 geordnet abgearbeitete Bulk-Aufträge sowie Vorrang der Echtzeitspur bei gleichzeitiger Last. Die p95-Grenze für Echtzeit ist unter 34 ms grün, ab 34 ms rot. Fehlende Messabdeckung bleibt grau in T1. `proc_awaits_per_frame_max`, `http_calls_rt` und `owner_violations` müssen bei null liegen. `pipe_busy_replies` und `inproc_fallbacks` stehen im Bericht. Ein belegter Agent-Hop über 20 ms wird für die Entscheidung über den Netz-Empfang in der DLL ausgewiesen. Die Spieltests stehen aus.

`pipe_busy_replies` zählt die sofortige Ablehnung bei voller Spur und die einmalige Wartemeldung eines bereits angenommenen Auftrags. Ein eigener Pipe-Thread stellt die Wartemeldung ab 30 ms zur priorisierten Zustellung bereit. Die Request-ID bleibt bis zur endgültigen Antwort offen und der Auftrag behält seinen Platz in der Spur. Die Zustellung innerhalb von 50 ms bei blockiertem Spielthread ist auf Windows im Spiel zu prüfen.

### Zwei-PC-Checkliste

1. Beide PCs mit demselben Build und `KCDMP_TEST_LANES=1` in Spiel und Agent starten. Im Wegwerfstand verbinden und die Partnerfigur auf beiden Seiten prüfen.
2. Beide Henrys bewegen. Auf beiden PCs in der Spielkonsole `mp_lanes --test-lanes 3101 120` ausführen. Pro Spielinstanz die Bestätigung des Agents und die native Abschlusszeile zu derselben Nummer prüfen.
3. Pro Abschlusszeile 120 Frames, 3.600 erledigte Echtzeit- und 6.000 geordnet erledigte Bulk-Aufträge zählen. Die Echtzeitspur soll unter gleichzeitiger Last Vorrang haben. Ihre Wartezeit p95 bleibt unter 34 ms.
4. Neu verbinden und mit dem Relay-Testplan für 150 ms plus Jitter wiederholen. Beide Henrys sollen während der Messung weiterlaufen und zuverlässig eintreffende Aktionen zeigen.
5. Beide LogPack-Bundles sammeln. `proc_awaits_per_frame_max`, `http_calls_rt` und `owner_violations` prüfen. Pipe-Busy, In-Process-Fallbacks und Agent-Hop mit den Stichprobenzahlen festhalten.

## s2-4 · Gemeinsame Zeitleiste für Bewegung und Schlag

Beide Henrys in einem Wegwerfstand auf einer freien, ebenen Fläche verbinden. `mp_rp_timeline` steht standardmäßig auf `on`. `timeline` fordert den Sender im Terminal auf, mit gezogener Waffe mindestens zwei Meter zu gehen und anschließend einen normalen Schlag auszuführen. Der Empfänger bleibt in Sichtweite. `--sender host` wechselt die Richtung. Der Test ordnet die gesendete Aktion über Relay-ID, ursprünglichen Sender-Zeitstempel, Sequenz und Host-Zeit derselben Empfängerfigur zu.

```powershell
$env:KCDMP_TEST_TIMELINE = '1'
$env:KCDMP_RIG_RUN = 'timeline-normal'
python tools/rig/pair.py relay --netz normal
python tools/rig/scenarios.py timeline --test-timeline --netz normal
```

Für den Bot-Lauf nur Relay und geladenen Host starten. Der T4 Bot-Joiner kalibriert seine Uhr mit dem Relay und sendet drei Bewegungs- und Schlagfolgen. `--attack-row` kann eine andere passende Schlagzeile wählen. Jede Wertermittlung braucht die von der Empfänger-DLL bestätigte Installation der Aktion sowie den Host-Zeitstempel der tatsächlich angewendeten Pose.

```powershell
$env:KCDMP_TEST_BOT = '1'
python tools/rig/scenarios.py timeline --test-timeline --netz normal --bot
```

Beide Varianten mit neuem `KCDMP_RIG_RUN` und einem frisch gestarteten Relay für `--netz schlecht` wiederholen. `timeline.json` enthält die erfassten Schläge, die zugehörigen nativen Pose-Zeitstempel sowie p95 der absoluten Zeitdifferenzen. Grün liegt unter 20 ms, gelb unter 50 ms, rot ab 50 ms. Fehlende Uhr- oder Installationsbelege bleiben grau. Der Test erwartet grün, vollständige Abdeckung der angeforderten Schläge und `owner_violations=0`. Der Puffer bleibt bei 60–120 ms, Extrapolation bei höchstens 200 ms. Verspätete Proben erscheinen als `rp_late_samples`. Die sichtbare Animationsphase bleibt im Spiel zu prüfen.

### Zwei-PC-Checkliste

1. Auf beiden PCs denselben Build laden und verbinden. Beide Partnerfiguren stehen auf ebener Fläche in Sichtweite.
2. Der Joiner geht mit gezogener Waffe und schlägt unmittelbar danach. Den Vorgang mehrmals wiederholen und den Übergang auf dem Host beobachten.
3. Der Host wiederholt die Folge als Sender. Bei wechselnder Netzqualität auf weiches Halten und sichtbare Sprünge achten.
4. Den Lauf mit 150 ms plus Jitter wiederholen. Beide LogPack-Bundles sammeln. Pro `installed`-Schlag Figuren-ID, Host-Zeit und die zugehörige Pose prüfen.
5. `rp_action_pos_skew_ms_p95`, Stichproben, Puffer und Extrapolation ablesen. Fehlende Proben festhalten und auf beiden Seiten `owner_violations=0` prüfen.

## s2-5 · Engine-Controller für den zweiten Henry

`mp_rp_controller` steht standardmäßig auf `on`. `rpwalk` verwendet eine 60-s-Strecke mit Treppe, Hang und drei Gangarten. `$Route` zeigt auf eine lokale JSON-Datei mit `points` und `stairs` wie bei `rpmeasure`. `slope` enthält Start und Ende eines eigenen Hangabschnitts mit wenigstens 0,5 m Höhenänderung auf 2 m horizontaler Strecke. `gaits` enthält `walk`, `run` und `sprint` mit je zwei Wegpunktzeiten. Die Gangabschnitte dauern mindestens vier Sekunden und liegen in dieser Reihenfolge. Die geplanten Geschwindigkeiten liegen bei 0,5 bis unter 2,4 m/s, 2,4 bis unter 4,0 m/s und 4,0 bis 6,0 m/s. Die Strecke vor dem Lauf im Spiel abgehen. Der echte Sender folgt der Strecke ab dem Rig-Signal, während das Werkzeug seine Position und Geschwindigkeit liest.

`rpstance` benötigt einen stehenden, mit einer Nahkampfwaffe ausgerüsteten Sender und freie Sicht. Das Rig lässt die Spielfigur über den bekannten Konsolenpfad ducken und aufstehen. Danach zieht der Sender die Waffe über den Spielaufruf. Am Empfänger zählen der bestätigte Haltungswechsel des Controllers und das Auslesen der gezogenen Waffe in der Hand der gleichen Körper-ID. `--sender host` wechselt in beiden Szenarien die Richtung.

```powershell
$env:KCDMP_TEST_RP_CONTROLLER = '1'
$env:KCDMP_RIG_RUN = 'rp-controller-normal'
python tools/rig/pair.py relay --netz normal
python tools/rig/scenarios.py rpwalk --test-rp-controller --netz normal --route $Route
python tools/rig/scenarios.py rpstance --test-rp-controller --netz normal
```

Für die T4-Varianten nur Relay und geladenen Host nahe der Strecke starten. `$Waffenklasse` ist die lokale GUID einer ausrüstbaren Nahkampfwaffe. Der Bot sendet die Wegpunkte und Ganggeschwindigkeiten beziehungsweise die Ausrüstung, beide Haltungen und den Ziehbefehl. Die Messungen der Partnerfigur kommen von der echten Empfängerinstanz.

```powershell
$env:KCDMP_TEST_BOT = '1'
python tools/rig/scenarios.py rpwalk --test-rp-controller --netz normal --bot --route $Route
python tools/rig/scenarios.py rpstance --test-rp-controller --netz normal --bot --weapon-class $Waffenklasse
```

Beide Szenarien und Varianten mit einem neuen `KCDMP_RIG_RUN` und einem frisch gestarteten Relay für `--netz schlecht` wiederholen. `rpwalk.json` und `rpstance.json` nennen Körper und Messzeitraum sowie die Zählerdifferenzen. Im Bundle liegt die T1-Ampel. Das Schrittverhältnis ist von 0,9 bis 1,1 grün. Ein Pull-Wert unter 0,2 pro beobachteter Körperminute ist grün. Das Verhältnis bleibt grau, wenn die Fußzyklen bei vergleichbarer Geschwindigkeit fehlen. Das gilt für den Bot ohne Sender-Skelett. Bestätigte harte Teleports außerhalb von Join, Respawn und Reise sind rot. `owner_violations` bleibt null. Die Sichtprüfung von Füßen und Bewegung steht aus.

### Zwei-PC-Checkliste

1. Mit demselben Build verbinden und eine Strecke mit Treppe und Hang wählen. Zuschauer und aktive Figur bleiben in Sichtweite.
2. Die Strecke gehen, laufen und sprinten. Am Empfänger Füße und Bodenkontakt auf Treppe und Hang prüfen. Auf Sprünge und Pulls achten.
3. Stehen, ducken, geduckt bewegen, aufstehen und eine Nahkampfwaffe ziehen. Am Empfänger Haltung und Waffe am selben Henry prüfen.
4. Rollen tauschen und mit 150 ms zusätzlicher Relay-Latenz plus Jitter wiederholen. Bewegungsunterbrechungen und Wechsel auf den Writer im Log festhalten.
5. Beide Bundles sammeln. Engine-Requests, Schrittverhältnis, Pulls und Teleport-Gründe prüfen. `owner_violations` bleibt null, harte Teleports haben einen erlaubten Grund.

## s2-6 · Native Kampfaktionen des zweiten Henry

`mp_rp_combat` steht standardmäßig auf `on`. In einem Wegwerfstand zwei Spielinstanzen verbinden und je nach Szenario Nahkampfwaffe, Bogen oder Armbrust bereithalten. `rpfight` führt den Sender erst zum Partner und dann zu einem lebenden Banditen (`$Bandit`, Entitätsname im lokalen Teststand). Das Rig erfasst Schlag, Block, Parade und Trefferreaktion. `rpdown` beobachtet je Spielrichtung den tatsächlichen KO und das Aufstehen am selben Ort. `rpbow` ordnet eine tatsächlich gesendete, typisierte P6-Schussfreigabe der nativen Animation zu. Ohne `--sender` laufen bei `rpdown` beide Richtungen.

Für die T4-Varianten nur Relay und den echten Host im Wegwerfstand starten. Bei `rpfight` braucht der Bot eine lokale Waffenklasse und passende Aktionszeilen aus dem verwendeten Build. Er nähert sich dem Host und dem geladenen Banditen und spielt feste Aktionen ab. Auch die Trefferreaktion ist ein Skriptimpuls. Belege für angewandte Animation oder Schaden stammen aus dem echten Empfänger. `rpdown` gibt beim Bot den KO und das Aufstehen vor. `rpbow` wählt die Waffenklasse aus der lokalen `Tables.pak` oder prüft optional `--item-class` dagegen.

```powershell
$env:KCDMP_TEST_RP_COMBAT = '1'
$env:KCDMP_TEST_BOT = '1'
$env:KCDMP_RIG_RUN = 'rp-combat-bot-normal'
python tools/rig/pair.py relay --netz normal
python tools/rig/scenarios.py rpfight --test-rp-combat --netz normal --bot --bandit $Bandit --melee-class $MeleeClass --block-row $BlockRow --parry-row $ParryRow --hit-row $HitRow
python tools/rig/scenarios.py rpdown --test-rp-combat --netz normal --bot
python tools/rig/scenarios.py rpbow --test-rp-combat --netz normal --bot --weapon bow
python tools/rig/scenarios.py rpbow --test-rp-combat --netz normal --bot --weapon crossbow
```

Für `--netz schlecht` ein frisches Relay mit dem gleichnamigen Profil verwenden. Die Richtung und native Körper-ID pro Aktion stehen in den JSON-Berichten. Gezählt werden bestätigte Installationen auf der richtigen Figur. Die T1-Ampel wertet ab 95 Prozent grün, fehlende Messbelege bleiben grau. Die Verzögerung beginnt bei der DLL und endet mit dem nativen Nachweis. `dmg_double>0` ist rot. Ein belegter blockierter Doppelpfad mit `dmg_double=0` und `dmg_double_unconfirmed=0` ist grün. Ungeklärte Schadensanwendungen und Läufe ohne belegte Unterdrückung bleiben grau. `owner_violations` bleibt null. Die Spielabnahme steht aus.

Für den Lauf mit echten Spielern nach dem Relay-Start beide Spielinstanzen über `pair.py prepare`, `up`, `host` und `join` wie in s2-1 verbinden. Im Wegwerfstand für den Partnerkampf die Host-Einstellung `mp_friendly_fire on` verwenden. Beim Blockschritt führt der Partner den Gegenschlag aus. Die Szenarien führen durch die einzelnen Aktionen.

```powershell
$env:KCDMP_TEST_RP_COMBAT = '1'
$env:KCDMP_RIG_RUN = 'rp-combat-normal'
python tools/rig/pair.py relay --netz normal
python tools/rig/scenarios.py rpfight --test-rp-combat --netz normal --bandit $Bandit
python tools/rig/scenarios.py rpdown --test-rp-combat --netz normal
python tools/rig/scenarios.py rpbow --test-rp-combat --netz normal --weapon bow
python tools/rig/scenarios.py rpbow --test-rp-combat --netz normal --weapon crossbow
```

### Zwei-PC-Checkliste

1. Mit demselben Build in einem Wegwerfstand verbinden. Sichtweite, Nahkampfwaffen und einen geladenen Banditen prüfen.
2. Schlagen, blocken und parieren, dann eine echte Trefferreaktion auslösen. Die andere Seite beobachtet die Partnerfigur. Spieler- und NPC-Schaden im jeweils zuständigen Spiel prüfen.
3. Nacheinander auf beiden Seiten KO gehen und aufstehen. Die Partnerfigur soll dort aufstehen, wo sie zu Boden ging. Körper-ID und KO-/Aufstehbelege prüfen.
4. Einen Schuss mit Bogen und mit Armbrust abgeben. Waffenart und Freigabe bei der anderen Figur prüfen.
5. Die Abläufe mit 150 ms zusätzlicher Relay-Latenz plus Jitter wiederholen. Beide Bundles sammeln.
6. Bestätigte Aktionen und p95 prüfen. `owner_violations` und `dmg_double` bleiben null. Fehlende oder unbestätigte Nachweise festhalten.
