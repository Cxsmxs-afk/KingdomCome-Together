# Partnermarker und stabile Farben

W5 übernimmt die Host-Zuteilung aus [#41](https://github.com/Cxsmxs-afk/KingdomCome-Together/pull/41) und die Rückkehrregeln aus [#45](https://github.com/Cxsmxs-afk/KingdomCome-Together/pull/45) auf Basis `9ad04fa2` von #68.

Der Host reserviert Rot für sich und vergibt Violett, Blau und Grün an die ersten Mitglieder seines Relay-Rosters. Bei neu gleichzeitig sichtbaren Teilnehmern entscheidet die Ghost-ID. Ein Rejoin desselben Relay-Mitglieds behält den Farbslot bei einer neuen Ghost-ID. Zwei Mitglieder mit gleichem Anzeigenamen erhalten eigene Farben. Weitere Teilnehmer erhalten den Companion-Marker. Karte und Kompass verwenden denselben Farbslot. [Zuteilung](../dotnet/KcdMp.Client/GameBridge.Wo171.cs) · [Slot-Regeln](../dotnet/KcdMp.Client/Wo171.cs)

Der Host überträgt die Zuteilung als `W164MarkNote` mit Präfix `marker-slots-v2|` bei einer neuen Reservierung und ansonsten höchstens alle fünf Sekunden je verbundenem Gast. In der Pause hält der Joiner seine bestätigte Tabelle. Welt-Epoche, Lauf-ID und derselbe Mitgliedsschlüssel erlauben das Beibehalten der Slots bei einer anderen Ghost-ID des Hosts. Ein neuer Relay-Prozess eröffnet eine neue Farbzuordnung. Beim Wiederverbinden des Hosts mit demselben Relay im selben geladenen Spiel hält dessen Agent die Reservierungen. Die Sichtbarkeit hängt von aktiven Partnern und frischen Positionen ab. [Cache und Empfang](../dotnet/KcdMp.Client/GameBridge.Wo171.cs) · [Marker-Tick](../dotnet/KcdMp.Client/GameBridge.Wo164.cs)

Das Relay sendet Instanz und opaken Mitgliedsschlüssel bei der Einführung eines Teilnehmers. Die Zuordnung verwendet die bestehende Relay-Identität aus Transport und Namen. Zwei TCP-Verbindungen mit identischer Quelladresse und identischem Namen gelten bereits beim Handshake als derselbe Teilnehmer. Für zwei Spieler hinter derselben NAT-Adresse sind deshalb unterschiedliche Anzeigenamen nötig. [Relay-Identität](../dotnet/KcdMp.Server/Features/ClientHandling/RelayConnection.cs) · [Einführung](../dotnet/KcdMp.Server/Features/ClientHandling/ClientSession.cs)

## Abnahme

- Die Tests für zwei, drei und vier Spieler schicken die Tabelle über echtes TCP-Relay zu den produktiven Gast-Readern. Je Rolle vergleichen sie dieselben Farbslots bei unterschiedlicher Sichtreihenfolge.
- Weitere Tests prüfen die Pause, verweigerte Snapshot-Updates, alte Ghost-IDs sowie zwei gleichnamige Gäste mit einem Austausch der physischen Verbindung. Die Engine und die sichtbare Farbe prüfen die Spieltests.
- Für die Spielabnahme bestätigen `WO171-MARK` und `MP-T3-STATS marker_slot_mismatch=0` den vom Agenten an die DLL gegebenen Farbcode. Die tatsächlichen Farben in Karte und Kompass prüfen beide PCs vor und nach Host-Pause und Rückkehr. [Regressionen](../dotnet/KcdMp.Client.Tests/Wo171SlotIntegrationTests.cs)

NightCoop bietet einen gemeinsamen Zustand für später beitretende Spieler. KCT überträgt die vollständige Farbzuordnung an neue Gäste und sucht Farben anhand der Relay-Mitgliedschaft. [NightCoop SDK](https://nightcoop.net/sdk.html)
