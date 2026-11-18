---
name: kct-coop-vergleich
description: "Kompakte Referenz, wie NightCoop (Cyberpunk 2077), Elden Ring Seamless Co-op, Skyrim Together Reborn und Baldur's Gate 3 Join, Rollenwahl, Spielstände, Uhr/Zeit, NPC-Autorität, Dialog, Loot und Latenz lösen, und was daraus für KCD2 folgt. Laden, wenn ein Paket, Review oder Prompt den Pflicht-Vergleich mit anderen Koop-Mods braucht oder eine Designentscheidung (Join, Autorität, Sync) ansteht."
---

# kct-coop-vergleich — Koop-Mechaniken und ihre Anwendung in KCD2

Der Vergleich im Bericht hat 1–2 Sätze: Wie lösen X/Y das, was ist in KCD2 machbar und am besten, mit Quelle. Warum: Die oberste Regel misst den Mod an Seamless Co-op und NightCoop. Diese Tabelle spart die Recherche, ersetzt aber keine gezielte Suche bei neuen Themen.

**[Q]** bezeichnet eine Hersteller- oder Projektquelle und **[K]** eine Community-Quelle. Ein Strich zeigt, dass zu diesem Thema kein Beleg in der Tabelle steht. Die KCD2-Spalte unterscheidet den geprüften H0-Stand von Paket-Zielen.

