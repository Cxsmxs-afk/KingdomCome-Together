# Lane 3 – Einheitliche Markerfarben

`codex/lane3-marker-stable-slots` baut auf `6dce93d9` auf. Der Entwurf richtet sich gegen `codex/s3-1-load-sequence`.

Der Host reserviert Rot für sich und vergibt Violett, Blau und Grün an die ersten Gastnamen seines Relay-Rosters. Innerhalb desselben Abgleichs entscheidet die Relay-ID. Ein Rejoin mit demselben Namen behält seine Farbe in derselben Host-Sitzung. Gleiche Relay-Namen teilen sich eine Identität. Ab der fünften Person gilt Slot 0 (Companion). [Zuteilung](../dotnet/KcdMp.Client/GameBridge.Wo171.cs#L90-L98) · [Slot-Regeln](../dotnet/KcdMp.Client/Wo171.cs#L141-L175) · [Rejoin-Test](../dotnet/KcdMp.Client.Tests/Wo171SlotIntegrationTests.cs#L103-L110)

Nach einer bestätigten `marker-slots-v1`-Tabelle hält der Joiner deren Bildcodes auf demselben Relay-Stream, wenn die Host-ID kurz unbekannt ist. Solange der Joiner noch in der Welt ist, bleiben die Pins auch sichtbar, falls der lokale Modus sonst auf getrennte Welten zurückfallen würde. Eingehende Tabellen brauchen einen aktuell bestätigten Host. Ein neuer Stream, ein anderer bestätigter Host oder bestätigte getrennte Welten leeren die Tabelle. [Cache und Empfang](../dotnet/KcdMp.Client/GameBridge.Wo171.cs#L54-L120) · [Pin-Pfad](../dotnet/KcdMp.Client/GameBridge.Wo171.cs#L123-L166) · [W164MarkNote](../dotnet/KcdMp.Client/GameBridge.Wo164.cs#L358-L360)

Der Relay-interne `IdentityKey` ist für den Host-Agenten nicht Teil der Teilnehmerliste. Das `Name`-Paket liefert Ghost-ID und Anzeigename. WO‑157 schickt den Schlüssel dieses Hosts an seine Gäste. Unter diesen Daten bleibt der Relay-Name der verfügbare Schlüssel für jeden Gast. [Relay-Key](../dotnet/KcdMp.Server/Features/ClientHandling/RelayConnection.cs#L34-L39) · [Name-Paket](../dotnet/KcdMp.Server/Features/ClientHandling/ClientSession.cs#L1612-L1619) · [Empfang](../dotnet/KcdMp.Client/GameBridge.cs#L5200-L5207) · [Host-Schlüssel](../dotnet/KcdMp.Client/GameBridge.Wo124.cs#L218)

## Vergleich

- **NightCoop** verteilt vom Host verwalteten Zustand auch an später Beitretende. Das SDK beschreibt hierfür einen gemeinsamen Schlüsselspeicher. [SDK](https://nightcoop.net/sdk.html)
- **Elden Ring Seamless Co-op** kann seit v1.9.4 mit `append_steam_id_to_players` eine eindeutige Steam-ID am Spielernamen anzeigen. [Changelog](https://www.nexusmods.com/eldenring/mods/510?tab=logs)
- **Skyrim Together Reborn** synchronisiert Gruppen-Wegpunkte und bietet „Reveal Players“. [Feature-Doku](https://wiki.tiltedphoques.com/tilted-online/general-information/features#player-map-markers-waypoints) · [Changelog](https://www.nexusmods.com/skyrimspecialedition/mods/69993?tab=logs)
- **Baldur’s Gate 3** unterstützt die Charakterzuweisung nach einem Rejoin. [Larian Hotfix 15](https://baldursgate3.game/news/hotfix-15-now-live_105)
- **Dark Souls III** kennzeichnet Mehrspielerrollen und Pakte mit Phantomfarben. [PlayStation](https://blog.ja.playstation.com/2016/04/12/20160412-darksouls3/)

## Ampel

| Prüfung | Stand |
|---|---|
| Host-ID-Lücke, Bildcodes, Absender, getrennte Welten und Verbindungswechsel | Grün · beide Host-Lücken-Tests reproduzieren auf `6dce93d9` den Slotverlust. [WO-171-Integrationstests](../dotnet/KcdMp.Client.Tests/Wo171SlotIntegrationTests.cs#L145-L270) |
| Sichtreihenfolge, fünfte Person und synthetisches Log mit `marker_slot_mismatch=0` | Grün. [WO-171-Tests](../dotnet/KcdMp.Client.Tests/Wo171Tests.cs#L15-L108) |
| Windows-CI und Drop-in-`install-verify` | [CI des Fix-Branches](https://github.com/Cxsmxs-afk/KingdomCome-Together/actions/workflows/build-windows.yml?query=branch%3Acodex%2Flane3-marker-stable-slots) |
| Live mit drei bis vier Spielern | Grau · das Spiel-Rig beschreibt zwei Spielinstanzen. [Rig](../tools/rig/rig.py#L4-L12) |

Der Agent zählt `marker_slot_mismatch` nach bekannter Host-Zuteilung am vom nativen Kanal bestätigten Bildcode. Die sichtbare Darstellung im Spiel bleibt ein Live-Test. [Messpunkt](../dotnet/KcdMp.Client/GameBridge.Wo171.cs#L175-L182)
