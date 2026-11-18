# Solo-Abend auf dem Windows-Testrechner

Der Lauf ist für Lasses Schwester mit 32 GiB installiertem RAM und einer RTX 4080 SUPER vorbereitet. Er verwendet die KCD2 Modding Tools, zwei getrennte Testkopien und ein lokales Relay. Die Werkzeuge wurden synthetisch geprüft. Die Ausführung im Spiel steht aus.

## Vorbereiten

- Eine Windows-PowerShell öffnen und in den Checkout mit `tools/rig/Solo-Abend.ps1` wechseln. Python ab 3.10 und das .NET-8-SDK müssen verfügbar sein.
- Zwei verschiedene lokale `.whs`-Wegwerfstände auswählen. Die Host-Kopie braucht eine Stelle für die Laufstrecken und die interaktiven Proben. Die Dateien bleiben lokal. Der Versuch schreibt in die eigenen Benutzerordner der Testkopien.
- Ein Rig-Build mit zusammengehörigem Agent, Relay, DLL und Pak bereitstellen. `-BuildDirectory` zeigt auf den unten beschriebenen Ordner. `-ModTemplate` zeigt auf das Verzeichnis `Mods\kdcmp` aus demselben Drop-in. `-Injector` zeigt auf den separat gebauten Rig-Injector.
- Wenn die Standardpfade für die Spielkopien auf Laufwerk D ungeeignet sind, `KCDMP_RIG_ROOT_A` und `KCDMP_RIG_ROOT_B` auf zwei eigene Ordner auf demselben NTFS-Datenträger wie die Installation setzen. Die Spielkopien brauchen getrennte Verzeichnisse außerhalb der Installation.

Der Rig-Build enthält `agent\KcdMpClient.exe` und `relay\KcdMpServer.exe` mit ihren Abhängigkeiten, `dll\KCDMP_w206_dropin.dll`, `mod\kdcmp.pak` und `logpack\KcdMp.LogPack.Tool.exe` mit seinen Abhängigkeiten. Für Agent und Relay kann jeweils der Anwendungsordner `KingdomComeTogether` aus dem entpackten, geprüften Drop-in kopiert werden. Die ausgelieferte `KCDMP.dll` kommt unter dem genannten Namen in den DLL-Ordner. Das Pak und die Mod-Vorlage kommen aus `Mods\kdcmp` desselben Archivs. Der Injector ist ein separates Entwicklungswerkzeug aus `native/KCDMP_LauncherInjector` und liegt nicht im normalen Drop-in.

Wer den Rig-Build aus einem verifizierten Archiv zusammenstellt, kann die beiden Zusatzprogramme aus diesem Checkout bauen. `$Bench` ist dabei der vorbereitete Rig-Build-Ordner. Der native Build braucht CMake und die Windows-C++-Build-Tools.

```powershell
dotnet publish dotnet/KcdMp.LogPack.Tool -c Release -r win-x64 --self-contained false -o "$Bench\logpack"
cmake -S native -B native/build -A x64
cmake --build native/build --config Release --target KCDMP_LauncherInjector
```

Je nach CMake-Generator steht der Injector direkt im Zielordner oder im Unterordner `Release`. Ein Self-Check der Maschine startet kein Spiel.

```powershell
$env:KCDMP_TEST_SOLO_ABEND = '1'
.\tools\rig\Solo-Abend.ps1 -TestSoloAbend -NurPruefen -Install "$ModdingTools"
```

## Lauf starten

```powershell
.\tools\rig\Solo-Abend.ps1 -TestSoloAbend -Netz normal -Install "$ModdingTools" `
  -BuildDirectory "$Bench" -ModTemplate "$DropIn\Mods\kdcmp" -Injector "$Injector" `
  -HostSave "$HostStand" -JoinerSave "$JoinerStand" `
  -RouteMeasure "$Treppe60" -RouteWalk "$EbeneTreppeHang" -Bandit "$TestBandit"
```

Beim Start von Kopie B erscheint **Bitte im zweiten Fenster ‚Ja‘ klicken**. Die späteren Stationen zeigen im Terminal, wann du laufen, kämpfen, umfallen, aufstehen, eine Waffe benutzen oder einen Dialog bedienen sollst. Wege und Kennungen müssen zum Wegwerfstand passen. Die Route für `rpmeasure` enthält eine 60-Sekunden-Strecke mit Treppe. `rpwalk` braucht Ebene, Treppe und Hang. Das Format steht in [ABNAHME-S2.md](ABNAHME-S2.md).

`-Netz schlecht` verwendet das Rig-Profil mit 150 ms und Jitter. `-Unbegleitet` überspringt Stationen, an denen eine Person handeln muss. Fehlende Eingaben und ausgelassene Stationen stehen im Bericht und führen zu einem unvollständigen Ergebnis.

## Ablauf und Ergebnis

Der normale Zweierlauf startet mit Smoke. Es folgen die Rigs für die Ampel, den zweiten Henry, Aufnahme und Replay sowie die Weltbereiche. Laden, Wetter beim Laden und der zehnsekündige Relay-Schnitt haben eigene frische Sitzungen. Ein weiterer Lauf verwendet den T4 Bot-Joiner mit der echten Host-Kopie. T2 führt die synthetischen Replay-Prüfungen aus. Die späteren Schritte der Zwei-PC-Abnahme beginnen erst, wenn du sie selbst startest.

Der Ausgabeordner steht am Ende im Terminal. Darin liegen `maschine.json`, die Stationsliste `abend.json`, je Zweierlauf die beiden Log-Bundles und `ERGEBNIS-PAAR.md`. Zum manuellen Nachlesen kann dieselbe Ampel mit den beiden entpackten Bundle-Ordnern erneut aufgerufen werden.

```powershell
python tools/verdict/pair.py "$HostBundle" "$JoinerBundle"
```

Exitcode 0 steht für einen vollständigen grünen Lauf. 1 bedeutet eine fehlgeschlagene Station oder eine Paar-Ampel mit Befund. 2 bedeutet fehlende Voraussetzungen, unvollständige Stationen oder einen Fehler bei den Berichten. 3 signalisiert einen fehlgeschlagenen Abbau oder eine fehlgeschlagene Bestandsprüfung. Der Ablauf ruft `down` auch nach einem Fehler auf und verwendet dabei die Prozessbelege des eigenen Laufs.
