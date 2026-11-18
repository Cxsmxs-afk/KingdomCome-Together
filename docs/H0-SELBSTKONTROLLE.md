# H0 Selbstkontrolle

Der Windows-Build vom Zwei-PC-Test hat 2.705 Client- und 12.488 native Tests bestanden. Im Spiel erreichten die Joiner-Logs trotzdem `clock_in=0` und `no current host calendar`. Die bisherigen Tests für Kalender, Queue und Relay prüften einzelne Abschnitte. H20 prüft den Empfang im Hauptmenü über den Reader bis zum Laden. [Build des getesteten Drop-ins](https://github.com/Cxsmxs-afk/KingdomCome-Together/actions/runs/44383488125)

Skyrim Together baut spielbare Windows-Pakete für mehrere Spielversionen und prüft separat die Architektur seiner Linux-Serverpakete. Hier prüft H0 zusätzlich das ausgelieferte KCD2-Paket und macht die noch fehlenden Ablaufprüfungen im CI-Bericht sichtbar. [Windows-Paketierung](https://github.com/tiltedphoques/TiltedEvolution/blob/dev/.github/workflows/windows-playable-build.yml#L13-L24) · [Serverprüfung](https://github.com/tiltedphoques/TiltedEvolution/blob/dev/.github/workflows/linux.yml#L73-L86)

## HostAuthority

Am 18.11.2026 um 20:46:58 Uhr (Berlin) meldete der Push-Build `Abandoned_native_probe_cannot_later_configure_a_role` als fehlgeschlagen. Der 50-ms-Timeout konnte auslösen, bevor der ThreadPool die native Anfrage begonnen und der Test seine Prüfaufgabe gesetzt hatte. Eine kontrollierte Verzögerung des Workers reproduziert den Nullzugriff im alten Test. Die beiden Tests warten jetzt auf die laufende native Anfrage, lösen den Timeout aus und geben anschließend deren Antwort frei. Sie prüfen weiter die abgebrochene Aufgabe und das Ausbleiben später Autoritätsänderungen. [Fehlgeschlagener Build](https://github.com/Cxsmxs-afk/KingdomCome-Together/actions/runs/44383139015/job/135080492732) · [ProcessorFrameScope](../dotnet/KcdMp.Client/ProcessorFrameScope.cs#L72) · [HostAuthorityTests](../dotnet/KcdMp.Client.Tests/HostAuthorityTests.cs#L89)

## Pflichtprüfungen

| Paket | Nachweis |
| --- | --- |
| H20 | Der simulierte Host sendet den gefrorenen Kalender über das Relay. Der echte Reader des Joiners nimmt ihn bei `WorldSyncReady=false` an, danach erreicht der Joiner die gemeinsame Welt. Beide Rollenverteilungen und ein verspätetes oder fehlendes erstes Kalenderpaket laufen durch. |
| H19 | Fünf Abbrüche vor dem Laden finden mit markierten Join-Dateien und persönlichen Spielständen im Testverzeichnis statt. Danach belegen Join-Welten höchstens einen Platz, und die persönlichen Dateien stimmen bytegenau überein. |
| H21 | Das entpackte Drop-in enthält je einen lesbaren, als passend geprüften Spielstand für `soldier`, `adviser` und `scout`. |

H0 meldet die ausstehenden Prüfungen ausdrücklich als erwartete Fehler. H20, H19 und H21 schalten jeweils ihre Prüfung scharf. Der bisherige Paketcheck mit `install-verify.txt` bleibt ein Pflichtschritt.

Die Mindestzahlen des bisherigen Windows-Laufs sind Client 2.705, Relay 288, LogPack 285, Setup 110 und native Tests 12.488. Bei einer Änderung der Mindestzahlen nennt der Paketbericht die betroffene Suite und den Grund.

## Löschliste

Die HostAuthority-Tests verwenden keine 50-ms-Wartezeit mehr als Nachweis, dass der Worker begonnen hat. Die bisherigen Aufrufe der vier .NET-Suiten, die nur den Exitcode prüften, sind durch die Auswertung ihrer TRX-Ergebnisse ersetzt. Der Paketcheck steht in `tools/Verify-DropIn.ps1` und wird vom Workflow aufgerufen.

## Zwei Instanzen und Ampel

Das Rig sammelt im lokalen Zwei-Instanzen-Szenario frische Logs von A als Host und B als Joiner. Es führt die Paar-Auswertung aus.

```powershell
python tools/rig/scenarios.py verdict
```

Für die spätere Abnahme auf zwei PCs aus demselben Drop-in zwei Durchläufe machen. Zuerst hostet A und B tritt bei. Danach hostet B und A tritt bei. Für jeden Durchlauf mit **Collect Logs** ein frisches Bundle pro PC sammeln und in der Reihenfolge Host, Joiner auswerten.

```powershell
python tools/verdict/pair.py <Host-Bundle> <Joiner-Bundle>
```

In beiden Einzelberichten muss `owner_violations` grün sein. Die Logs müssen pro Rolle ausdrücklich `owner_violations=0` und `rp_owner_n>0` belegen. Für einen fehlenden Messwert zeigt die Ampel grau. Den Join über dieselbe Join-ID prüfen. Beim Joiner zählen `clock_in>0`, `pre-update latch armed before load` und `ready sent for join`. Beim Host zählt `resume join=… reason=ready`. [Rig und Paar-Ampel](T1-VERDICT.md)
