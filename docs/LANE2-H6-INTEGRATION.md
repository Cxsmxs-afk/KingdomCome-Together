# Lane 2 · H6-Integration auf H22

Draft #54 führt den Stand `9228de7159cf2d9f8911dda96bed9e15fdd36a12` auf H22 (`18d3dfd69bcd9068ce27b76c4266cb752ba9e614`) fort. Der originale Lane-Stand mit den beiden separat geprüften Übergabepatches ist `91798c4b1d618650a40de4fdda3a7200d7e476cd`. [Draft #54](https://github.com/Cxsmxs-afk/KingdomCome-Together/pull/54)

## Relay-Handshake

Der historische PR-Lauf las bei fünf Markerfällen einen Header und erhielt für den angekündigten Body ein EOF. Die Marker-Assertions wurden nicht erreicht. Der bestehende Log belegt weder einen fremden Listener noch den tatsächlichen Close-Grund. Auf demselben Dateistand war der Push-Lauf grün. Die Tests prüfen jetzt den vom OS tatsächlich gebundenen Endpoint und den eigenen Verbindungstest, inklusive Antworttyp, deklarierter/Lese-Länge und Fixture-Identität bei Fehlern. Das `FreePort`-Fenster und die acht Bind-Retries entfallen. WO144 läuft mit einem eigenen Relay pro Test. Ein kontrollierter Lauf auf dem alten Tree reproduzierte separat den zuerst gelesenen Disconnect von Peer 0 statt Peer 2. [Historischer PR-Lauf](https://github.com/Cxsmxs-afk/KingdomCome-Together/actions/runs/44447771182) · [Historischer Push](https://github.com/Cxsmxs-afk/KingdomCome-Together/actions/runs/44447734044)

```powershell
dotnet test dotnet/KcdMp.Relay.Tests/KcdMp.Relay.Tests.csproj -c Release --filter FullyQualifiedName~P5DeathMarkerRelayTests
dotnet test dotnet/KcdMp.Relay.Tests/KcdMp.Relay.Tests.csproj -c Release
```

## H6 · Native Todesgrenze

`GameBridge.NpcDeathDispatch.cs` nutzt denselben Auftrag für benannte Meldungen, fatale Schadensmeldung und expliziten GUID-Fallback. `ApplyDeath` trägt Soul-GUID, optionale EID, native Lebensgeneration und Versuch. Der native Resolver benutzt für den GUID-Fallback die höchstens 64 beobachteten nahen Souls und den vorhandenen WUID-Index. Entity, Actor und konkrete Soul-Verbindung werden zurückgeprüft. Eine unbestätigte GUID erzeugt `guid-tracked-binding-unproven`. Der fatale Schadensdelta bleibt diagnostisch. Numerischer NPC-Schaden setzt nach erfolgreichem Health-Read eine 1-HP-Untergrenze. Der Todespfad setzt eine Freigabe nur nach beendetem eigenen Auftrag oder nachgewiesener Abwesenheit eigener Aufträge fort. Der Queue-Aufruf reserviert davor beide Tracking- und Hook-Nachweise.

| Ausgang | Folge |
| --- | --- |
| `Pending` | Derselbe Versuch wartet auf `Finished` oder liest den letalen Engine-Zustand erneut. Nur `Finished` oder fehlende eigene Aktionen können die Freigabe bestätigen. |
| `Applied` | Der Motor meldet `IsDead` für dasselbe Ziel. Das schließt eine bereits tote Figur ein. |
| `Stale` | Eine Körper-, Welt-, Lade- oder Pipe-Bindung ist abgelaufen. |
| `Uncertain` | Tracking, Release, Guard-Readback oder Engine-Quittung blieb offen. Der Versuch wird nicht als neue Mutation wiederholt. |

Erste bestätigte Freigabe, abgeschickte Mutation und gelesenes `IsDead` stehen mit derselben Versuch-ID in `MP-LANE2-H6 stage=ready/death-dispatched/applied`. `WO221-ACTEND` nennt wartende und aktive Aktionen beim ersten Versuch. Der verspätete `WO195-FALL`-Pfad liest den Zustand. Sein früherer EID-/Namens-basierter Abschluss hatte eine Lücke zwischen Lesebeleg und Mutation und entfällt.

```bash
dotnet test dotnet/KcdMp.Client.Tests/KcdMp.Client.Tests.csproj -c Release --filter 'FullyQualifiedName~Lane2NpcDeathIngressTests|FullyQualifiedName~Wo195Tests'
tools/sim/run-sim ci /tmp/kct-h6-integration --timeline both
```

## H15/H16 und N3 · Herkunft und Grenzen

Der H15-Übergabepatch basiert auf `a23dec8d1c554ca9902773581951ad53ddfc5807` und hat SHA-256 `eae61b2da760c6235723316b7a612419614d7f88189656e4d028d4aaf7e579a2`. Der Produktions-Scheduler ist dort bis `91798c4b` identisch. Der ursprüngliche Patch bestand 26/26 Prüfungen mit Engine-Doubles. Die integrierte Pflichtsuite erweitert das auf 30 Fälle. Sie war mit dem alten Scheduler 19 grün und 11 rot, mit der Integration 30/30 und auch mit ASan/UBSan grün. `Accepted` bedeutet Einreihung. Eine installierte Aktion braucht eine positive native Ausführungsbestätigung. Sicher abgelaufene Zeilen können höchstens einen abgesicherten Versuch mit derselben ID haben. Mehrdeutige Dispatches werden nicht wiederholt.

H16 fällt von einer authored Typ-14-Zeile ohne Perfect-Flag nur auf eine einzelne, eindeutige gewöhnliche Blockzeile zurück. Der beim Katalogladen aufgebaute Index prüft Spielerfigur, Figurklasse, beide Waffen, Zone und vereinbare Tags. Auf `9228de71` scheiterte die positive Probe vor der ersten Pipe-Anforderung. Mit dem Fix bestanden 124 gezielte Tests. Die Fälle für 55/62, Sync und mehrdeutige Kontexte blieben geschlossen. Riposte, Combo und Paaraktionen benötigen separate Engine-Belege.

Der FF-Quittungspatch auf `18d3dfd69bcd9068ce27b76c4266cb752ba9e614` hat SHA-256 `50a7ebf3566b0f2225ef4ab7d1857172aac7c8d078849ff489b3c5b5e9276a4d`. Die drei ursprünglichen Reader-/Pipe-Proben mit Doubles waren nach der Änderung grün. `protected_consumed` bestätigt ein verbrauchtes Schutzfenster mit 0 HP/ST. `acknowledged` ist der Positiv-Ack ohne beobachtete Wirkung. HP/ST-Änderung zählt über tatsächliche Messwerte am selben Ziel. Nach einem unklaren Engine-Aufruf gibt es keinen zweiten Versuch über die andere TakeDamage-Überladung.

`LinkCapPvpContact` verhandelt eine Sequenz pro gemeldetem Treffer und die Relay-Inkarnation des Empfängers. Das Relay setzt die authentifizierte Sender-Inkarnation beim Weiterleiten. Der Empfang verbraucht die ID vor der Pipe-Mutation und bindet sie an die aktuelle lokale Sitzung. Up- und Down-Body wachsen je um 16 Bytes, der Treffertakt bleibt unverändert. Die zusätzliche 9-Byte-Peer-Meldung folgt dem Capability-Beitritt und geht nur an unterstützende Peers. Alte Clients behalten ihren gemeinsamen alten Pfad. Die Paket-ID bezeichnet den gemeldeten Messwert; der native Watch kann mehrere physische Kontakte in diesem Wert zusammenfassen.

## Verhaltensänderungen gegenüber `5cdec914`

Der H6-Agent hält bei `Pending` denselben Versuch 2,5 Sekunden offen. Er sendet nach einem unklaren Apply nur noch `ObserveOnly`. Das finale `AbortBeforeApply` darf die Sperre erst aufheben, wenn der Ledger den noch lebenden Originalkörper vor der Mutation bestätigt. `preparation-not-started` erlaubt diese Prüfung und sperrt weitere `Advance`-Anfragen für denselben Versuch. Bei `preparation-uncertain` hält der Ledger die Sperre, da die Engine-Holds bereits geändert sein können. Ein fehlendes Ack erzeugt keine weitere Mutation. Namens-, GUID- und fatale Meldungen behalten die gemeinsame Grenze. [Agent](../dotnet/KcdMp.Client/GameBridge.NpcDeathDispatch.cs) · [Ledger](../native/KCDMP/npc_death_release.h)

Wenn die native Zieluhr fehlt, wartet H15 weiterhin 750 ms. Danach kann eine frische akzeptierte Partnerpose derselben Körper-, Lebens-, Welt- und Pipe-Bindung die bereits angenommene typisierte Aktion einmal zur bestehenden Schadensschutzprüfung führen. Der Scheduler protokolliert `guarded-live-pose`. Aus Posen mit belastbarem Host-Zeitstempel kann er den Replay-Wasserstand fortschreiben. Bei dauerhaft fehlenden Zeitstempeln schließt die vorhandene Grenze von 256 Aktionsquittungen weitere Annahmen, bis ein verlässlicher Zeitbezug wieder vorliegt. [Scheduler](../native/KCDMP/rp_timeline_actions.cpp) · [Pose-Nachweis](../native/KCDMP/npc_drive.cpp) · [Engine-Doubles](../native/tests/rp_timeline_outcome_integration.cpp)

`LinkCapPvpLife` ergänzt den bestehenden Friendly-Fire-Kontakt um den nativen Lebensnachweis des Empfängers. Die DLL erzeugt den Nachweis und prüft direkt vor der Mutation den aktuellen Soul und WUID. Das Relay verteilt Änderungen über `0x95` und die Peer-Meldung `0x94`. `0x44` und `0x45` wachsen mit ausgehandeltem Nachweis um je acht Bytes. `GameBridge.cs` liest die Lebensbindung am Eingang desselben Relay-Streams. Bereits gebundene Kontakte eines alten Lebens werden auch bei verspätetem Reader-Eingang verworfen. Ein gemischtes Paar aus neuem und älterem Protokollstand kann keine Friendly-Fire-Kontakte austauschen. Die ältere Paketform bleibt für gleich alte Teilnehmer verfügbar. Der native Messwert `0x97` erhält die Lebensbindung erst am Sender im Agenten. Der Fall eines dort verspätet verarbeiteten Messwerts erfordert einen eigenen Nachweis. [Reader](../dotnet/KcdMp.Client/GameBridge.cs) · [Reader-Test](../dotnet/KcdMp.Client.Tests/FriendlyFireReceiptIntegrationTests.cs) · [Native Schadensgrenze](../native/KCDMP/hits.cpp)

## Spiel-Freigabe

Das H6-Rig verlangt dieselbe Build-ID und gültige Join-Belege in beiden Rollenverteilungen, je 30 nahe NPC-Tode mit wartender und aktiver Aktion und 60 Sekunden Nachlauf. Manuelle Sicht- und PC-Belege bleiben erforderlich. `rp_owner_n>0`, `owner_violations=0` und `FRAME ours_us_p95<2000` sind Spielmessungen.

```bash
python3 tools/rig/scen_lane2_h6.py --build BUILD_ID \
  --a-host A_HOST_AGENT A_HOST_NATIVE A_HOST_KCD \
  --b-joiner B_JOIN_AGENT B_JOIN_NATIVE B_JOIN_KCD \
  --b-host B_HOST_AGENT B_HOST_NATIVE B_HOST_KCD \
  --a-joiner A_JOIN_AGENT A_JOIN_NATIVE A_JOIN_KCD
```

H20/H19, H4/H17 und H14 brauchen integrierte Gate-Belege vor dem erweiterten Kampftest. H22s erwartete Join-/Save-Fehler erlauben den CI-Integrationsbuild. Lasse prüft den gemeinsamen Kandidaten mit Rollentausch, Sichtbelegen und echten HP-/Ausdauer-Vorher-/Nachherwerten. NightCoop gibt NPC-Treffer an den zuständigen Engine-Owner. Seamless Co-op dokumentiert mehrere Friendly-Fire-Modi. KCT behält Friendly Fire standardmäßig an. [Zwei-PC-Prüfliste](ABNAHME-LANE2-NAHKAMPF.md) · [NightCoop SDK](https://nightcoop.net/sdk/docs/npc-effects.md) · [Seamless Co-op FAQ](https://ersc-docs.github.io/faq/)
