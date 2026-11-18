# B1 Bogenklasse in der Hand

Der Agent übernimmt die echte Bogen-GUID aus der vorhandenen typisierten P6-Erfassung. Ein frischer Erfassungswert ergänzt einen gezogenen WO-214-Handeintrag mit fehlender Klasse. Ein ausdrückliches Wegstecken, eine gemeldete Klasse oder ein Wechsel des P6-Geltungsbereichs beendet die Übernahme des alten Erfassungswerts. Ohne frischen Nachweis bleibt die Handmeldung mit leerer Klasse für unbewaffnete Figuren verfügbar.

Der Empfänger übergibt die geprüfte Bogen-GUID beim Spannen, Zielen oder Schießen an den bestehenden WO-214-Handeinstieg. Eine leere Meldung aus dem Ein-Slot-Leser verdrängt eine aktuelle Messung aus beiden Händen nicht. Die vorhandene 25-Sekunden-Frist für WO-214 schützt die gemessene Klasse beim Outfit-Abgleich. Ein neuerer Steckvorgang oder eine andere gemeldete Waffenklasse hat Vorrang.

Das Rig liest getrennte Agent-Logs desselben Durchlaufs.

```sh
python3 tools/rig/scen_lane1_bogen.py --sender-log sender.log --receiver-log receiver.log --bow-class BOGENKLASSEN-UUID
```

Die Prüfung verlangt übereinstimmende Bogen-GUIDs im gesendeten Schritt und im Handauftrag des Empfängers sowie einen nachfolgenden T3-Zähler `bow_hand_empty_while_drawn=0`. Ein Handauftrag belegt die Übergabe an den vorhandenen Spieleeinstieg. Die tatsächliche Handbelegung und Sichtbarkeit brauchen die Spielabnahme.

## Übergabe an H4

Beim bloßen Ziehen vor der ersten Schießphase liefert die bestehende Native-Erfassung noch keinen neuen Beleg. Der Lua-Sender liest nur Slot 0. Der Lua-Empfänger prüft Slot 0 und verwendet diesen auch im letzten Draw-Fallback. H4 muss die tatsächliche Bogenhand auf beiden Seiten prüfen und die Handkorrektur mit dem Zeitpunkt der nativen Fire-Anforderung abstimmen. Ein bereits begonnener Outfit-Abgleich kann eine alte Entfernungsliste abarbeiten. [Native Erfassung](../native/KCDMP/wo201_ranged.cpp) · [WO-214 Lua](../kdcmp/Data/Scripts/Startup/kdcmp.lua#L36213) · [Outfit-Abgleich](../dotnet/KcdMp.Client/GameBridge.cs#L2988)
