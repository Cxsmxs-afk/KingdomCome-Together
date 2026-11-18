# Lane 2 · Nahkampf am Partner

## N1 · Schwung, Block, Parade und Trefferreaktion

`codex/lane2-nahkampf-swings` setzt auf `5fc9133e` auf. Die Presentation-Lane prüft Aktion, Katalogtabelle und die Kennzeichen für Combo und perfekten Block, bevor sie eine Zeile an die DLL gibt. Angriff (`1`), Block (`6`), perfekter Block (`0x83`), Trefferreaktion (`0x84`) und Rückstoß (`0x85`) gehen als getrennte Typen an den vorhandenen nativen Zeilen-Owner. Eine unpassende Zeile wird vor dem Routing verbraucht und als `kind-table-mismatch` protokolliert.

Der aktive Pfad gibt die Zeile einmal an den nativen Zeitplan. Fehlt der Zeitstempel oder meldet ein typisierter Provider vorübergehend Nichtverfügbarkeit, wird die Zeile in diesem Pfad beendet. Die Kompatibilitätsroute greift bei ausgeschaltetem `rp_combat` oder wenn die DLL die typisierte Anfrage ausdrücklich als unbekannt zurückweist. Der native Provider schützt die visuelle Aktion mit dem vorhandenen Damage-Guard. Friendly Fire bleibt beim Test eingeschaltet.

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
| `dmg_double` | T3-Zähler beider Seiten, muss null bleiben. Grün braucht eine belegte Blockierung im aktuellen Messfenster und null ungeklärte Schadensfälle. |

Das Rig verbindet gesendete Aktionen mit Agent-Quittung und der nativen `installed`-Meldung für dieselbe Figur und denselben Lauf. Ein Queue-Eintrag reicht dafür nicht aus. Die Bildprüfung bleibt grau, bis ein Beobachter auf der Empfängerseite die Pose sieht. Für die Schadensabnahme im Spiel zusätzlich die tatsächliche Lebens- und Ausdaueränderung pro Treffer prüfen. Der untere native Trefferpfad liefert derzeit keine Bestätigung pro angewendetem Treffer, daher reicht `dmg_double=0` allein dafür nicht aus. Reale Messwerte für diese Lane und der Spielnachweis stehen aus. [Nativer Damage-Guard](../native/KCDMP/hits.cpp)

## N2 · Griffe und Konter

**Grau · Engine-Einstieg für Spielerpaare unbestätigt.** `codex/lane2-nahkampf-grabs` baut auf N1 auf und dokumentiert den Befund. WO-197 fand `StartSyncGuard`, die Engine wies den Aufruf auf zwei Puppets jedoch in allen Versuchen ab. Die dort beobachteten einzelnen Reaktionszeilen betrafen NPC-/Figurenpaare. Der Einstieg aus WO-209 war für die Kombination aus NPC-Kopie und lokalem Spieler gebaut. Auf dem Prüfstand trat für diese Kombination kein Clinch auf. WO-210 dokumentiert, dass die Abnahme einer Paaraktion auf beiden Bildschirmen offen blieb. [WO-197](WO-197-findings.md) · [WO-209](WO-209-findings.md) · [WO-210](WO-210-findings.md)

`melee_grab_shown` ist ungemessen. Der Abnahmebeleg wäre ein echter Griff mit beobachtetem Einstieg, Bewegung und sauberem Ende für beide Spieler, in beiden Rollenrichtungen. Der dokumentierte Hinweis zu Griffen mit den Leuten des Hosts gilt weiter. [KNOWN-ISSUES](KNOWN-ISSUES.md#fights)
