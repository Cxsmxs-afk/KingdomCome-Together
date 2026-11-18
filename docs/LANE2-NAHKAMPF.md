# Lane 2 · Nahkampf am Partner

## N1 · Schwung, Block, Parade und Trefferreaktion

`codex/lane2-nahkampf-swings` setzt auf `5fc9133e` auf. Die Korrektur auf `codex/lane2-nahkampf-swings-fix` baut auf `2727cfb0` auf. `Attack` aus der Tabelle `combat_action_attack` bleibt auch mit `FlagCombo` ein Schwung (`1`) auf der Angreiferfigur. Block (`6`), perfekter Block (`0x83`), Trefferreaktion (`0x84`) und Rückstoß (`0x85`) gehen als getrennte Typen an den nativen Zeilen-Owner. Eine unpassende Kombination von Aktion und Tabelle wird vor dem Routing als `kind-table-mismatch` abgewiesen.

Der aktive Pfad gibt die Zeile einmal an den nativen Zeitplan. Ein fehlender Zeitstempel beendet die Anfrage. Die Kompatibilitätsroute greift bei ausgeschaltetem `rp_combat` oder wenn die DLL die typisierte Anfrage ausdrücklich als unbekannt zurückweist. Friendly Fire bleibt beim Test eingeschaltet.

