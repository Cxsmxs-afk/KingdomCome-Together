# Partnermarker und stabile Farben

W5 übernimmt die Host-Zuteilung aus [#41](https://github.com/Cxsmxs-afk/KingdomCome-Together/pull/41) und die Rückkehrregeln aus [#45](https://github.com/Cxsmxs-afk/KingdomCome-Together/pull/45) auf Basis `9ad04fa2` von #68.

Der Host reserviert Rot für sich und vergibt Violett, Blau und Grün an die ersten Gastnamen seines Relay-Rosters. Bei neu gleichzeitig sichtbaren Teilnehmern entscheidet die Relay-ID. Ein Rejoin unter demselben Relay-Namen behält die Farbe auch bei einer neuen Relay-ID. Gleiche Relay-Namen teilen eine Identität. Weitere Teilnehmer erhalten den Companion-Marker. Karte und Kompass verwenden denselben Farbslot. [Zuteilung](../dotnet/KcdMp.Client/GameBridge.Wo171.cs) · [Slot-Regeln](../dotnet/KcdMp.Client/Wo171.cs)

Der Host überträgt die vollständige Zuteilung als `W164MarkNote` mit Präfix `marker-slots-v1|` bei einer neuen Reservierung und ansonsten höchstens alle fünf Sekunden je verbundenem Gast. Beim Host-Hold hält der Joiner seine bestätigte Tabelle auf derselben Relay-Verbindung. Während der Pause nimmt er keine neuen Tabellen an. Bestätigen Welt-Epoche und Lauf-ID die Rückkehr desselben Relay-Namens, behält der Joiner seine Slots auch bei anderer Host-ID. Ein neuer Empfangsstream, eine neue bestätigte Host-Bindung oder bestätigte getrennte Welten lassen die alte Tabelle verfallen. Beim Wiederverbinden des Hosts im selben geladenen Spiel hält dessen Agent die Reservierungen. Die Sichtbarkeit der Pins hängt weiterhin von aktiven Partnern und hinreichend frischen Positionen ab. [Cache und Empfang](../dotnet/KcdMp.Client/GameBridge.Wo171.cs) · [Marker-Tick](../dotnet/KcdMp.Client/GameBridge.Wo164.cs)

Der Agent nutzt die vom Relay übertragenen Namen. Der Relay-interne `IdentityKey` steht im `Name`-Paket nicht zur Verfügung. [Relay-Verbindung](../dotnet/KcdMp.Server/Features/ClientHandling/RelayConnection.cs) · [Name-Paket](../dotnet/KcdMp.Server/Features/ClientHandling/ClientSession.cs)

## Abnahme

- Die Tests für zwei, drei und vier Spieler schicken die Tabelle über echtes TCP-Relay zu den produktiven Gast-Readern. Je Rolle vergleichen sie dieselben Farbslots bei unterschiedlicher Sichtreihenfolge.
- Weitere Tests prüfen die Pause, verweigerte Snapshot-Updates, alte Relay-IDs und je fünf Wiedereintritte auf dem Host mit neuer Gast-ID. Native Engine und Spielansicht sind in diesen Tests nicht enthalten.
- Für die Spielabnahme bestätigen `WO171-MARK` und `MP-T3-STATS marker_slot_mismatch=0` den vom Agenten an die DLL gegebenen Farbcode. Die tatsächlichen Farben in Karte und Kompass prüfen beide PCs vor und nach Host-Pause und Rückkehr. [Regressionen](../dotnet/KcdMp.Client.Tests/Wo171SlotIntegrationTests.cs)

NightCoop bietet einen gemeinsamen Zustand für später beitretende Spieler. Die Marker-Tabelle verwendet denselben Grundsatz. Elden Ring Seamless Co-op kann Steam-IDs an Spielernamen anhängen. KCTs Markertabelle nutzt die in seiner Teilnehmerliste verfügbaren Relay-Namen. [NightCoop SDK](https://nightcoop.net/sdk.html) · [Seamless Co-op Changelog](https://www.nexusmods.com/eldenring/mods/510?tab=logs)
