# H20 · Host-Kalender vor dem Laden

Der Auswertungsbericht zum Zwei-PC-Test vom 18. November 2026 protokolliert beim Join um 22:28 Uhr (Berlin) den Eingang von `w164:18`, `clock_in=0` und um 22:28:33 den Abbruch `clock-unavailable`. Der Host hatte die Welt bereits übertragen. Die historische Auswertung belegt den Ablauf bis zum Abbruch. Der kontrollierte Test ordnet den konkreten Reader-Pfad zu.

Der Host sendet den eingefrorenen Kalender vor dem Weltangebot. Der bisherige Reader klassifizierte ihn als `LoadScoped` und prüfte die Ladegeneration vor dem Kalender-Cache. Im Hauptmenü liefert `WorldLoadSequence.IsCurrent` immer `false`. Diese Ablehnung erhöht `_loadScopeDropped`. Der historische Wert `stale_gen_dropped=0` ist damit vereinbar. [Sender auf der Basis](https://github.com/Cxsmxs-afk/KingdomCome-Together/blob/18d3dfd/dotnet/KcdMp.Client/GameBridge.Wo123.cs#L516) · [Reader auf der Basis](https://github.com/Cxsmxs-afk/KingdomCome-Together/blob/18d3dfd/dotnet/KcdMp.Client/GameBridge.cs#L4872) · [Phasenprüfung](../dotnet/KcdMp.Client/WorldLoadSequence.cs)

Die eingefrorene Uhr läuft als Join-Nachricht vor dem Gameplay-Gate. Der Empfänger bindet sie an den Relay-Absender, die Verbindung, Welt-Epoche und Join-ID. Nach Annahme quittiert er diese Kombination. Bis zur Quittung wiederholt der Host denselben eingefrorenen Wert. Die Quittung beendet die Wiederholung. Der Ende-zu-Ende-Join behält den separaten Ready-Schritt, der die Weltpause freigibt. Der Joiner kann vor dem Laden auf die Uhr warten, solange der Host den Versuch offen hält. [Kalender-Reader](../dotnet/KcdMp.Client/GameBridge.HostCalendar.cs) · [Wiederholung](../dotnet/KcdMp.Client/GameBridge.JoinCalendarHost.cs)

NightCoop unterscheidet zwischen einem laufenden Spiel und der geladenen Host-Welt. H20 verwendet dieselbe Trennung für den Start des Beitritts. Der Bootstrap ist im Menü verfügbar. Die laufende Welt-Uhr bleibt an die Ladephase gebunden. [NightCoop SDK](https://nightcoop.net/sdk/docs/concepts.md)

## Nachweis

```bash
dotnet test dotnet/KcdMp.Client.Tests/KcdMp.Client.Tests.csproj -c Release \
  --filter 'FullyQualifiedName~JoinCalendarIntegrationTests'
```

Auf `18d3dfd` wurden beide Rollentausche mit vollständiger Übertragung, verifizierter Platzierung und SaveList-Bestätigung geprüft. In allen vier Fällen brach der Beitritt mit `no current host calendar -- load not issued` und `ABORTED (clock-unavailable)` ab. Auch mit garantiert unversehrtem erstem Snapshot blieb `clock_in=0`.

Nach der Korrektur bestanden alle vier Fälle. In beiden Rollenverteilungen akzeptierte der Joiner die unversehrte erste Lieferung vor dem Angebot im Hauptmenü bei `WorldSyncReady=false`. Bei Verlust der ersten Lieferung kam der unveränderte Snapshot erneut an. Die Quittung erreichte den Host. Der native Latch erhielt vor dem Laden im Fixture `572161000` ms bei Rate null und lieferte nach dem Laden denselben Wert. `JoinerReady` traf mit der übertragenen Sequenz ein und der Host meldete `reason=ready`. Das Fixture verwendet echtes TCP-Relay, die produktiven Agent-Reader und simulierte Antworten an den Grenzen zu Spiel und DLL. [Ende-zu-Ende-Test](../dotnet/KcdMp.Client.Tests/JoinCalendarIntegrationTests.cs) · [CI-Einstieg](../tools/Test-JoinReadiness.ps1)

Ein fünfter Test lässt während der echten Kalender-Wartephase sechs weitere Sekunden lang nur die Kalenderpakete ausfallen. Der Host blieb in `waiting-ready` und hielt die Welt. Die erste danach zugelassene Wiederholung führte zum erfolgreichen Laden, zur identischen Latch-Rückgabe und zu `reason=ready` nach rund 9,6 Sekunden Weltpause. Alle fünf Tests liefen ohne Skip.

## Abnahme auf zwei PCs

1. Dasselbe Drop-in auf beiden PCs verwenden. A hostet und B tritt im Hauptmenü mit **Bring my character** bei. Danach die Rollen tauschen.
2. Für dieselbe Join-ID müssen Host- und Gast-Logs `snapshot=sent`, `bootstrap accepted`, `receipt=accepted`, `pre-update latch armed before load`, den Latch-Readback nach dem Laden und `reason=ready` zeigen. `clock_in` ist beim Gast mindestens 1. Bei verlorener Zustellung erscheint auch `snapshot=repeated`.
3. Für den Owner-Nachweis beide frischen Mitschnitte mit `rp_owner_n>0` und `owner_violations=0` prüfen. Das Verhalten im Spiel und die Main-Thread-Kosten werden anhand von `FRAME`, `MAIN-COST`, `MP-W207-Q` und `MP-LINK` abgenommen.

### Station Rejoin-Schleife

Beide PCs verwenden dieselbe Build-ID. Nach dem Rollentausch dieselben Schritte wiederholen. Für jeden Versuch Uhrzeit, Join-ID, Build-ID und Ergebnis festhalten.

1. Aus dem eigenen Spiel im Koop-Menü **Join / Rejoin your host** wählen. Beim Laden **Cancel join** wählen. Nach bestätigter Rückkehr zum eigenen Spielstand erneut beitreten. Fünf Durchgänge.
2. In der gemeinsamen Welt **Leave** wählen. Die eigene Welt muss vollständig laden und der nächste **Join / Rejoin your host** muss funktionieren. Fünf Durchgänge.
3. Den Host im laufenden Spiel dreimal vom Relay trennen und mit demselben Weltlauf zurückkehren lassen. Der Gast bleibt pausiert, bis der Host nachweislich zurück ist. Den Sonderfall mit neu gestartetem Host separat festhalten. Er braucht einen bestätigten neuen Hostlauf und eine erneute Aktion des Gasts.
4. Vor und nach der Schleife Threads, Handles, aktive Pipe-Verbindungen, Timer und Spielstand-Leases zählen. Temporäre `mpworld*`- und `mpexit*`-Dateien kontrollieren und den persönlichen Save per SHA-256 vergleichen. `ours_us_p95` muss auf beiden PCs unter 2.000 µs bleiben. Der Messwert darf sich um höchstens 5 % verschlechtern.

Spielabnahme und Messwerte sind offen.

## Gegenproben zum Scope

Auf dem lokalen Vorgänger `67cb67b` konnte ein verspätetes Paket vom bestätigten Host mit derselben Join-ID den Latch um 1.000 ms verfälschen. Der Reader prüfte die beim Lesen gelernte Welt-Epoche. Das Paket selbst enthielt nur die Join-ID. In einer separaten Probe vor dem Angebot blieb nach einem echten Relay-Disconnect die ausstehende Join-ID ungleich null. Ein nicht bestätigter Engine-Hold lieferte auf derselben Basis trotzdem ein Angebot, das der Gast erfolgreich lud. Der Progress-Test beobachtete außerdem, dass das WorldSync-Gate die Meldungen während des Host-Holds vollständig verwarf. [Kalender-Reader](../dotnet/KcdMp.Client/GameBridge.HostCalendar.cs) · [Relay-Proben](../dotnet/KcdMp.Client.Tests/JoinCalendarIntegrationTests.cs)

Der eingefrorene `W164Clock` führt die Join-ID im Umschlag und die ursprüngliche Welt-Epoche im Token. Der Reader bindet Angebot, Cache und Quittung an den bei der Anfrage bestätigten Host und dieselbe Relay-Verbindung. Ein alter ungebundener Snapshot wird abgelehnt. Der Host muss den Engine-Hold und die tatsächlich stehende Uhr belegen. Fortschritt ist eine Join-Steuermeldung. Er kann den Hold erst verlängern, wenn der genaue Gast den Kalender für diesen Versuch quittiert hat. Gelesener Host-Abbruch und Host-Verlust sperren die Ladefreigabe. Der Gast bestätigt die native Offer-Generation vor dem Setzen des Latch und prüft die Pipe-Revision erneut beim Dispatch. [Sendeseite](../dotnet/KcdMp.Client/GameBridge.JoinCalendarHost.cs) · [Ladefreigabe](../dotnet/KcdMp.Client/GameBridge.Wo124.cs) · [Fortschritt](../dotnet/KcdMp.Client/GameBridge.Wo204.cs)

Die fünf zusätzlichen Pflichtfälle prüfen alte oder doppelte Kalenderpakete, Host-Verlust vor Angebot und nach Transfer, einen nicht bestätigten Engine-Hold und Fortschrittsmeldungen vor Kalenderquittung. Die Paketprobe speist veränderte und duplizierte Kopien eines echten Relay-Frames unmittelbar vor dem produktiven Reader ein. Die Verlustprobe beendet die echte TCP-Senderichtung des Hosts. Spielkonsole und Engine-Log sowie die Antworten der DLL-Pipe sind Testgrenzen.

```powershell
pwsh -NoProfile -File tools/Test-JoinReadiness.ps1 -Check EndToEnd
```

Das Selbst-Review hat den bisherigen vierparametrigen Aufruf für private Ladepfade erhalten. Der bestehende Test für ein zeitversetzt verarbeitetes Angebot eröffnet den Versuch jetzt über den produktiven Anforderungspfad. Er prüft weiterhin, dass die Verdrängung durch eine neuere Welt-Epoche die Herkunft des Angebots unverändert lässt. Join-Freigabe und Host-Verlust verwenden denselben Zustandslock. Alte Welt- und Join-Nachrichten unterscheiden sich in den Meldungen `different-world` und `inactive-attempt`. Die zusätzliche native Offer-Bestätigung liegt einmalig im Ladepfad. Ihre Zeit und die Zeit bis zum echten Latch-Readback sind auf beiden PCs zu messen. Offen bleiben grüner Windows-Volllauf, exakter Build im Drop-in und die Abnahme mit derselben Join-ID auf beiden PCs.

## Abbruch an der tatsächlichen Ladeübergabe und vor Ready

Auf `df301015` lag die frühere Freigabe in `AuthorizeJoinCalendarLoad` vor `LoadSequenceBeginLoad` und der anschließenden HTTP-Übergabe in `P5LoadGameCoreAsync`. Der Reader konnte in diesem Fenster den Host-Abbruch bereits verarbeitet haben. Die alte Probe beobachtete in vier Fällen jeweils einen tatsächlichen Aufruf der Spielkonsole. Auch ein bereits im LinkWriter wartendes Ready konnte in vier Fällen nach dem gelesenen Abbruch noch auf der Verbindung erscheinen. Die Gegenprobe hält den echten Reader aktiv und wartet auf den Eintritt der Barriere im Produktionspfad. Host-Abbruch wird authentifiziert über das Relay zugestellt. Host-Verlust schließt tatsächlich die TCP-Senderichtung des Hosts. [Ladeaufruf](../dotnet/KcdMp.Client/GameBridge.DeathMarkers.cs) · [Reader](../dotnet/KcdMp.Client/GameBridge.cs) · [Tests](../dotnet/KcdMp.Client.Tests/JoinCalendarIntegrationTests.cs)

Der Join-Versuch gibt den Transportaufruf erst am tatsächlichen HTTP- bzw. Relay-Handoff unter derselben kurzen Sperre frei, unter der der Reader den Versuch beendet. Die Sperre umfasst den synchronen Start des Aufrufs. Auf die Antwort oder den Abschluss des Sendens wartet der Agent außerhalb. Das Ready trägt Join-ID, übertragenen Save-Stand und die gebundene Verbindung. Der UI-Zustand `in` setzt ein tatsächlich geschriebenes und weiterhin gültiges Ready voraus. Eine allgemeine Lua-Ready-Meldung kann ihn nicht mehr auslösen. [HTTP-Übergabe](../dotnet/KcdMp.Client/HttpGameTransport.cs) · [Relay-Übergabe](../dotnet/KcdMp.Client/GameBridge.Wo123.cs) · [LinkWriter](../dotnet/KcdMp.Protocol/LinkScheduler.cs)

Die Test-Doubles steuern die HTTP-Konsole des Spiels, die Engine-Logsignale und die DLL-Pipe. Die 25 H20-Fälle unterscheiden unter anderem einen Abbruch vor der HTTP-Übergabe, einen Abbruch mit einem bereits wartenden Ready und einen Abbruch bei laufendem Load. Beim späten Engine-Lesen bleibt die Datei bis zur Abschlussmeldung für genau diesen Pfad und diese Engine-Instanz verfügbar. Bei getrenntem Gast-Relay wartet die Rückkehr zum eigenen Spielstand separat auf dessen eigene Engine-Meldung. Die Proben enthalten Doppel-Abbruch, einen frischen Beitritt und beide Rollen. Zusätzliche TCP-Peers prüfen den gezielten Join mit drei und vier Teilnehmern. [Abbruchsuite](../dotnet/KcdMp.Client.Tests/JoinCalendarIntegrationTests.cs) · [Pflicht-Gate](../tools/Test-JoinReadiness.ps1)

## Löschliste und Leistungsbudget

Das Cache-Schreiben des Join-Snapshots im Gameplay-Pfad und die lokale Fünf-Sekunden-Abbruchfrist entfallen. Auch das Löschen von Join-Dateien nur nach Dateinamen beim Start, die Dateifreigabe allein nach einer Frist und die allgemeine Lua-Meldung als Ready-Freigabe entfallen. Die atomare Dateibesitzprüfung gehört zu H19. Der Host hält pro laufendem Versuch einen eingefrorenen Snapshot. Solange dessen Bestätigung aussteht, sendet er höchstens zwei Wiederholungen pro Sekunde an genau diesen Gast. Der Reader führt dafür keine Pipe- oder HTTP-Arbeit aus. Die Autorität für die Uhr bleibt bei der Host-Welt. Die neuen Prüfungen liegen an Lade- und Sendeschnittstellen. Im Frame entsteht kein neues Datei-I/O und kein Polling. Join-Dauer und `ours_us_p95 < 2000` bleiben auf zwei PCs zu messen.