Die Edge-Result-Nutzlast `[0,0,1]` setzt sich aus `ok=0`, `seq=0` und `Receipt::Unavailable=1` zusammen. Welche Bedingung zutrifft, hängt vom Request ab. Der ältere `TimedAction`-Pfad kann `Unavailable` etwa bei deaktivierter Timeline, fehlender Bindung, fehlendem Body oder voller Queue melden. Auch ein typisierter KO-Recovery-Request kann die Antwort erhalten, wenn sein Provider noch nicht bereit ist. Bei den typisierten Nahkampfzeilen liefern die Registrierung und die Einreihung `Unavailable`, wenn Timeline oder nativer Nahkampf-Owner aus sind. Bei einer bereits bekannten Aktions-ID gibt die Einreihung zuerst die gespeicherte Quittung zurück. [Result-Format](../native/KCDMP/pipe_server.cpp#L1177-L1181) · [Receipt-Werte](../native/KCDMP/rp_timeline_action_rules.h#L17) · [Agent-Validierung](../dotnet/KcdMp.Client/CombatPipe.Edge.cs#L301-L312) · [Native Request-Pfade](../native/KCDMP/rp_timeline_actions.cpp#L274-L371)

Scheitert bei einer typisierten Zeile die Prüfung `hits::visual_damage_ready`, ist das Ergebnis `Blocked` (`3`). Bei der Wiedergabe über `queue_ghost_row` prüft die DLL den Schutz erneut. Das ältere `ghost_swing` hat an dieser Stelle keine entsprechende Pflichtprüfung. Deshalb bleibt eine aktuelle Nahkampfzeile mit `[0,0,1]` unterdrückt. Diese Anfrage löst weder typisiert noch über Legacy einen Schwung aus. [Registrierungsprüfung](../native/KCDMP/rp_timeline_actions.cpp#L301-L309) · [Prüfung vor Einreihung](../native/KCDMP/rp_timeline_actions.cpp#L188-L191) · [Zulassungsentscheidung](../native/KCDMP/rp_timeline_actions.cpp#L359-L371) · [Wiedergabeprüfung](../native/KCDMP/combat_construct.cpp#L1055-L1062) · [Agent-Zulassung](../dotnet/KcdMp.Client/GameBridge.PresentationCombat.cs#L96-L111)

Der Agent meldet `result=unavailable reason=typed-provider-unavailable` und zählt jede solche Antwort aus der aktuellen Nahkampf-Registrierung oder -Einreihung einmal in `melee_presentation_unavailable`. Der Zähler erscheint ab der ersten typisierten Zeile mit gültigem Nahkampf-Scope in `MP-T3-STATS`. Ziel bei aktivem `rp_combat` ist ein Anstieg von null im Messfenster. Beim Rig bleiben fehlende Messwerte grau. Ohne echten Kampflauf ist die Häufigkeit unbekannt. [Einreihung und Zähler](../dotnet/KcdMp.Client/GameBridge.PresentationCombat.cs#L76-L111) · [Initialisierung und Registrierung](../dotnet/KcdMp.Client/GameBridge.PresentationCombat.cs#L160-L185) · [T3-Ausgabe](../dotnet/KcdMp.Client/GameBridge.T3.cs#L260-L280) · [Richtungsmessung](../tools/rig/scen_lane2_melee.py#L114-L136)

**Grau · Paaraktionen und Meisterschläge.** Die alte Zuordnung behandelte `combat_action_sync_attack` als Schlag. Ein Meisterschlag der Typen `55` und `62` konnte als perfekter Block durchgehen, da er dieselbe Tabelle nutzt. Beide Fälle enden jetzt mit einem eigenen grauen Status. WO-197 belegt separat installierte Reaktionszeilen. Für die Partnerfigur fehlt der Nachweis, dass der Engine-Einstieg die beiden Körper als Paar abspielt. WO-209 und WO-210 beschreiben die Grenzen des Einstiegs. [WO-197](WO-197-findings.md) · [WO-209](WO-209-findings.md) · [WO-210](WO-210-findings.md)

### Abnahme im Spiel

Zwei verbundene Windows-Instanzen der KCD II Modding Tools mit demselben Build verwenden. Im Wegwerfstand Friendly Fire am Host einschalten. `$Bandit` bezeichnet den tatsächlichen Entitätsnamen eines geladenen, lebenden Banditen. Der Aufruf führt durch die Partner- und Banditenstation in beiden Richtungen.

```powershell
$env:KCDMP_TEST_RP_COMBAT = '1'
python tools/rig/scen_lane2_melee.py --test-rp-combat --netz normal --bandit $Bandit --sender both
```

Für den zweiten Lauf ein frisches Relay mit dem Profil `schlecht` starten und `--netz schlecht` verwenden. Das Szenario liest Friendly Fire auf beiden Seiten zurück und legt die Richtungsberichte `lane2-melee-host.json` und `lane2-melee-joiner.json` mit den Log-Bundles ab.

| Feld im Lane-Bericht | Messgrundlage |
| --- | --- |
| `melee_swing_shown` | Gesendete Schwünge mit bestätigter Installation auf der adressierten Partnerfigur |
| `melee_block_shown` | Bestätigte Blocks und perfekte Blocks, im Bericht je Typ getrennt |
| `melee_hitreact_shown` | Bestätigte Trefferreaktionen, Rückstöße separat unter `recoil` |
| `melee_presentation_unavailable` | T3-Zähler für `[0,0,1]` bei typisierten Nahkampfzeilen. Vorher-/Nachher-Messung auf der Empfängerseite. |
| `dmg_double` | T3-Zähler beider Seiten, muss null bleiben. Grün braucht eine belegte Blockierung im aktuellen Messfenster und null ungeklärte Schadensfälle. |

Das Rig verbindet gesendete Aktionen mit Agent-Quittung und der nativen `installed`-Meldung für dieselbe Figur und denselben Lauf. Ein Queue-Eintrag reicht dafür nicht aus. Die Bildprüfung bleibt grau, bis ein Beobachter auf der Empfängerseite die Pose sieht. Für die Schadensabnahme im Spiel zusätzlich die tatsächliche Lebens- und Ausdaueränderung pro Treffer prüfen. Der untere native Trefferpfad liefert derzeit keine Bestätigung pro angewendetem Treffer, daher reicht `dmg_double=0` allein dafür nicht aus. Reale Messwerte für diese Lane und der Spielnachweis stehen aus. [Nativer Damage-Guard](../native/KCDMP/hits.cpp)

## N2 · Griffe und Konter

**Grau · Engine-Einstieg für Spielerpaare unbestätigt.** `codex/lane2-nahkampf-grabs` baut auf N1 auf und dokumentiert den Befund. WO-197 fand `StartSyncGuard`, die Engine wies den Aufruf auf zwei Puppets jedoch in allen Versuchen ab. Die dort beobachteten einzelnen Reaktionszeilen betrafen NPC-/Figurenpaare. Der Einstieg aus WO-209 war für die Kombination aus NPC-Kopie und lokalem Spieler gebaut. Auf dem Prüfstand trat für diese Kombination kein Clinch auf. WO-210 dokumentiert, dass die Abnahme einer Paaraktion auf beiden Bildschirmen offen blieb. [WO-197](WO-197-findings.md) · [WO-209](WO-209-findings.md) · [WO-210](WO-210-findings.md)

`melee_grab_shown` ist ungemessen. Der Abnahmebeleg wäre ein echter Griff mit beobachtetem Einstieg, Bewegung und sauberem Ende für beide Spieler, in beiden Rollenrichtungen. Der dokumentierte Hinweis zu Griffen mit den Leuten des Hosts gilt weiter. [KNOWN-ISSUES](KNOWN-ISSUES.md#fights)

## Build und Download

Der Nachzug `codex/lane2-nahkampf-swings-fix2` baut auf `b9fcee3c` aus Draft-PR #42 auf. Er liegt als Draft-PR #47 gegen `codex/s3-1-load-sequence` vor. [Basis-PR #42](https://github.com/Cxsmxs-afk/KingdomCome-Together/pull/42) · [Draft-PR #47](https://github.com/Cxsmxs-afk/KingdomCome-Together/pull/47) · [CI-Prüfungen für Fix2](https://github.com/Cxsmxs-afk/KingdomCome-Together/pull/47/checks)

Der Download gehört zum grünen Windows-Lauf für `b9fcee3c`. Dort bestanden 2.663 Client-Tests und die Netzwerksimulation. Die CI hat im fertigen Drop-in die erste Zeile von `install-verify.txt` als `PASS` bestätigt. [Windows-CI für `b9fcee3c`](https://github.com/Cxsmxs-afk/KingdomCome-Together/actions/runs/44378296256)

[Geprüftes Drop-in-ZIP `kingdom-come-together`](https://github.com/Cxsmxs-afk/KingdomCome-Together/actions/runs/44378296256/artifacts/14462876809)
