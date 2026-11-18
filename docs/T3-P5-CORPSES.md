# P5: Leichen und Todesortmarker

Basis ist P4 `0cacd7a`, Branch `codex/t3-p5-corpses-marker`, PR gegen `codex/t3-p4-respawn-wake`. Es gibt keinen neuen Schalter. Die Funktionen folgen der bestehenden Host-/Kopien- und Weltzuordnung.

## Befund aus den hochgeladenen Logs

S1 `KCDMP-logs-20261117-234935/kcd.log:57841,58826` bestätigt zwei gezeigte tote Kopien (`WO217-CORPSE ... here=shown dead=true`). S2 `KCDMP-logs-20261117-235809/kcd.log:14472` bestätigt ebenfalls eine. Die Leichenfunktion war also nicht grundsätzlich ausgefallen. In S1 `kcdmp-native.mirror.log:13304` steht um 23:38:05.926 noch `marker=yes` für ein echtes Grab. Dieselbe Datei `:19718-19719` um 23:44:19.480 und `:22312-22313` um 23:48:44.475 zeigt dagegen jeweils `nothing to bury ... -- no grave`; der alte Aufrufer endete dort ohne Todesortmarker. Duplikate derselben Sitzung und rotierte Altlogs werden dafür nicht addiert.

## Sichtbare Körper

Native und Lua prüfen die tatsächliche aktuelle Körperidentität und den tatsächlichen Todeszustand vor Hide, Parken oder Entfernen. Ein bestätigter Tod bleibt über das 60-Sekunden-Fenster der alten Leichenliste hinaus geschützt. Ein späterer tatsächlicher Alive-Read oder eine ersetzte Identität beendet nur diesen Eintrag. Ein Host-Dead-Signal allein macht aus einer noch lebenden, auf Death-Apply wartenden Kopie keine sichtbare Leiche. Unbekannte Reads erlauben keinen neuen destruktiven Aufruf.

Lua unterscheidet gleichnamige Welt-NPCs und alte Stand-ins anhand Entity-Referenz, ID, Soul-/Stand-in-Token und Weltrevision. Kehrt der originale NPC zurück, darf die Bindung des alten Stand-ins enden, ohne dessen tatsächlich tote Instanz zu entfernen. Die Register werden bei echten Load-Kanten erneuert, nicht beim Tod oder Aufwachen des lokalen Henry. Native liest in der vorhandenen Census-Runde bereits erhobene Werte mit; beide Backstops prüfen höchstens acht vorhandene Einträge pro Tick und erzeugen keinen neuen Weltvollscan. Es entsteht weder ein neuer Kill-/FATAL- noch ein zweiter Death-Writer.

## Ein Marker pro Todesort

Ein echtes Grab und ein reiner Todesortmarker sind ausdrücklich verschiedene Fakten. Die neuen typisierten Nachrichten führen Art, Welt, ID und Revision mit. Der Markerpfad darf deshalb ohne geplündertes Inventar und ohne originales Grave-Linkable einen vorhandenen, geprüften SmartObjectHolder als eigenen Anker verwenden; daraus entsteht kein fingiertes plünderbares Grab. Alte Grave-Nachrichten bleiben auf wirkliche Gräber beschränkt.

Neue lokale Tode werden ausschließlich über das generation- und weltgebundene B0-Ereignis publiziert. Alte ungegenerte Native-0x93-Ereignisse werden diagnostiziert, aber nicht nach einem Load in die neue Netzwelt umgedeutet. Empfangene alte Grave-Nachrichten sind weiterhin nur echte Gräber und benötigen ebenfalls den passenden Reader-/Loaded-Receipt sowie den neuen an Generation und Welt gebundenen Op253. Gemischte alte DLLs ersetzen diesen Nachweis nicht; die zusammengehörigen Dateien kommen im Drop-in.

Fehler werden pro Marker isoliert. Die bisherigen acht Fehler einer gemeinsamen Engine-Callsite dürfen nicht sämtliche Marker abschalten. Unsichere Remove-/Membership-Ergebnisse geben keine Freigabe zum Löschen eines noch von der Map referenzierten Ankers. Der Marker bleibt dann quarantänisiert. Das ist kein Beweis, dass die Engine einen solchen Anker bei eigenständigem Streaming oder einem unkontrollierten Vanilla-Load am Leben hält.

Der neue Native-Status liefert typisierte Seiten mit höchstens 16 Einträgen und maximal 485 Byte pro Antwort. Reconnect und Peer-Ready stoßen eine Reparatur an; Revisionen und Remove-Tombstones verhindern, dass ein älterer Snapshot einen entfernten Marker wiederherstellt. Die Anwendungsgrenze ist die tatsächlich geladene Welt, nicht eine bereits empfangene zukünftige Host-Welt. Die P1-Generation begrenzt ausstehende Schreibaufträge und ist kein Ersatz für diesen Weltbeleg.

