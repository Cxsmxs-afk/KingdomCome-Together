# P4: frische Teleportziele und begrenzte Recovery

Basis ist P3 `bfd9500b`, Branch `codex/t3-p4-respawn-wake`, PR gegen `codex/t3-p3-pickup-freeze`. Die Rohlogkorrektur für Tod 2/3 und den 115-m/s-Fall steht in [T3-P4-LOGFINDINGS.md](T3-P4-LOGFINDINGS.md).

## Was ersetzt wird

Die TP-Entscheidung verwendet nicht mehr das beim Dequeue frisch gestempelte `_ghostLastPos`, den bis zu 30 Sekunden alten Leash-Fallback oder einen ungeprüften Exact-Spot. Die mindestens 100 m entfernte Hangover-Wake-Auswahl und spätere ungeprüfte TP-Wiederholungen entfallen. Der alte Home-Aufruf mit drei Sekunden Verzögerung und 31 direkten Lua-Positionsschreibvorgängen entfällt vollständig; Henry und Inventar kommen weiterhin über das geprüfte eigene Save zurück. Die alte Lua-API lehnt verspätete Aufrufe ohne Bewegung ab.

Auch Video-Landing, Video-Rollback und Scene-Nudge laufen über denselben begrenzten nativen Platzierungspfad. Die Lebenszeit eines Auftrags beginnt vor seinen Wartezeiten; neue Welt, neues Leben oder ersetzte Szene dürfen keinen alten Auftrag aufwerten.

## Herkunft und zwei Sekunden

Für einen TP fordert der Agent beim Partner einen Nonce-Snapshot an. Der Partner liest Position, Kampf-, Todes-, KO- und Physikzustand tatsächlich neu im Spielprozess. Erst die passende Antwort derselben Verbindung, desselben Peers und derselben Welt gilt. Unbekannte oder unsichere Werte geben kein Ziel frei. Die normale Bewegungsnachricht bleibt unverändert.

Der lokale Beginn der Anfrage ist eine konservative Untergrenze für den späteren tatsächlichen Read beim Partner. Die unveränderliche Deadline ist deshalb **Anfragebeginn + 2000 ms**, nicht Antwortzeit + 2000 ms. Warteschlangen, Pipe-Gate, physischer Writer und native Mainqueue dürfen sie nicht verlängern. C# prüft zusätzlich Verbindungs-, Welt-, P1- und Lebensgeneration; Native prüft Generation, Gameplay und letzte echte Lebens-/Spieleridentitätskante unmittelbar vor dem Schreiben erneut. Ein alter Aufruf, der erst nach Tod und Wake am Pipe-Reader ankommt, wird ebenfalls verworfen.

