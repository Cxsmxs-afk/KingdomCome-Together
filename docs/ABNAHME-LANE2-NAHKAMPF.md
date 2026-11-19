# Zwei-PC-Spielabnahme für den gemeinsamen Nahkampf-Build

**Status.** Der Spielnachweis steht für alle Stationen aus. Der H6-Rig wertet vorhandene Logs aus. Die Tests für verspätetes `Finished`, fehlende Zieluhr und zurückgehaltene Friendly-Fire-Kontakte nutzen kontrollierte Test-Fixtures. [H6-Rig](../tools/rig/scen_lane2_h6.py) · [Integrationsstand](LANE2-H6-INTEGRATION.md)

## Vorbereitung

1. Auf PC A und PC B dasselbe geprüfte Drop-in des gemeinsamen Stacks in den KCD II Modding Tools installieren. Exakte Build-ID aus `MP-BUILD` im Agent-Log und `KCDMP.dll attached … build=…` im nativen Log abgleichen. Die Rolle gilt für die jeweilige Sitzung. Erst A hosten und B beitreten lassen, dann mit einem frischen Start B hosten und A beitreten lassen. Der Join ist belegt, wenn die neueste `ready sent for join`-Meldung am Joiner und `MP-JOIN resume … reason=ready` am Host dieselbe Join-ID nennen. Die integrierten Join-/Save-, H4/H17- und H14-Prüfungen vor der erweiterten Kampfserie abschließen. [H22-Rollentausch](ABNAHME-H22.md) · [Join-Zuordnung](../tools/rig/scen_s3_load.py#L105)
2. Einen geeigneten Wegwerfstand verwenden. Friendly Fire bleibt an und wird in beiden Rollen durch `MP-FF … friendly_fire=on` oder `MP-WO121 cfg … friendly_fire=on` belegt. `mp_combat_action_end` bleibt an. Auf beiden PCs die Logs für genau einen Spielstart bereithalten und die Uhrzeit in Europe/Berlin zusammen mit der Station notieren. Mit `mark <Notiz>` eine Unregelmäßigkeit in beiden Logs auffindbar machen. Sichtbelege einzeln und bewusst aufnehmen. [Friendly-Fire-Konfiguration](../dotnet/KcdMp.Client/GameBridge.Wo121.cs) · [Markierung](../kdcmp/Data/Scripts/Startup/kdcmp.lua#L20489)

## H6 · Aktionsfreigabe vor dem NPC-Tod

In jeder Rollenverteilung 30 verschiedene nahe NPCs im echten Spiel durch den normalen Kampf zu Tode bringen. Der Joiner beobachtet die lokale NPC-Kopie aus der Nähe. Pro Versuch müssen der Native-Tracker eine wartende und eine aktive Aktion lesen und die Logs dieselbe EID, Versuch-ID, Lebensgeneration und Pipe-Verbindung belegen. Sobald der letzte Tod bestätigt ist, auf beiden PCs mindestens 60 Sekunden in der verbundenen Szene bleiben. Nachlauf, unerwartete Rückkehr ins Menü, Verbindungsabbrüche und Spielabstürze in beiden Sichtprotokollen festhalten. Ziel sind 30/30 je Rollenverteilung und null Crashs oder Abbrüche. [Tracker-Quittung](../native/KCDMP/combat_actions.cpp) · [Native Todesphasen](../native/KCDMP/pipe_server.cpp)

| Prüfung | Soll im Lauf |
| --- | --- |
| Anfangslage | `WO221-ACTEND ... attempt=... waiting>0 active>0` für den betrachteten NPC. Auch die Nähe und die laufende Szene im Bild festhalten. |
| Freigabe | Während `receipt=2` beziehungsweise `stage=release-pending` läuft dieselbe Versuch-ID weiter. Ein `WO221-ACTEND receipt=1` muss vor `MP-LANE2-H6 stage=ready` stehen. |
| Tod | Danach einmal `stage=death-dispatched` und einmal `stage=applied` mit derselben Versuch-ID. `applied` enthält den `IsDead`-Readback. `WO195-FALL` und der sichtbare Fall ergänzen die Beobachtung. Die ältere Fallzeile nennt den NPC-Namen. |
| Nachlauf | Der H6-Rig findet für den Joiner mindestens 60 Sekunden `FRAME`-Nachlauf im selben nativen Start. Beide PCs bleiben verbunden. `rp_owner_n>0`, `owner_violations=0`, `FRAME ours_us_p95<2000`. |

Die kontrollierten Gegenproben haben eigene Messfenster. Für über eine Sekunde `Pending` und erst nach 1,2 Sekunden `Finished` zählt eine einzige letale Engine-Anforderung auf demselben Körper. Der belegte Abbruch vor der Mutation liefert `stage=aborted-before-apply`, null `death-dispatched` und danach eine bestätigte gültige Aktion auf dem weiterhin lebenden Ziel. Bei `stage=apply-uncertain` denselben Versuch bis zum Readback verfolgen. Die Zahl der letalen Aufrufe bleibt eins. Ohne eindeutigen Readback bleibt der Ausgang grau. Name, GUID und fatale Meldung über ihren jeweiligen Einstieg im Produktionspfad testen. [Managed-Todespfad](../dotnet/KcdMp.Client/GameBridge.NpcDeathDispatch.cs) · [Native Versuchssteuerung](../native/KCDMP/npc_death_release.h) · [Ingress-Regression](../dotnet/KcdMp.Client.Tests/Lane2NpcDeathIngressTests.cs)

```powershell
python tools/rig/scen_lane2_h6.py --build $BuildId `
  --a-host $AHostAgent $AHostNative $AHostKcd `
  --b-joiner $BJoinAgent $BJoinNative $BJoinKcd `
  --b-host $BHostAgent $BHostNative $BHostKcd `
  --a-joiner $AJoinAgent $AJoinNative $AJoinKcd
```

## H15 und H16 · Eine sichtbare Aktion pro gültiger Zeile

In beiden Senderrollen einzeln Schwung, normalen Block, perfekte Parade, Riposte, Combo, Trefferreaktion, KO, Aufstehen und Tod vorführen. `MP-RP-TIMELINE-IN` und `MP-RP-COMBAT-ACTION` über `peer`, `seq`, `kind` und `event_host_ms` zuordnen. EID und Scope in der nativen Folge abgleichen. Bei einer vorhandenen `MP-RP-MELEE-ROW`-Diagnose deren `peer`, `seq` und `source_ms` zuordnen. Für KO und Aufstehen den `MP-RP-KO`-Beleg der Figur ergänzen. Zuerst genau einen sichtbaren Schwung beobachten. Den zugehörigen nativen installierten Ausgang separat prüfen. Für einen gewöhnlichen Block ohne Perfect-Flag muss der eindeutige gewöhnliche Tabellenkontext `kind=6` ergeben. Eine fehlende oder mehrdeutige Zuordnung sowie Meisterschlag und Paaraktion bleiben ohne passenden Engine-Nachweis grau. Für den Doppelkontakt-Test vor und nach der Station auf beiden PCs die T3-Zähler erfassen. Soll ist `dmg_double=0`, ein im Messfenster gestiegener `dmg_double_blocked` und `dmg_double_unconfirmed=0`. [Nahkampf-Rig](../tools/rig/scen_lane2_melee.py) · [Native Aktionsdiagnose](../native/KCDMP/rp_timeline_actions.cpp#L114) · [Sicht- und Schadensmessung](LANE2-NAHKAMPF.md#abnahme-im-spiel)

Die kontrollierte H15-Gegenprobe lässt den Pose-Strom frisch und entfernt gezielt die native Zieluhr. Erwartet wird für denselben gültigen Scope ein einziger Provider-Aufruf, `reason=guarded-live-pose` beim Einreihen durch den Provider und genau ein terminales `result=installed`. Bei Scope-Wechsel vor Dispatch lautet der terminale Ausgang `result=expired dispatch=not-started`. Bei mehrdeutigem Engine-Ausgang höchstens ein Provider-Aufruf und ein terminales `result=unconfirmed dispatch=uncertain`. Die Beobachtung des normalen Spiels und die kontrollierte Zieluhr-Gegenprobe getrennt ausweisen. Für gezielten Zieluhrverlust ist hier ein Einstieg im Engine-Double belegt. [Scheduler-Fixture](../native/tests/rp_timeline_outcome_integration.cpp)

## Friendly Fire · Altes Leben, neues Leben, Schutzfenster

Auf beiden Seiten die HP-/Ausdauerwerte des tatsächlichen Empfängers direkt vor und nach dem isolierten Ereignis erfassen. Bei vorhandener nativer Messung `MP-LANE2-COMBAT event=contact source=pvp` anhand von `contact`, Ziel-EID und Lebens-/Weltscope mit `MP-FF dir=in` verbinden. `acknowledged` zählt als Quittung. Ein gesundes oder erschöpftes Ziel, eine tatsächliche Änderung und die Zahl der nativen Aufrufe werden getrennt notiert. [N3-Rig](../tools/rig/scen_lane2_melee.py) · [Native HP-/Ausdauerwerte](../native/KCDMP/hits.cpp)

| Kontrollierte Station je Rollenrichtung | Soll |
| --- | --- |
| Alter Kontakt | Ein eindeutiger Kontakt wird vor dem Reader zurückgehalten. Das Ziel erfährt einen echten Respawn auf derselben Relay-Verbindung und zeigt einen neuen Lebens-Scope in `T3-LIFE-EDGE`. Erst danach wird der alte Kontakt erstmalig zugestellt. `MP-FF dir=in … result=recipient_retired reason=recipient-life` nennt die Kontakt-ID. Null native Schadensaufrufe, null HP-/Ausdauerverlust im isolierten Fenster. |
| Gültiger Kontakt und Duplikat | Ein isolierter gültiger Kontakt für das neue Leben ergibt genau einen nativen Schadensaufruf und den dazu gehörenden HP-/Ausdauer-Readback. Dasselbe Kontaktpaket ein zweites Mal zustellen. Die Duplikatmeldung nennt dieselbe Kontakt-ID. Null weitere Aufrufe und null zusätzlicher HP-/Ausdauerverlust. |
| Schutz | Ein neuer eindeutiger Kontakt während des belegten Schutzfensters liefert `protected_consumed` und einen getrennten nativen Beleg mit unveränderten HP-/Ausdauerwerten. Der Empfang allein belegt die Werte nicht. |

Für die Vor-Reader-Zustellung ist aktuell eine kontrollierte Reader-Fixture vorgesehen. Im Spiel zusätzlich realen KO, Aufstehen, Respawn und einen gewöhnlichen Friendly-Fire-Kontakt in beiden Rollen beobachten. Für den gezielt verspäteten Kontakt fehlen ohne kontrollierte Zustellung und eigene Zielmessung die Spielbelege. [PlayerHit-Protokoll](../dotnet/KcdMp.Protocol/ProtocolV8.cs#L239) · [FF-Empfang](../dotnet/KcdMp.Client/GameBridge.Wo121.cs) · [Lebenswechsel und Schutz](../native/KCDMP/p4.cpp)

## Abgabe

Für jede Rolle und Station Build-ID, Join-ID, Uhrzeit, erwarteten und beobachteten Ausgang, Logausschnitt und Sichtbeleg notieren. Pro PC und Start Agent-, Native- und Spiel-Log zusammen sichern. Im Fall eines Fehlers die Zeitspanne um die Markierung erhalten. Beim Teilen Steam-IDs, Secrets und persönliche Daten auslassen. Spielstände bleiben lokal. Der Zwei-PC-Lauf ist ein eigener Abnahmenachweis. [Log-Auswertung](../.agents/skills/kct-log-analyse/SKILL.md)
