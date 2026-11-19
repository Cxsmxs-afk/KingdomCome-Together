# B1 Bogenklasse in der Hand

Der Agent übernimmt die Bogen-GUID aus einer typisierten P6-Erfassung, die beim Eingang höchstens zwei Sekunden alt ist. Die gemessene Klasse ergänzt beim Sender auch spätere gezogene WO-214-Handmeldungen. Wegstecken, eine andere nachgewiesene Waffenklasse, ein Wechsel des P6-Modus oder ein Session-Reset beenden die Übernahme. Eine unklare Messung lässt eine bereits bestätigte Klasse bestehen. Ohne je bestätigte Klasse kann der Agent weiterhin eine leere Hand melden.

Der Empfänger übergibt die geprüfte Bogen-GUID beim Spannen, Zielen oder Schießen an den bestehenden WO-214-Handeinstieg. Die gemessene Bogenklasse bleibt beim Outfit-Abgleich auch über die allgemeine 25-Sekunden-Frist hinaus geschützt, solange Handzustand und P6-Geltungsbereich passen. PreDraw verwendet die zuletzt angeforderte gezogene Klasse. Nach einem Wechsel vom Schwert zum Bogen legt es beim nächsten Ziehen damit den Bogen zurück.

## Wiederholung des Handzustands

Der Agent wiederholt die bestätigte gezogene Klasse und den bestätigten Wegsteckzustand über `W203FigureHand`. Eine leere lokale Lua-Meldung lässt die bestätigte Klasse für den Heartbeat bestehen. Der bestehende Sitzungstakt prüft das Intervall `BodyState2HeartbeatMs` von 1.000 ms unabhängig vom Ranged-Poll. Vor dem Schreiben prüft der Agent erneut Zustand, Sitzung und Zielpeer. Pro Peer wartet höchstens eine Wiederholung desselben Zustands in der Schreibwarteschlange. [Klassenbindung](../dotnet/KcdMp.Client/GameBridge.Wo214Hands.cs#L100) · [Sitzungstakt](../dotnet/KcdMp.Client/GameBridge.Wo201Ranged.cs#L99) · [Intervall](../dotnet/KcdMp.Protocol/ProtocolV8.cs#L295) · [Versandprüfung](../dotnet/KcdMp.Client/GameBridge.Wo214Hands.cs#L149)

Eine leere gezogene Meldung aktualisiert den Empfangszeitpunkt der zuletzt bestätigten Hand. Neuere vollständige Meldungen verdrängen ältere, noch wartende Meldungen derselben Verbindung. Der Outfit-Abgleich hält die Klasse bis zu einem neueren Handzustand oder dem Ende ihres Geltungsbereichs. Auf bestätigtes Wegstecken folgt der bestehende kosmetische Holster-Aufruf. Für identische Meldungen gilt bei Spielaufrufen der bisherige Zehn-Sekunden-Takt. Die Lua-Timer können dadurch auslaufen. [Empfang](../dotnet/KcdMp.Client/GameBridge.Wo214Hands.cs#L229) · [Holster-Aufruf](../dotnet/KcdMp.Client/GameBridge.Wo214Hands.cs#L355) · [Lua-Timer](../kdcmp/Data/Scripts/Startup/kdcmp.lua#L36237)

Skyrim Together Reborn v1.8.4 sendet Bewegungszustände regelmäßig und den Waffenstatus bei Änderung. TES3MP überträgt `drawState` als ausdrücklichen Wert im Animationszustand. Für KCD2 kombiniert WO-214 die periodische Übertragung mit ausdrücklichen Werten für beide Handzustände auf dem vorhandenen Kanal. [Skyrim Bewegung](https://github.com/tiltedphoques/TiltedEvolution/blob/54b3a858e228dc8807ed65bc3f9ac3c3f81b610d/Code/client/Services/Generic/CharacterService.cpp#L1814-L1873) · [Skyrim Waffenstatus](https://github.com/tiltedphoques/TiltedEvolution/blob/54b3a858e228dc8807ed65bc3f9ac3c3f81b610d/Code/client/Services/Generic/InventoryService.cpp#L290-L324) · [TES3MP Zustandspaket](https://github.com/TES3MP/TES3MP/blob/49be5b6405d6ab427e06ed350cf76c715a1f3bdd/components/openmw-mp/Packets/Player/PacketPlayerAnimFlags.cpp#L9-L18)

Das Rig liest getrennte Agent-Logs desselben Durchlaufs.

```sh
python3 tools/rig/scen_lane1_bogen.py --sender-log sender.log --receiver-log receiver.log --bow-class BOGENKLASSEN-UUID
```

Für das Rig den Bogen nach einer erfassten Spann-, Ziel- oder Schießphase gezogen halten und danach wegstecken. Die Prüfung verlangt einen tatsächlich gesendeten WO-214-Heartbeat mit derselben Klasse mehr als zwei Sekunden nach der letzten Messung, einen folgenden T3-Stand von `bow_hand_empty_while_drawn=0`, eine Wiederholung der Wegsteckmeldung mit derselben Revision und die Empfangsbestätigung des Handzustands. Der Zähler sammelt Ereignisse über die Laufzeit. Die tatsächliche Handbelegung und Sichtbarkeit brauchen die Spielabnahme. [Senderprüfung](../tools/rig/scen_lane1_bogen.py#L26) · [Empfängerprüfung](../tools/rig/scen_lane1_bogen.py#L113)

## Übergabe an H4

Spanngrad, Zielrichtung, sichtbare Spann- und Zielphasen sowie Pfeilflug brauchen weiterhin ihren Spielnachweis im Hauptchat. [B2 Posen](LANE1-BOGEN-AIM.md) · [B3 Pfeil](LANE1-BOGEN-SHOT.md)

Beim bloßen Ziehen vor der ersten Schießphase liefert die bestehende Native-Erfassung noch keinen neuen Beleg. Der Lua-Sender liest nur Slot 0. Der Lua-Empfänger prüft Slot 0 und verwendet diesen auch im letzten Draw-Fallback. H4 muss die tatsächliche Bogenhand auf beiden Seiten prüfen und die Handkorrektur mit dem Zeitpunkt der nativen Fire-Anforderung abstimmen. Ein bereits begonnener Outfit-Abgleich kann eine alte Entfernungsliste abarbeiten. [Native Erfassung](../native/KCDMP/wo201_ranged.cpp) · [WO-214 Lua](../kdcmp/Data/Scripts/Startup/kdcmp.lua#L36213) · [Outfit-Abgleich](../dotnet/KcdMp.Client/GameBridge.cs#L2988)
