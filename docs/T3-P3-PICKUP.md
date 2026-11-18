# P3: Aufheben ohne blockierten Nachrichtenpfad

Basis: P1 `ad16c5f`, Branch `codex/t3-p3-pickup-freeze`, PR gegen `codex/t3-p1-intellect-gate`. Der bestehende verlässliche Loot-Wirevertrag bleibt erhalten.

## Befund und Ersatz

In Test 3 antwortete der Host auf die Item-Anfragen mit `unknown`; der in P0 behobene Prozessorstillstand hielt die Antworten zurück. Zusätzlich lagen die W134-Fristen hinter dem W136-Hold. Ein Hold konnte deshalb auch die lokale Auflösung eines ausbleibenden Ergebnisses verhindern. Der bisherige Weg über die normale NPC-/Ghost-Verarbeitung und den Lua-Batch-Sende-Lock entfällt für konkrete Loot-Anfragen und Antworten.

Diese beiden zuverlässigen Nachrichtenfamilien teilen nun eine eigene geordnete FIFO pro Verbindung. Ein blockierter NPC-Handler hält sie nicht auf. Jede Nachricht besitzt dieselbe begrenzte, bei EOF widerrufene Verarbeitungslizenz wie in P0. Queue-Abrechnung erfolgt genau einmal; die neue Spur setzt keine globalen Prozessorstempel. Weltstempel und P1-Ladegeneration stammen aus dem Reader. Vor jedem HTTP-Schritt und in Lua vor einer späteren Anwendung werden sie erneut geprüft. Unbekannte Weltstempel erlauben keine Inventarmutation. Build-Metadaten bleiben vor einem Join verfügbar.

Konkrete Loot-Kommandos verwenden den unmittelbaren HTTP-Weg. Ledger, Props, Carts und andere bisher gemeinsam geroutete Unterarten erhalten dadurch keinen allgemeinen Bypass. Große gültige BodyList-Wireparts werden in lokale Lua-Subchunks innerhalb des vorhandenen 1900-Zeichen-Budgets zerlegt. Die ursprüngliche Teilnummer, Revision und Receipt bleiben erhalten; erst die vollständige Sammlung darf angewendet werden.

## Eingabe und Rejoin

Die Loot-Fristen laufen vor Spieler-/Hold-Abfragen. Originale lose Pickup-Aktionen laufen lokal einmal; Hostantworten lösen sie nicht nochmals aus. Ein bestätigtes `unknown` behält das Item. Ein `gone` darf nur einen nachweislich diesem Pickup zugeordneten neuen WUID oder Stack-Zuwachs zurücknehmen. Ein unbekanntes Inventarergebnis ist kein leeres Inventar. Baseline-Inhalt darf nicht über einen Klassenlöschpfad entfernt werden. Unauflösbare oder noch nicht bestätigte Rollbacks werden ausdrücklich diagnostiziert.

Der Eingabewächter beobachtet erfolgreiche Lua-Aufrufe des tatsächlich verwendeten `ActionMapManager.EnableActionMap`. Er löst nach mehr als drei Sekunden nur eine beobachtete, weiterhin identische Sperre ohne bekannten Eigentümer. Wiederholtes Setzen derselben Sperre verlängert ihre Frist nicht. Szenen, Dialoge, Menüs, Laden, Join, Link-Hold, Downed und neuere Use-Aktionen schützen ihre eigene Lebenszeit. Der echte native Menü-/Downed-Eventzustand wird unabhängig vom Frame-Prozessor nach Lua gespiegelt; alte Seriennummern und Ladegenerationen gelten nicht. Ein Fader alleine belegt keine Eingabesperre und wird nicht pauschal zurückgesetzt.

Die Engine bietet hier keinen verifizierten Getter für sämtliche nativen ActionMap-Schreibvorgänge. Der Wächter deckt beobachtete Lua-Sperren ab; unbekannte interne C++-Sperren werden nicht aus Bewegungslosigkeit geraten. Bei tatsächlich angehaltenen Engine-Timern erfolgt die Lua-Bereinigung erst beim nächsten Callback. `OnEndInteractive` und `AbortUse` sind gezielte Aufräumversuche, kein Beweis einer nativen Entsperrung.

Automatische In-World-Rejoins brauchen eine gegenüber dem geladenen Join-Save erhöhte, bekannte Host-Weltepoche. Ein neuer Transfer-/Join-Zähler, ein Timeout, derselbe Playthrough-Tag oder eine wiederhergestellte Verbindung reicht nicht. Ein zurückgesetzter Epochenzähler ist mehrdeutig und gibt keinen automatischen Reload frei. Vor jedem Request wird nochmals geprüft. Alle Auslöser und Entscheidungen werden als `T3-AUTO-REJOIN trigger=... decision=...` protokolliert; bestätigte andere Playthroughs sowie die Wiederherstellung eines abgebrochenen ersten Transfers bleiben eigene begründete Wege.

## Messung und Prüfung

Alle zehn P3-Auftragszähler erscheinen mit Nullwerten oder den gemessenen Zahlen im 60-Sekunden-`MP-T3-STATS` von Agent und DLL-Mirror. Lua importiert ausschließlich vollständige numerische Loot-/Input-Gruppen; fremde native Zähler kann ein Lua-Report nicht überschreiben. `auto_rejoins_in_world` zählt zugelassene automatische Request-Versuche nach bestätigtem Host-Weltwechsel.

Lokale gezielte Client-Regressionen: 112/112, einschließlich echter Receive-/HTTP-/Pipe- und EOF-Fälle sowie Rejoin-/Input-Lebenszeiten. Alle 84 Lua-Suiten bestanden mit 5412 Checks; nach den letzten kleinen Hold-/Legacy-Chunk-Korrekturen bestanden die vier betroffenen Suiten nochmals mit 363/363 Checks. Der komplette Clientlauf auf macOS hatte 2329 bestandene und 14 plattform-/lokalisierungsabhängige Fehler (unter anderem Windows-Registry/iphlpapi, Pfadsyntax, Culture und Kompressionsfixture); maßgeblich bleibt die vollständige Windows-CI ohne solche Ausnahmen. Windows-CI 44223046972 (Head bfd9500b) ist vollständig grün: Client 2355, Relay 236, Setup 110, LogPack 105, native 11025, alle Lua-Suiten und Netzwerksimulator. Artifact 14392880463 enthält den geprüften Drop-in; die CI öffnet das Zip und bestätigt install-verify PASS. PR #21 bleibt offen und ungemergt. Reale Zweirechner-Spieltests bleiben offen: Aufheben während Hold, `gone`/`unknown`, Leichen-Take ohne Open, ESC/Quicksave und Wiederverbindung in derselben Welt. Synthetische Tests ersetzen diese Spielprüfung nicht.