Speicher und Wiederholungen bleiben begrenzt: 128 eigene aktuelle Fakten, höchstens 384 aktive Remote-Fakten für drei Mitspieler und ein getrenntes Revisionsjournal mit 4096 Einträgen. Die bis zu 64 wirklichen Lootgräber werden nicht zur Markerrotation gelöscht. Bei 128 Fakten wird der älteste reine Ortsmarker mit einer neueren Remove-Revision ausgetauscht. Quarantänisierte Renderer bleiben bis zur bestätigten Bereinigung erhalten. Noch nicht fertige Identitäten/Linkables bekommen höchstens 20 Versuche innerhalb von zwei Sekunden; echte Enginefehler lösen keine dauernde Neuerzeugung aus.

## Laden und offene Enginegrenze

Die vier vom Mod ausgelösten Ladepfade verwenden einen gemeinsamen Main-Thread-Detach mit echter Antwort vor dem Load. Ein nicht sicher abgetrennter Marker stoppt diesen Load mit verständlichem deutschem und englischem Hinweis. Nach der Bestätigung bleiben neue Marker gesperrt, bis eine echte geladene Welt bestätigt ist. Ein beim kalten Agent-Neustart vom Native zurückgegebener Scope darf ausschließlich die Bereinigung unterstützen; er wird nicht zur Weltbestätigung oder Reannounce-Erlaubnis aufgewertet.

**Offen bleibt die Lebensdauer bei unkontrolliertem Vanilla-Load oder Engine-Streaming.** Die vorhandene Map hält ein rohes Linkable. Es gibt keinen frisch verifizierten universellen Preload-/Mapdraw-/Entity-Retention-Hook. C++-Quarantäne allein verhindert keine engineeigene Entity-Zerstörung. Diese Grenze und die wirkliche Kartenanzeige müssen im Spiel geprüft werden. Ein kalter Agent-Neustart ohne vorhandenen Loaded-Receipt darf alte Marker behalten, aber nicht eine unbekannte geladene Welt für Remote-Anwendung behaupten.

## Messung

Der leere Op250-Statusrequest erweitert den stabilen 19-Felder-P4-Präfix um die atomare P5-Gruppe: `corpses_visible`, `corpse_hidden_blocked`, `grave_markers_added`, `grave_marker_fallbacks`, `markers_off_session`. Die Antwort ist 218 Byte und bleibt innerhalb der vorhandenen 512-Byte-Grenze. Ein halber oder ungültiger Snapshot ersetzt keine letzte gute Messung. Ein altes P4-Statusformat bleibt lesbar, wird aber durch `corpse_stats_unreadable` als fehlender P5-Nachweis markiert.

`corpses_visible` ist der native Gauge tatsächlich beobachteter, bestätigter sichtbarer Leichen; kein behaupteter weltweiter Gesamtbestand. Lua meldet den unabhängig beobachteten Satz als `lua_corpse_visible` sowie kumulative `lua_corpse_hidden_blocked` und `lua_corpse_unknown_kept`. Native und Lua werden nicht addiert. Sichtbarkeit erfordert Readback; ein erfolgreicher Hide(0)-Aufruf genügt nicht. Alle Felder erscheinen in der 60-Sekunden-Zeile `MP-T3-STATS` des Agents und des DLL-Mirrors beider Rollen und bleiben im Collect-Logs-Bundle erhalten. `markers_off_session` muss null bleiben.

## Prüfung

Lokal bestanden: 185 Client-Regressionen einschließlich aller T3-/Host-Authority-Fälle und der neuen Pipe-, Reader-, Loaded-Receipt-, Legacy- und Load-Barrier-Prüfungen; sieben echte Relay-Rundläufe; alle 84 Lua-Suiten mit 5455/5455 Checks; Log-Bundle-Roundtrip einschließlich sämtlicher P5-Felder. Der gemeinsame native Lauf verbindet den tatsächlichen P4-/P5-Runtime-Code: 231/231 Checks unter strikten Clang-Warnings sowie ASan/UBSan (48 P4-Abläufe, 120 Markerabläufe, 20 Statistik-, 43 Leichenprüfungen). Drei zusätzliche echte Windows-Fault-Registry-Prüfungen sind für die CI registriert. Vier tatsächliche Runtime-TUs wurden mit Windows-/Fault-/Log-Deklarationsstubs syntaktisch geprüft; das bestätigt keinen Engine-ABI-Aufruf.

Windows-CI [44238201571](https://github.com/Cxsmxs-afk/KingdomCome-Together/actions/runs/44238201571) auf Head `329e440355f8285933fb71374610b9dd87b863e9` ist vollständig grün: Client 2441, Relay 245, Setup 110, LogPack 105, Native 11523, alle 84 Lua-Suiten und Simulation 6/6. Artifact [14399380513](https://github.com/Cxsmxs-afk/KingdomCome-Together/actions/runs/44238201571/artifacts/14399380513) enthält das Drop-in. Die CI öffnet das tatsächliche ZIP und prüft Pflichtdateien sowie die erste Zeile `PASS` von `install-verify.txt`.

Sichtbare Karte, wiederholte leere Tode/Execution, Save-/Map-Lebensdauer, unveränderte Welt beim Reconnect sowie zwei echte Windows-PCs sind keine durch Unit-Tests belegte Spielabnahme. Die oben genannte Grenze unkontrollierter Engine-Purges bleibt bestehen.
