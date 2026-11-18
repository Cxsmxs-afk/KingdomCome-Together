# H0 Selbstkontrolle

Der Windows-Build vom Zwei-PC-Test hat 2.705 Client- und 12.488 native Tests bestanden. Im Spiel erreichten die Joiner-Logs trotzdem `clock_in=0` und `no current host calendar`. Die bisherigen Tests für Kalender, Queue und Relay prüften einzelne Abschnitte. H20 prüft den Empfang im Hauptmenü über den Reader bis zum Laden. [Build des getesteten Drop-ins](https://github.com/Cxsmxs-afk/KingdomCome-Together/actions/runs/44383488125)

Skyrim Together baut spielbare Windows-Pakete für mehrere Spielversionen und prüft separat die Architektur seiner Linux-Serverpakete. Hier prüft H0 zusätzlich das ausgelieferte KCD2-Paket und macht die noch fehlenden Ablaufprüfungen im CI-Bericht sichtbar. [Windows-Paketierung](https://github.com/tiltedphoques/TiltedEvolution/blob/dev/.github/workflows/windows-playable-build.yml#L13-L24) · [Serverprüfung](https://github.com/tiltedphoques/TiltedEvolution/blob/dev/.github/workflows/linux.yml#L73-L86)

## HostAuthority

Am 18.11.2026 um 20:46:58 Uhr (Berlin) meldete der Push-Build `Abandoned_native_probe_cannot_later_configure_a_role` als fehlgeschlagen. Der 50-ms-Timeout konnte auslösen, bevor der ThreadPool die native Anfrage begonnen und der Test seine Prüfaufgabe gesetzt hatte. Eine kontrollierte Verzögerung des Workers reproduziert den Nullzugriff im alten Test. Die beiden Tests warten jetzt auf die laufende native Anfrage, lösen den Timeout aus und geben anschließend deren Antwort frei. Sie prüfen weiter die abgebrochene Aufgabe und das Ausbleiben später Autoritätsänderungen. [Fehlgeschlagener Build](https://github.com/Cxsmxs-afk/KingdomCome-Together/actions/runs/44383139015/job/135080492732) · [ProcessorFrameScope](../dotnet/KcdMp.Client/ProcessorFrameScope.cs#L72) · [HostAuthorityTests](../dotnet/KcdMp.Client.Tests/HostAuthorityTests.cs#L89)

Der nächste Windows-Lauf zeigte drei weitere Tests mit einer Annahme über den Start des Workers. Ein kontrolliert verzögerter Start reproduzierte `TimedOut` beim regulären Abschluss, das ausbleibende Signal des späten HTTP-Schreibversuchs und die fehlende HTTP-Wiederaufnahme. Der Abschlusstest wartet jetzt mit kontrollierter Uhr auf den Worker. Die beiden HTTP-Tests lösen die Frist erst am belegten Wartepunkt aus. Der Wiederaufnahmetest prüft zusätzlich, dass der erste erfolgreiche Request eine harmlose Probe ist. [Fehlgeschlagener Build](https://github.com/Cxsmxs-afk/KingdomCome-Together/actions/runs/44413076859/job/135186087657) · [Testuhr](../dotnet/KcdMp.Client.Tests/ManualFrameTime.cs) · [HTTP-Tests](../dotnet/KcdMp.Client.Tests/HttpFrameCancellationTests.cs#L150)

## Pflichtprüfungen

| Paket | Nachweis |
| --- | --- |
| H20 | Der simulierte Host sendet den gefrorenen Kalender über das Relay. Der echte Reader des Joiners nimmt ihn bei `WorldSyncReady=false` an, danach erreicht der Joiner die gemeinsame Welt. Beide Rollenverteilungen und ein verspätetes oder fehlendes erstes Kalenderpaket laufen durch. |
| H19 | Fünf Abbrüche vor dem Laden finden mit markierten Join-Dateien und persönlichen Spielständen im Testverzeichnis statt. Danach belegen Join-Welten höchstens einen Platz, und die persönlichen Dateien stimmen bytegenau überein. |
| H21 | Das entpackte Drop-in enthält je einen lesbaren, als passend geprüften Spielstand für `soldier`, `adviser` und `scout`. |

H0 meldet die ausstehenden Prüfungen ausdrücklich als erwartete Fehler. H20, H19 und H21 schalten jeweils ihre Prüfung scharf. Der bisherige Paketcheck mit `install-verify.txt` bleibt ein Pflichtschritt.

Die Mindestzahlen des bisherigen Windows-Laufs sind Client 2.705, Relay 288, LogPack 285, Setup 110 und native Tests 12.488. Bei einer Änderung der Mindestzahlen nennt der Paketbericht die betroffene Suite und den Grund.

## Repo-Skills

H0 ergänzt die Projektkarte, den Fix-Ablauf, die Performance-Regeln, die Log-Analyse und den Koop-Vergleich. Ihre Einstiege stehen in [AGENTS.md](../AGENTS.md#skills). Die Projektkarte verweist auf die vorhandenen Architektur- und Entscheidungsdokumente.

Der Netzsimulator prüft im CI-Modus zuerst die Skill-Namen, Beschreibungen und Pfade. Bewusst fehlende Pfade brauchen einen Eintrag `<!-- skill-path-pending: PFAD -->` auf derselben Zeile wie die Referenz. Der Marker gilt nur für den genannten Pfad. [Prüfskript](../tools/Test-Skills.py)

## Nachtrag zu Draft #49

**Teilstatus:** Automatisierte H0-Prüfungen belegt, Spielabnahme offen. Der bislang freigegebene Windows-Volllauf bestätigt Client 2705/2705, Setup 110/110 und das Drop-in mit `install-verify.txt` auf `PASS`. Der Nachtrag zieht das Python-Gate im Simulator nach. Die Workflow-Datei entspricht `da41d16`. Die genehmigte Windows-Ausnahme betrifft H0. Die Übergangserwartungen für H19, H20 und H21 gelten nur in ihren vorgesehenen Schritten. [Windows-Lauf auf da41d16](https://github.com/Cxsmxs-afk/KingdomCome-Together/actions/runs/44415398350)

### Python-Pflichtsuiten

Im bisherigen `run-sim` genügte der Exitcode von `unittest discover`. Am 19.11.2026 gegen 00:23 Uhr (Berlin) lief der kontrollierte Test mit einem echten Pass und einem absichtlichen Skip durch. Der neue Runner wertet das Ergebnisobjekt pro Suite aus. Die Discovery-Ordner, Muster und Reihenfolge bleiben erhalten. [Bisherige Aufrufe](https://github.com/Cxsmxs-afk/KingdomCome-Together/blob/da41d16/tools/sim/run-sim#L67-L75) · [Ergebniskontrolle](../tools/sim/run-required-python.py#L10)

```bash
fixture="$(mktemp -d)"
cat > "$fixture/test_skip.py" <<'PY'
import unittest

class Proof(unittest.TestCase):
    def test_pass(self):
        self.assertEqual(1, 1)

    @unittest.skip("absichtlicher Skip")
    def test_skip(self):
        self.fail("muss übersprungen werden")
PY
python3 -B -m unittest discover -s "$fixture" -p test_skip.py
printf 'Exit vorher: %s\n' "$?"
python3 -B tools/sim/run-required-python.py discover -s "$fixture" -p test_skip.py
printf 'Exit nachher: %s\n' "$?"
rm -rf "$fixture"
```

Vorher `Ran 2 tests`, `OK (skipped=1)`, Exit **0**. Nachher `Ran 2 tests`, `FAILED (skipped=1)`, Exit **1**. Auch der echte Einstieg `tools/sim/run-sim ci` brach im isolierten Testbaum mit der übersprungenen Pflichtprüfung bei Exit **1** ab. Eine leere Discovery wird explizit abgelehnt. Mit Python 3.12.14 wechselte der Exit bei null Tests von **5** auf **1**. Der vollständige Simulatorlauf auf `44a8de6` bestand mit **6 / 9 / 8 / 8 / 18 / 7** Python-Tests, **353** portablen nativen Prüfungen und **6/6** gewerteten Netzwerkläufen. [Simulator-Einstieg](../tools/sim/run-sim#L67)

### Kontrollierte HostAuthority-Reproduktion

Der Reproduktionsaufruf nutzt separat eingefrorene T5-Binärdateien mit Quellprüfsummen aus den zugehörigen PDBs. Er blockiert den ersten Worker per gewechseltem Ausführungskontext. Die reale Frist läuft weiter. Die aufgezeichneten Aufrufe und der Kern der roten Ausgabe lauten wie folgt.

```text
/tmp/kcd-dotnet/dotnet /tmp/kcd-h0-pdb-evidence/host-red/HostAuthorityRed.dll
exit: 1
65 ms: Xunit.Sdk.ThrowsAnyException: Assert.ThrowsAny() Failure: Exception type was not compatible
Expected: typeof(System.OperationCanceledException)
Actual:   typeof(System.NullReferenceException)
HostAuthorityTests.Abandoned_native_probe_cannot_later_configure_a_role():line 103

/tmp/kcd-dotnet/dotnet /tmp/kcd-h0-pdb-evidence/host-red/HostAuthorityRed.dll configure
exit: 1
76 ms: Xunit.Sdk.ThrowsAnyException: Assert.ThrowsAny() Failure: Exception type was not compatible
Expected: typeof(System.OperationCanceledException)
Actual:   typeof(System.NullReferenceException)
HostAuthorityTests.Abandoned_native_configuration_cannot_report_late_success():line 122
```

Der ursprüngliche fokussierte Lauf mit `--filter 'FullyQualifiedName~HostAuthority|FullyQualifiedName~ProcessorFrameScope'` meldete `Total tests: 34`, `Passed: 34`. Die Wiederholung auf `da41d16` mit zusätzlichem `FullyQualifiedName~HttpFrameCancellation` meldete `Total tests: 46`, `Passed: 46`. Beide liefen mit `dotnet test dotnet/KcdMp.Client.Tests/KcdMp.Client.Tests.csproj -c Release --no-restore` und TRX-Logger. Die Wiederholung auf `da41d16` benutzte zusätzlich `--no-build` für die zuvor geprüfte Assembly.

Ein älterer Scratch-Lauf für den ProcessorFrameScope-Abschluss verwendete durch den Assembly-Cache erneut T5-Binärdateien. Die neue Wiederholung mit geprüftem H0-Build bestätigt unabhängig `Expected: Completed`, `Actual: TimedOut` an Zeile 43. [HostAuthority-Test auf T5](https://github.com/Cxsmxs-afk/KingdomCome-Together/blob/a62ba9ba/dotnet/KcdMp.Client.Tests/HostAuthorityTests.cs#L89) · [Testuhr](../dotnet/KcdMp.Client.Tests/ManualFrameTime.cs)

### Linux-Vergleich nach Testnamen

Aus den TRX-Dateien wurden alle `testName` mit `outcome="Failed"` gezählt und sortiert. Der SHA-256-Wert verwendet die UTF-8-Namen mit Zeilenumbrüchen zwischen den Namen. Geprüft wurde zusätzlich die Differenz der Namensmultimengen.

| Suite | Ausgeführt / bestanden / fehlgeschlagen | Basis `a62ba9ba` und H0 `da41d16` nach Namen |
| --- | --- | --- |
| Client | 2705 / 2693 / 12 | 0 hinzugekommen, 0 entfallen. SHA-256 `92e29374bded4c6c5f3630162597d87584fd59556301458ec180490d16f1ed26` |
| Setup | 110 / 49 / 61 | 0 hinzugekommen, 0 entfallen. SHA-256 `95cd22da8a2d404b7071462b0cd46a1bcc17fa23e969d9655de0d7a7b668ea06` |

```bash
python3 -B /tmp/kcd-h0-addendum/compare-trx.py /tmp/kcd-t5-client-full/client.trx /tmp/kcd-h0-timing-latch/client.trx
python3 -B /tmp/kcd-h0-addendum/compare-trx.py /tmp/kcd-h0-local/Setup.trx /tmp/kcd-h0-addendum/Setup-da41d16.trx
```

Client prüft unter anderem Windows-Registry, IP Helper, Windows-Pfade und die Größe eines komprimierten Transfer-Fixtures. Setup verwendet unter anderem Win32-Systemprogramme, NTFS und Laufwerkspfade. Der unveränderte Windows-Runtime-Kontext im CI bestätigt alle Client- und Setup-Prüfungen. [Client-Beispiele](../dotnet/KcdMp.Client.Tests/Wo154RelayTests.cs#L81) · [Setup-Beispiele](../dotnet/KcdMp.Setup.Tests/WorkspaceTests.cs#L125)

### Selbst-Review, Performance und Owner

| Prüfpunkt | Befund |
| --- | --- |
| Rückgaben, Fehler, Löschliste | Der Standard-Discovery-Aufruf ist in allen sechs Pflichten durch die geprüfte Variante ersetzt. Skips und leere Suiten führen zu Exit 1 und sind im unittest-Protokoll sichtbar. Reguläre Fehler einschließlich Discovery-Fehlern bleiben Fehler. |
| Races und Fristen | Die HostAuthority- und HTTP-Tests erreichen ihren Wartepunkt vor dem gesteuerten Timeout. Die Blocker werden beim Aufräumen freigegeben. Die normale Produktionsfrist verwendet weiterhin `TimeProvider.System` und dieselbe Reserve. |
| Abbruch und Aufräumen | Der Simulator räumt seine temporären nativen Dateien per `trap` auf. H19 benutzt einen isolierten Testordner und einen persönlichen Spielstand als Bytevergleich. Produktions-Slot-Freigabe ist der offene H19-Nachweis. |
| Rollen, Reconnect und 2/3/4 Spieler | Der H0-Diff lässt Setup, Rollenwahl, Transportpakete und Epochen unverändert. Die Simulatorprofile decken 2, 3 und 4 Teilnehmer ab. Rollenwechsel im echten Launcher und im Spiel bleibt H22. |
| Single-Owner und Spielregeln | Die enginefreien Halterprüfungen decken korrekte Schreiber und die Erkennung von Verletzungen ab. H0 fügt keinen Schreiber, Schadenspfad oder Spielregeln hinzu. Der Live-Nachweis verlangt in beiden Rollen `owner_violations=0` bei `rp_owner_n>0`. |
| Daten und Skill-Pfade | Die Vergleichsprotokolle enthalten Testnamen und die Simulatorprotokolle Modellwerte. Die hinzugefügten Quellen und Beispiele sind frei von Zugangsdaten und realen Steam-IDs. Die fünf Skill-Dateien und ihre Pfade werden geprüft. Die Änderungen an Referenzen stehen unten. |
| Performance | Die Python-Prüfung läuft einmal je Suite vor dem Simulator. Der Ingame-Main-Thread, Frame-/Paketpfade, Senderaten und Byteformate werden durch den Nachtrag nicht berührt. `da41d16` und `44a8de6` erreichten lokal jeweils 6/6 und höchstens 200 ms in den gewerteten Simulatorprofilen. Reale Messwerte bleiben für `FRAME`, `MAIN-COST`, `MP-W207-Q` und `MP-LINK` offen. |

### Abweichungen vom Skill-ZIP

| Skill | Abweichung und Grund |
| --- | --- |
| `kct-kontext` | Direct-Test, abgeleitete Launcher-Rolle und Reihenfolge im Kalender-Reader sind an den tatsächlichen Aufrufen beschrieben. Die Live-Ursache des Kalenderverlusts ist offen. Vorhandene Architekturdateien ersetzen den toten Verweis. Die kontrollierte Testuhr ergänzt die Fehlerkarte. |
| `kct-fix-workflow` | 85 Synthetic-Suiten, TRX-/Native-Gates, H0-Umfang der Windows-Ausnahme sowie das streng geprüfte Python-Gate sind am Build-Einstieg ausgerichtet. Der Spieltest verlangt dieselbe Join-ID und positive Owner-Abdeckung. |
| `kct-performance` | Zeitbudgets, Paketintervalle, Heartbeat-Bedingung und Bedeutung der Frame-Maxima sind präzisiert. Die offizielle NightCoop-SDK-Seite belegt rund 30 Hz für eine SDK-Pose. Der Link und die Zeilen der 50-ms-Schwelle sind korrigiert. |
| `kct-log-analyse` | Manifest- und Archivteile, Zeitformate und `anim-compare`-Schreibverhalten sind an LogPack angepasst. Versand, Empfang, Cache und Anwendung der Uhr sind getrennte Belege. |
| `kct-coop-vergleich` | Primärquellen und gekennzeichnete Community-Quellen ersetzen unklare Links. Aussagen ohne Nachweis zu Uhr/Loot wurden gestrichen. KCD2-Paketziele und gemessene Verhaltenspfade sind im Vergleich kenntlich. |

Die Aufnahme umfasst die fünf Repo-Skills aus dem Zusatz. `kct-prompt-review` ist dort für die Arbeit im Projektchat vorgesehen.

**Offene Nachweise:** H20 braucht den vollständigen Verhaltenstest über den echten Reader bei `WorldSyncReady=false` bis zum Laden, in beiden Rollenverteilungen und mit verspätetem oder fehlendem erstem Kalenderpaket. H19 braucht den grünen strengen Slot-Test. H21 braucht die gelieferten und im Archiv validierten Originalspielstände. H22 braucht den ausgeführten Rollentausch auf derselben Konfiguration. Zwei-PC-Abnahme, echte Owner-Abdeckung, Live-Save-Roundtrip und Frame-Rate-Soak sind offen.

## Löschliste

Die Annahmen über den Worker-Start innerhalb der realen Frist sind in den betroffenen Tests durch Wartepunkte und eine kontrollierte Testuhr ersetzt. Die bisherigen Aufrufe der vier .NET-Suiten, die nur den Exitcode prüften, sind durch die Auswertung ihrer TRX-Ergebnisse ersetzt. Der Paketcheck steht in `tools/Verify-DropIn.ps1` und wird vom Workflow aufgerufen.

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