Die lokale .NET-8-/Windows-Uhr verwendet dieselbe GetTickCount64-Basis wie die DLL ([Runtime-Quelle](https://github.com/dotnet/runtime/blob/v8.0.0/src/coreclr/classlibnative/bcltype/system.cpp#L41-L46)). Remote-Uhren werden nicht voneinander abgezogen. SenderMs und eine gute durchschnittliche Latenz beweisen kein absolutes Alter einer alten Position.

Der Host erhält einen belegten Gameplay-Receipt auch bei deaktivierter optionaler Story-Autorität: `Disabled(newGen)` invalidiert, tatsächliches `Gameplay(sameGen)` bestätigt Readiness bei `enabled=false, active=false`. Das aktiviert keine Story-/Decision-Gates. Ein Attach an einen bereits geladenen Host darf einmal eine echte Gameplay-Kante aus dem aktuellen Logprozess nachweisen; Load, Menü, Quit oder Prozessneustart verwerfen sie. Role, REST-Erreichbarkeit und Timeouts ersetzen diesen Beweis nicht.

## Boden, Kampf und Wake

Platzierung prüft endliche Koordinaten, einen Abstand von höchstens 30 m zum frischen Partnerziel und höchstens acht bestehende Navmesh-Bodenproben. Auch Exact-Spot braucht diese Prüfung. Der originale Todes-/Quell-Z bleibt erhalten: Sowohl sein Abstieg zum Boden als auch die Zielkorrektur müssen die Zwei-Meter-Grenze erfüllen. Die Logs zeigen beide Werte sowie signierte Höhenänderung, Originalziel, geprüftes Ziel, Abstand, Alter und tatsächliches Schreibergebnis.

Normale automatische Teleports passieren keine unbekannten Kampf-/KO-/Physikwerte. Native prüft den lokalen Zustand vor dem Schreiben; der frische Peer-Snapshot liefert den Partnerzustand. Das ist keine behauptete Vollprüfung jedes feindlichen NPC im Zielraum. Ein neuer reflektierter Weltvollscan wurde ausdrücklich entfernt, damit dieser Pfad keinen neuen Framehänger erzeugt.

Der autorisierte Death-Wake ist ein eigener interner Recovery-Pfad. Ohne frischen Partner oder passenden Boden erfolgt kein ferner Ersatzteleport; die Recovery bleibt am bestehenden Ort und meldet den fehlenden sicheren Landepunkt. Ein fehlgeschlagener TP wird nicht als erfolgreiches partnernahes Wake gezählt.

Nach einer tatsächlichen Platzierung hält die gemeinsame Fall-Lease bis zu einer Sekunde ununterbrochen belegter Boden-/Physikwerte, höchstens 20 Sekunden. Alte Ground-Caches zählen am neuen Ort nicht weiter. Vorwerte der CVar werden nur über den gemeinsamen Besitzer wiederhergestellt. Ein unbekannter Integerwert gibt keine neue Lease frei; ein abweichender Fremdwert wird beim Release bewahrt. Fremde Schreibvorgänge desselben Werts sind ohne eigenen Hook nicht beobachtbar. Die vorhandene Navmeshprojektion ist ein Bodenproxy; sie beweist keinen unsichtbaren physikalischen Kontakt. Unbekannte Werte zählen nicht als gelandet.

## Fünf Sekunden: belegter Schutz und Grenze

Unmittelbar vor Freigabe werden volle HP und das Entfernen der Blutung nochmals mit den vorhandenen Engine-APIs angefordert und nachgelesen, auch nach KO. Nur bestätigter Guard und Restore aktivieren das fünfsekündige, spieler- und weltgebundene Recoveryfenster. Beide Mod-Respawn-Einstiege und die eigenen PlayerHenry-/PvP-Damage-Blätter beachten dieses Fenster. Geschützte Mod-Treffer werden geordnet konsumiert, verursachen dort null HP-/Staminaschaden und werden danach nicht nachgespielt oder als angewendeter Schaden gutgeschrieben. Laden, Disconnect, Deaktivierung oder neue Identität beenden alte Recoveryarbeit samt eigenen Leases.

**Vollständiger Schadensschutz gegen sämtliche Vanilla-Schadenspfade ist damit nicht nachgewiesen.** Der bestehende Immortality-/Unconsciousness-Guard ist eine Überlebensuntergrenze. Die installierten Waffen-Hooks müssen dem Caller einen gültigen Engine-Cause zurückgeben; ein unvalidiertes Early-Return crashte bereits in früheren Liveproben. Es wird weder ein solcher Rückgabewert noch ein zentraler Health-Write-Hook erfunden. Logs nennen `vanilla_zero_damage=unverified`. Dieser Teil des Auftrags bleibt für vollständige Abnahme offen und benötigt einen frisch validierten Enginehebel sowie den echten Spieltest.

## Diagnose und Prüfung

Die DLL liefert 19 unabhängige P4-Felder über den leeren Op250-Statusrequest als 173-Byte-TLV. Das passt in das vorhandene 512-Byte-Antwortbudget und verändert weder Pipe-Caps noch das 60-Sekunden-Limit des Agent-Mirrors. Der Agent importiert nur vollständige, gültige Snapshots und behält bei Fehlern die letzte gute Messung. Alle Pflichtfelder erscheinen im 60-Sekunden-`MP-T3-STATS` beider Rollen und des DLL-Mirrors. Meter-Maxima werden konservativ auf ganze Meter aufgerundet; Detail-Logs behalten Nachkommastellen.

`tp_refused_stale` misst die native Write-Fence. `tp_snapshot_timeouts` und `tp_snapshot_refused` messen vorgelagerte Agent-Anfragen separat; sie werden nicht doppelt aufsummiert. `engine_teleport_lines` zählt zusätzlich echte Engine-Logzeilen auch außerhalb des Quest-Catchups, ohne sie als bewiesenen Mod-TP zu klassifizieren. Der Tod-nach-TP-Marker und sein Zähler kennzeichnen die zeitliche Nähe; `cause`/`evidence` unterscheiden belegten jüngeren Mod-Damage-Aufruf, Fall-/Blutungsinferenz und unbekannte Ursache.

Lokal bestanden: 220/220 Client-Regressionen, 6/6 zusätzliche Statistik-/Pipe-Checks, 2/2 echte Relay-Rundläufe, 84 Lua-Suiten mit 5424/5424 Checks. Native: 462/462 mit strikten Clang-Warnings sowie ASan/UBSan, darunter 48 Ablaufprüfungen am tatsächlichen p4.cpp. Drei geänderte Runtime-TUs wurden zusätzlich mit Windows-Deklarationsstubs syntaktisch geprüft; das ersetzt den echten MSVC-Build nicht. Vollständige Windows-CI und Drop-in-Nachweis werden im PR vor Paketabschluss ergänzt. Offen im Spiel: zwei PCs, Brücken/Stockwerke/Hänge, Streaming ohne gültigen Boden, Kampf/KO während Warteschlangen, wiederholte Tode, Fall-Lease mit fremder CVar-Belegung, echte Vanilla-Schadensquellen und der bisher unbekannte Writer des 115-m/s-Ereignisses.
