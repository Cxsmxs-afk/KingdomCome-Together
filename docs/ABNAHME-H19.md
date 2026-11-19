# H19 · Join-Plätze nach einem Abbruch freigeben

Im Auswertungsbericht vom 18. November 2026 meldete der Joiner um 22:28:33 Uhr (Berlin) `clock-unavailable: kept playline3/…` und nach der nächsten Anfrage denselben Befund für Playline 4. Um 22:29:45 Uhr meldete das Menü fünf belegte Plätze. Der alte Abbruchpfad rief die Bereinigung auf, ohne den eigenen Dateilease freizugeben. Der automatische Folgeversuch wählte anschließend den nächsten freien Platz. [Abbruch auf H20](https://github.com/Cxsmxs-afk/KingdomCome-Together/blob/18d3dfd/dotnet/KcdMp.Client/GameBridge.Wo124.cs#L1131) · [Platzwahl](../dotnet/KcdMp.Client/Wo175Coop.cs)

Der Join hat genau eine Reservierung für die vorübergehende Welt. Der Agent belegt sie mit Host- und Weltkennung, Join-ID, Pfad und SHA-256. Das Journal wird vor der Veröffentlichung der Datei geschrieben. Der Abbruch beendet die Platzierung und gibt danach den eigenen Lease frei. Vor einem möglichen Ladebefehl markiert der Agent die Datei für den Engine-Zugriff. Bei einer ausstehenden Ladeanforderung wartet die Bereinigung auf einen frischen Spielnachweis. Die Startbereinigung prüft nur positiv zugeordnete Join-Dateien, deren Ladebefehl nie abgeschickt wurde. [Journal](../dotnet/KcdMp.Client/JoinWorldJournal.cs) · [Platzierung und Abbruch](../dotnet/KcdMp.Client/GameBridge.Wo124.cs) · [Wartepfad](../dotnet/KcdMp.Client/GameBridge.Wo154Join.cs)

Ein Folgeversuch aus demselben Menü und mit derselben Verbindung, Welt und Charakterwahl braucht eine ausdrückliche neue Anfrage. Eine neue Welt-Epoche gibt die automatische Wahl wieder frei. NightCoop trennt Heimkampagne und besuchte Host-Welt. Seamless Co-op verwendet einen eigenen Spielstand-Namensraum. Hier gehören temporäre Join-Dateien dem jeweiligen Versuch und werden anhand dessen Belegs aufgeräumt. [NightCoop SDK](https://nightcoop.net/sdk/docs/concepts.md) · [Seamless Co-op FAQ](https://ersc-docs.github.io/faq/)

## Nachweis

```powershell
pwsh -NoProfile -File tools/Test-JoinReadiness.ps1 -Check Abort
```

Der Ausgangsstand auf H20 meldete bei fünf echten Aufrufen des Abbruchpfads `retained_slots=5 own_save_unchanged=1`. Der strenge Aufruf war rot. Der ergänzte Relay-Test brach vor dem Fix nach einer bestätigten Platzierung und dem absichtlich verweigerten Kalender-Latch ab. Der Test fand die liegen gebliebene Datei.

Die Korrektur erreicht `JOIN-ABORT-LIFECYCLE playline_reused=1 submitted_preserved=1` und `JOIN-ABORT-GATE attempts=5 retained_slots=0 own_save_unchanged=1`. Die Probe verwendete für alle fünf Versuche denselben Platz. Der getrennte Fall mit freigegebenem Ladeauftrag hielt die Datei geschützt. [Simulatorprobe](../tools/sim/KcdMp.Sim/JoinAbortRegression.cs) · [Relay-Test](../dotnet/KcdMp.Client.Tests/JoinCalendarIntegrationTests.Abort.cs)

## Selbst-Review und Spielabnahme

- Der Abbruch setzt den Versuch unter demselben Lock außer Kraft, der Platzierung und Ladefreigabe schützt. Die Platzierung wird nach dem Schreiben erneut geprüft. Eine laufende Ladeanforderung sperrt das Löschen bis zum Spielnachweis.
- Das Journal prüft vorhandene Identität und Hash vor jeder Löschung. Geänderte oder unmarkierte Altdateien bleiben erhalten. Das gilt auch für Dateien, bei denen das Spiel noch lesen könnte. Der alte Namensfilter räumt keine `mpworld`-Dateien mehr weg.
- Hashing, Journal und Löschung fallen beim Join oder beim Start an. Bei einer erlaubten Wiederholung des SaveList-Aufrufs bleibt dieselbe temporäre Datei reserviert. Laufende Frame- und Senderaten ändern sich durch H19 nicht.
- Im Zwei-PC-Test fünf Abbrüche vor dem Ladebefehl mit manueller Wiederholung prüfen. Dieselbe Playline muss wieder frei werden. Die Agent-Zeile `deleted=on` muss zur abgebrochenen Join-ID gehören. Bei einem Abbruch während des Ladens bleibt die Datei bis zum Ende des Engine-Zugriffs sichtbar. Auf beiden PCs stehen die Messwerte für `rp_owner_n>0` und `owner_violations=0` noch aus.
