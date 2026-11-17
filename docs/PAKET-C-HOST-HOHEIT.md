# Paket C plus F2/F3: Story und KI gehören dem Host

Basis: Paket B, PR #14, `73e6715`. Gestapelte Branch `codex/host-story-authority`, Ziel `codex/host-clock-bootstrap`.

Der Joiner sperrt die NPC-Entscheidungsrunde am Einstieg von `C_NPCManager::UpdateIntellects`. Der Einstieg wird über seinen eindeutigen Profiler-Namen und den PE-Funktionsanfang gefunden. Die tatsächlich geladenen ersten Instruktionen müssen einen relocationsfreien Hook erlauben; andernfalls wird der Join verweigert. Bewegung, Navigation und die vom Host angeforderten Körperaktionen laufen weiterhin über die vorhandenen nativen Wege. Die periodischen `wh_ai_PauseNPC`-Befehle und lokalen Brain-Ausnahmen entfallen. Damit entstehen durch den Mod keine eigenen Suspend/Resume-Wechsel mehr in den kopierten Bäumen. Lua-Detach/ResetElement und dessen Retries entfallen während dieser Host-Hoheit ebenfalls.

Die nativen Klassen `C_AreaTrigger`, `C_AreaTriggerAnonymous` und `C_AreaLabelTrigger` werden direkt an Execute und ihren Ausgangspulsen gesperrt. Das gilt auch für bereits aktive Trigger und später geladene Module. Die wiederkehrende Pfadregistrierung aus Scripts.pak entfällt; ungeladene Pfade werden nicht mehr als `npc_trigger_missing` gezählt. `npc_trigger_classes=3/3` zeigt die tatsächliche Klassenabdeckung. SchedulerLink, Behaviour, EntityContext, Interrupt und Lebensänderungen an Kopien bleiben auch bei ausgeschalteten alten Feature-Schaltern gesperrt. Henrys eigene Aktionen bleiben zielbezogen ausgenommen.

Vor dem Ladebefehl und erneut vor Ready verlangt der Agent `decision_gate=armed story_gate=armed`. Die Rolle bleibt über den Load und Holds erhalten. Abbruch, eigenes Save, Verlassen und Rollen-/Weltwechsel setzen sie zurück. Konfiguration und Reset sind serialisiert und gegen verspätete Aufträge geschützt. Bei einer reinen Pipe-Unterbrechung behält die DLL die Sperre; eine Wiederaktivierung verlangt die bestätigte Host-Welt.

F2: Joiner-Kopien melden keinen Tod als FATAL. Die letzte Schreibstelle im Agenten prüft die Host-Rolle; der Relay entfernt fremde FATAL-Bits und verweigert fremde GUID-Todesmeldungen. Tatsächliche Trefferanfragen bleiben erhalten. DLL- und Lua-Todesbeobachter teilen eine atomare Sperre pro Körperleben, inklusive GUID-/Namensalias und Wiederholung nach einem fehlgeschlagenen Versand. Ein FATAL erzeugt beim Empfänger einen Todespfad, keinen vorgeschalteten numerischen tödlichen Treffer. Die konkurrierenden Empfangswege sind serialisiert; erst ein erfolgreich übernommener Tod wird gesperrt. Save-/Verbindungswechsel und eine beobachtete Wiederbelebung eröffnen ein neues Leben.

F3: `SetIsNight` und `WhatTimeIsIt` werden sowohl als exakte Methoden als auch als eigene Pfadkomponenten von Quest-Bibliotheken erkannt. Auf dem Joiner laufen weder Execute, Ausgangspulse noch Function-Invoke über Datenports. Direkt vom Host gespiegelte State-Werte werden weiterhin übernommen.

## Prüfen mit zwei Windows-PCs

1. Auf beiden Rechnern dasselbe Drop-in-Zip installieren. Host lädt seinen Save, Joiner tritt bei.
2. Vor Load und Ready muss `MP-AUTHORITY ... decision_gate=armed story_gate=armed` erscheinen. Fehlt ein Hook, muss der Join vor dem Laden abbrechen.
3. `mp_w216_status` und native Statusausgabe: Rolle 2, `decision_blocked` wächst, Triggerklassen 3/3. Keine neuen Pause/Resume-Befehle auf dem Joiner. `npc_trigger_missing` bleibt ohne die alte Pfadregistrierung 0; das allein ersetzt keinen Verhaltensnachweis.
4. NPCs am Schmied, an der Maistange, beim Würfeln und an Leitern beobachten; Questschritte und NPC-Zeitentscheidungen nur vom Host. Über Tag/Nacht, Pause, Hold und Schlaf/Wait testen. Ziele: `Node status inconsistency`, DropFromLadder und Triggerfehler nahe 0.
5. Je ein Host- und Joiner-Treffer töten einen NPC; beide sehen einen lootbaren Körper. Auf dem Joiner kein FATAL-Ausgang, auf dem Host eine Meldung pro Leben. Doppeltes 0x31/0x27/owner-death führt zu einem ApplyDeath. Save erneut laden und denselben NPC erneut töten.
6. Join abbrechen, eigenen Save laden, Rolle wechseln, Relay und Pipe getrennt unterbrechen. Eigener Save muss seine KI wieder ausführen; im bestätigten Host-Save darf eine Pipe-Unterbrechung keine lokalen NPC-Entscheidungen freigeben.

## Grenzen der Prüfung

Lua-Szenarien, native Regeln, Client-/Relay-Tests und Windows-Artefaktprüfung sind offline prüfbar. Die neuen Hooks können hier ohne Modding-Tools-Spiel nicht tatsächlich installiert werden. Der erste interne Scheduler beim Load, die genaue Trennung der NPC- und Spieler-Scheduler, echte Quest-/Leiterabläufe und die genannten Fehlerraten bleiben bis zum Zweirechnerlauf unbestätigt. Es gibt hier keine behauptete Live-Messung nahe 0 und keinen Retail-Nachweis.
