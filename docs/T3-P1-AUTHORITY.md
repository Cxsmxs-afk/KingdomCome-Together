# P1: Host-Autorität nach dem Load, sichere NPC-Sichtbarkeit

Gestapelt auf P0/PR #18 (`780075b`), Branch `codex/t3-p1-intellect-gate`.

## Befund

Die sechs Test-3-Bundles belegen vorzeitige Aktivierung. S1 `kcdmp-native.mirror.log:220`: Rolle 0→2 um 23:28:30.109, aber `agent.log:2664` echtes Gameplay erst 23:32:50.752. Schon um 23:32:07 meldet der Joiner `hidden=506 bound=0` (`agent.log:1547`), später `hidden=446 bound=60` (`:2718`). S2: Rolle 2 um 23:52:19.793, Gameplay 23:56:35.703. S3: Rolle 2 um 00:00:07.073, Gameplay 00:04:19.695. Die doppelten Snapshotpaare werden nicht als zusätzliche Sitzungen gezählt.

In der Basis sperrt `decision_gate` den vollständigen `C_NPCManager::UpdateIntellects`-Einstieg allein anhand Rolle 2 und installiertem Hook. Der Alive-/Brain-Check des Census sitzt dagegen nur im Breach-Zweig, der bei funktionierendem Gate entfällt. Der Spawn-Pfad kann ganz ohne diesen Check verstecken. Das ist kein Nachweis, dass jeder später sichtbare Fehler nur diese eine Ursache hat; P0 behebt zusätzlich den nachfolgenden Prozessor-Abbruch.

## Verhalten und Vertrag

Rolle, Hook-Bereitschaft und aktive Sperre werden getrennt. Rolle 2 vor dem Laden bedeutet zunächst **pending**. Jeder Load widerruft eine frühere Freigabe; erst ein passendes Gameplay-Signal derselben Generation und mindestens 5000 ms Settle erlauben die Sperre. Wiederholte Heartbeats starten diese Frist nicht neu. Eine alte verzögerte Gameplay-Nachricht kann einen neuen Load nicht freigeben.

Der Kontrollpfad arbeitet ohne Engine-Aufrufe auf dem Pipe-Reader, außerhalb von Main-Thread-Queue und Batchpump. Eine bestätigte Pending-Nachricht muss vor dem vom Mod ausgelösten Load eintreffen. Weitere vom Spiel gemeldete Loads setzen den Zustand ebenfalls zurück. Anfragen und Antworten tragen Request-IDs, damit eine späte Antwort keinen späteren Kontrollaufruf bestätigen kann.

Der Reader verarbeitet diese Kontrollnachrichten auch während des bisherigen bis zu 3500 ms langen Wartens auf einen normalen Main-Thread-Aufruf. Er prüft den Eingang in höchstens 5-ms-Wartescheiben; dabei bleibt der Ergebnis-Mutex frei. Ein gemeinsamer inkrementeller Decoder erhält auch teilweise eingetroffene Header/Payloads. Ausschließlich Standalone-op168 darf vorziehen; normale Frames und Batches bleiben in ihrer Reihenfolge. Die Sequenznummer wird beim Lesen genau einmal vergeben. Puffer und Warteschlange sind begrenzt. Das ist kein zusätzlicher Thread pro Anfrage.

Auch normale C#-Singles/Pings bleiben an Stream und Replykanal ihrer ursprünglichen Verbindung gebunden. Ein alter Gate-/Writer-Waiter kann beim Reconnect nicht in eine neue Verbindung schreiben. Fehlende Kontrollantworten werden derselben Ladegeneration zugeordnet erneut versucht; ein Retry erfindet kein Gameplay-Signal. Lua-Modusaufrufe tragen Generation und eine monotone Phasenfolge gegen verspätete HTTP-Aufrufe. Ein eigenes Save beim Leave bekommt keine Joiner-Autorität.

Eine Aufteilung von `UpdateIntellects` in Rekonstruktion, Smart-Entity-Initialisierung und eigentliche Entscheidung ist mit den vorhandenen, geprüften Anchors **nicht belegt**. Es wird kein innerer Offset erfunden. Der bestehende eindeutig verifizierte Funktionseinstieg bleibt der Hook. Für fehlende eindeutige Rekonstruktionssignale gilt die Gameplay-plus-5-s-Regel; das tatsächliche Ende der Rekonstruktion und die Ladebildschirmmeldung bleiben im Spiel zu prüfen.

