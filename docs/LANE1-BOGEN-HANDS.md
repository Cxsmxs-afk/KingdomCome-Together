# B1 Bogenklasse in der Hand

Der Agent übernimmt die Bogen-GUID aus einer typisierten P6-Erfassung, die beim Eingang höchstens zwei Sekunden alt ist. Die gemessene Klasse ergänzt beim Sender auch spätere gezogene WO-214-Handmeldungen. Wegstecken, eine andere nachgewiesene Waffenklasse, ein Wechsel des P6-Modus oder ein Session-Reset beenden die Übernahme. Eine unklare Messung lässt eine bereits bestätigte Klasse bestehen. Ohne je bestätigte Klasse kann der Agent weiterhin eine leere Hand melden.

Der Empfänger übergibt die geprüfte Bogen-GUID beim Spannen, Zielen oder Schießen an den bestehenden WO-214-Handeinstieg. Die gemessene Bogenklasse bleibt beim Outfit-Abgleich auch über die allgemeine 25-Sekunden-Frist hinaus geschützt, solange Handzustand und P6-Geltungsbereich passen. PreDraw verwendet die zuletzt angeforderte gezogene Klasse. Nach einem Wechsel vom Schwert zum Bogen legt es beim nächsten Ziehen damit den Bogen zurück.

Das Rig liest getrennte Agent-Logs desselben Durchlaufs.

```sh
python3 tools/rig/scen_lane1_bogen.py --sender-log sender.log --receiver-log receiver.log --bow-class BOGENKLASSEN-UUID
```

Für das Rig den Bogen nach einer erfassten Spann-, Ziel- oder Schießphase gezogen halten. Die Prüfung verlangt einen tatsächlich gesendeten WO-214-Heartbeat mit derselben Klasse mehr als zwei Sekunden nach der letzten Messung, einen Handauftrag des Empfängers und danach `bow_hand_empty_while_drawn=0` im T3-Bericht des Senders. Der Zähler sammelt Ereignisse über die Laufzeit. Die tatsächliche Handbelegung und Sichtbarkeit brauchen die Spielabnahme.

## Übergabe an H4

Beim bloßen Ziehen vor der ersten Schießphase liefert die bestehende Native-Erfassung noch keinen neuen Beleg. Der Lua-Sender liest nur Slot 0. Der Lua-Empfänger prüft Slot 0 und verwendet diesen auch im letzten Draw-Fallback. H4 muss die tatsächliche Bogenhand auf beiden Seiten prüfen und die Handkorrektur mit dem Zeitpunkt der nativen Fire-Anforderung abstimmen. Ein bereits begonnener Outfit-Abgleich kann eine alte Entfernungsliste abarbeiten. [Native Erfassung](../native/KCDMP/wo201_ranged.cpp) · [WO-214 Lua](../kdcmp/Data/Scripts/Startup/kdcmp.lua#L36213) · [Outfit-Abgleich](../dotnet/KcdMp.Client/GameBridge.cs#L2988)