## Quellen
- NC1 [NightCoop SDK – Concepts](https://nightcoop.net/sdk-concepts.html)
- NC2 [NightCoop SDK – Limits and versions](https://nightcoop.net/sdk-limits-and-versions.html)
- NC3 [NightCoop SDK – Effects on NPCs a partner leads](https://nightcoop.net/sdk-npc-effects.html)
- NC4 [NightCoop – Kampagne, Dialog und Rollenwahl](https://nightcoop.net/)
- ER1 [Seamless Co-op – Installation und Sitzungsgegenstände](https://ersc-docs.github.io/how-to-install-and-update/)
- ER2 [Seamless Co-op – FAQ](https://ersc-docs.github.io/faq/)
- ER3 [Seamless Co-op – Changelog 1.5.0](https://www.nexusmods.com/eldenring/articles/241)
- ST1 [Tilted Online – Technische Übersicht](https://wiki.tiltedphoques.com/tilted-online/technical-documentation/overview)
- ST2 [Tilted Online – Server-Konfiguration](https://wiki.tiltedphoques.com/tilted-online/guides/server-guide/server-configuration)
- ST3 [Tilted Online – Features](https://wiki.tiltedphoques.com/tilted-online/general-information/features)
- ST4 [Tilted Online – Playguide](https://wiki.tiltedphoques.com/tilted-online/general-information/playguide)
- BG1 [Larian – Multiplayer und Dialogstimmen](https://baldursgate3.game/news/community-update-21-forging-your-legacy_77)
- BG2 [BG3 Community-Wiki – Multiplayer](https://bg3.wiki/wiki/Multiplayer)
- BG3 [Larian – Hotfix 4 und Zustimmung bei laufender Charaktererstellung](https://store.steampowered.com/news/posts/?appids=1086940&enddate=1692288740&feed=steam_community_announcements)

## Vergleich nach Thema
| Thema | NightCoop (CP2077) | Seamless Co-op (ER) | Skyrim Together | BG3 | → KCD2 (dieses Repo) |
|---|---|---|---|---|---|
| **Welt** | Das Spiel des Hosts führt Zeit, Wetter, NPCs und Quests [Q NC1] | 1.5.0 gleicht beim Laden alle Flags ab und legt fest, welche vom Host oder aus dem eigenen Spiel gelesen werden [Q ER3] | Der Server repliziert Zustand und vergibt Autorität. Clients führen die jeweiligen Zellen und Figuren aus [Q ST1] | Die Kampagne liegt beim Host [K BG2] | Host-Save ist die Welt (`AGENTS.md`). Zustandswerte kommen vom Host (`dotnet/KcdMp.Protocol/ProtocolWo200.cs`). |
| **Join** | Der Gast betritt die Host-Welt mit eigenem Charakter. `InWorld` und `InHostWorld` unterscheiden Menü, Laden und Welten [Q NC1, NC4] | Host startet mit Tiny Great Pot, Gäste mit Effigy of Malenia. Das Passwort muss übereinstimmen [Q ER1] | Spieler verbinden sich mit dem Server und bilden eine Party. Automatischer Party-Beitritt ist konfigurierbar [Q ST2, ST4] | Drop-in in eine laufende Partie. Bei Beitritt während der Charaktererstellung muss der Host zustimmen [Q BG1, BG3] | Weltübertragung und Laden aus dem Hauptmenü. H20 muss den gefrorenen Kalender im Menü vor dem World-Sync annehmen. Abbrüche mit Grund protokollieren. |
| **Rollenwahl** | Das Coop-Menü bietet Hosten und Beitreten per Code [Q NC4] | Die Gegenstände wählen die Rolle pro Sitzung [Q ER1] | Separater Server und davon unabhängiger Party-Leader [Q ST2, ST4] | Der Ersteller des Multiplayer-Spielstands hostet dessen Folgesitzungen [K BG2] | H22 soll beide Rollen auf jedem PC auch nach einem Direct-Test erreichbar machen. |
| **Spielstände** | Beim Beitritt wird die Host-Welt mit dem eigenen Charakter kombiniert, beim Verlassen umgekehrt [Q NC1] | Eigene, konfigurierbare Dateiendung, standardmäßig `.co2`. Weltfortschritt orientiert sich am Host [Q ER1, ER2] | – | Der Host speichert und lädt [K BG2] | Ziel H19 ist höchstens ein Join-Slot pro Host-Welt, mit Aufräumen bei Abbruch und unveränderten persönlichen Saves. H21 liefert die geprüften Start-Saves im Drop-in. |
| **Uhr/Zeit** | Die Weltzeit kommt vom Host. Die Welt steht, wenn alle Spieler im Menü sind [Q NC1] | – | Der Server führt die Weltzeit. Die Synchronisierung des Kalenderdatums ist konfigurierbar [Q ST2, ST3] | – | `W164Clock` übermittelt die Host-Uhr, an der sich der Joiner ausrichtet (`dotnet/KcdMp.Protocol/ProtocolWo200.cs`). Der Host sendet den gefrorenen Kalender vor dem Join-Angebot (`dotnet/KcdMp.Client/GameBridge.Wo123.cs`). |
| **NPC-Autorität** | Jeder synchronisierte NPC hat einen Owner, meist den Host. Die anderen sehen Kopien ohne eigene KI [Q NC3] | – | Der Server bestätigt einen Owner pro Actor. Andere Clients spiegeln den Actor [Q ST1] | – | Einzelne Körperführung in `native/KCDMP/holder_rules.h`. Gate `owner_violations=0` bei aktiv gemessenen Setter-Aufrufen. |
| **Schaden/Treffer** | Ein Treffer auf der Kopie geht als Damage Claim an den NPC-Owner, der ihn in seiner eigenen Schadenslogik verarbeitet [Q NC3] | – | Der synchronisierte NPC-Zustand umfasst Kampf und Tod [Q ST3] | – | Ein autorisierter Fernkampfpfad mit P6 (`dotnet/KcdMp.Client/GameBridge.P6Missile.cs`). Friendly Fire ist standardmäßig an (`dotnet/KcdMp.Client/GameBridge.Wo121.cs`). |
| **Dialog/Abstimmung** | Story-Entscheidungen per Stimmenmehrheit. Das SDK beschreibt zusätzlich Host-Optionen für eigene Abstimmungen [Q NC1, NC4] | – | Party-Leader führt Dialog und Quest [Q ST3, ST4] | Andere können Gesprächen zuhören und für Antworten stimmen [Q BG1] | Vorgabe ist Mehrheit, bei Gleichstand entscheidet der Gesprächsstarter. Im aktuellen Pfad bestätigt der Empfänger die dargestellte Stimme (`dotnet/KcdMp.Client/GameBridge.Wo204Conv.cs`). Diese Quittung belegt keine Mehrheitsentscheidung. |
| **Loot/Truhen** | – | Zufalls-Loot ist pro Spieler instanziert [Q ER2] | Truheninhalte werden geteilt. Die Dokumentation nennt Truhen in Spielerhäusern als Ausnahme [Q ST3] | – | Truhen und Kräuter sind pro Spieler (`AGENTS.md`). |
| **Zustand/Latenz** | SDK-Pose etwa 30 Hz. Das SDK bietet Streams und Latest-only, mit kurzfristigen Bursts bis zum Vierfachen seines Ratenlimits [Q NC2] | – | Der Server repliziert vom Client gemeldete Änderungen wie Position, Animation und Ausrüstung [Q ST1] | – | Ziel für gehaltene Zustände ist die regelmäßige Meldung des letzten Werts inklusive „aus“. Neue Werte, Session-Reset oder ein echter Verbindungsverlust beenden den Zustand. Bewegung nutzt Latest-wins (`dotnet/KcdMp.Protocol/LinkScheduler.cs`). |
| **Tod** | Ein niedergeschlagener Spieler kann aufstehen oder Hilfe erhalten. Wenn alle niedergeschlagen sind, lädt der Host den letzten Save [Q NC1, NC4] | – | – | – | `s2-6` prüft KO und Aufstehen in beiden Richtungen (`docs/ABNAHME-S2.md`). |

## Ableitungen, die in KCD2 gelten
1. **Join darf nie still scheitern.** NightCoop trennt „im Menü“ von „in der Host-Welt“. Bei H20 muss der Joiner den Kalender vor dem Laden annehmen, während World-Sync noch auf die gemeinsame Welt wartet.
2. **Rolle pro Sitzung, beide Knöpfe immer erreichbar** (Seamless, NightCoop). Warum: Am 18.11. konnte Lasse nur joinen.
3. **Fehl-Join hinterlässt keine verwaisten Slots.** Seamless zeigt, wie man Koop-Spielstände separat führt. Die gezielte Bereinigung nach einem KCD2-Join-Abbruch ist die Aufgabe von H19.
4. **Ein Owner pro Ding, Aktionen zum Owner** (NightCoop, Skyrim Together). Warum: Zwei Schreiber erzeugen Zittern und doppelten Schaden.
5. **Stream statt Momentaufnahme** (NightCoop, Skyrim Together). Warum: Gehaltene Zustände (Block, Bogen) müssen sichtbar bleiben.
6. **Abstimmung nach Mehrheitsregel.** NightCoop belegt den Vergleich für Mehrheitsvoten. Die zusätzliche Entscheidung durch den Gesprächsstarter bei Gleichstand ist die KCD2-Vorgabe.

## So wird der Vergleich im Bericht geschrieben
`Vergleich: <Mod> löst <Thema> durch <Mechanik> [Quelle]. In KCD2 machbar/besser: <Ansatz>, weil <Engine-Grenze/Repo-Fakt mit Datei:Zeile>.` Neues Thema ohne Zeile hier: gezielt suchen, Quelle verlinken, Tabelle per PR ergänzen.