Die Lua-Grenze folgt demselben Prinzip: Mod-eigene Pause-/Resume-Befehle laufen über `KCD2MP_ModBrainCommand` und werden für strikte Joiner bereits während Pending unterdrückt. Der globale Wrapper lässt dagegen fremde Spielskript-Aufrufe während Pending durch und sperrt sie erst bei Active/armed. Dazu wird kein ungesicherter Caller-/Stack-Getter verwendet. Auch die ältere Release-Policy darf den Pending-Durchlass nicht erneut sperren.

Konsole: `mp_host_authority on|off`, Standard **on**. Bei off wird fehlende Hook-Bereitschaft nicht zum Join-Abbruch. Die globale Sperre und der Paket-C-Lua-Pauseblock werden gelöst; der frühere eigene Pause-/Resume-Pfad für NPC-Kopien wird wieder verwendet. Das ist ein Diagnose-Fallback mit dessen früheren Einschränkungen. Auch dieser Pfad startet nicht vor dem Laden. Host-Uhrschutz bleibt vom Schalter unabhängig.

Eigene native Legacy-Pausen werden nach Identität, WUID, Generation und ursprünglich laufendem Brain erfasst. Cleanup darf nur den exakt eigenen Kontext `0x08` lösen. Andere Pausebits werden nicht durch ein pauschales Resume geöffnet; die eigene Cleanup-Lease wartet beziehungsweise verfällt bei einer anderen Körperidentität. Die Bereinigung läuft mit begrenzter Arbeit pro Frame.

Sichtbarkeit wird aus einem vollständigen Census entschieden: nur bekannte lebende, rekonstruierte Körper dürfen versteckt werden. Bei `bound==0` oder weniger als 30 % gebundenen lebenden NPCs im Umkreis von 150 m bleibt der Failsafe an und zeigt die Kopien. Ein versteckter Körper wird spätestens nach 10 s ohne Bindung wieder gezeigt und erst nach einer neuen Bindung wieder für Verstecken zugelassen. Spawn und nachgelagerte Hide-Aktionen müssen dieselben Regeln prüfen; Leichen und unklare Initialisierung sind davon ausgenommen.

## Zähler und Prüfung

`MP-T3-STATS` alle 60 s, Agent und gekennzeichneter DLL-Spiegel:

- Native Momentaufnahme: `gate_armed_at_s`, `decision_blocked`, `decision_blocked_during_load`, `npc_near`, `npc_bound`, `npc_hidden`, `hide_failsafe_on`, `unhidden_unbound_10s`.
- Agent aus der tatsächlichen Engine-Timeout-Zeile: `postload_timeouts`.
- Diagnose eines unlesbaren/unvollständigen nativen Snapshots: `authority_stats_unreadable`. Ein solcher Snapshot überschreibt keine einzelnen Werte. Alle acht nativen Felder müssen vollständig vorliegen.

Lokale Integration: 204/204 Client-Regressionen. Native Regeln/Decoder/Wait-Pump: 631/631 mit strikten Warnungen; ASan/UBSan ohne Befund. Der gemeinsame Wait-Pump-Test beantwortete ein auf Header/Payload verteiltes Load in 31 ms, während Main weiter unfertig blieb. Lua-Regressionen WO134 114, WO200 81, WO217 55, WO86/P1 89 und P0-Konsole PASS. Nach einem CI-Befund zur optionalen Soul-GetId-Fähigkeit wurden Capture/Cleanup abgesichert (ohne Getter keine Lease) und Regressionen ergänzt. Tatsächlicher MoonSharp-2.0.0-Lauf: alle 83 Suiten, 5294/5294 Checks bestanden; WO131 101/101. CI und Drop-in-Nachweis werden im PR nach Abschluss ergänzt. Im Spiel offen: zwei PCs, Join sowie jeder folgende Quicksave-/Menü-Load, NPC-Sichtbarkeit und Bindungsquote, keine Post-Load-Timeout-Meldung. Ein reiner Regeltest ersetzt diese Prüfung nicht.
