# Host-Verlust in Königreich und Sitzung

Wenn die Verbindung des ausgewählten Hosts endet, bleibt die Welt- und Schadensautorität unbesetzt. Das Relay stoppt Welt-, Kampf- und Dialogverkehr. Steuerverkehr für die Verbindung sowie Sprache und Session-Leave bleiben möglich. Die nächste Autorität ist der zurückgekehrte Host nach bestätigter Welt-Epoche und Run-ID. Diese Prüfung erfolgt vor dem Weiterleiten seiner Weltmeldung. [Relay-Entscheidung](../dotnet/KcdMp.Server/Features/ClientHandling/ClientHandler.cs) · [Rückkehrprüfung](../dotnet/KcdMp.Server/Features/Tcp/TcpBroadcastService.cs)

Der Host-Agent kann im erhaltenen Spielprozess einen Rückkehrbeleg aus seinem bestehenden Weltzustand senden, bevor ihm das Relay die Spielautorität zurückgibt. Der Gast beendet einen laufenden H20-Join sicher. Im bereits geladenen Coop-Spiel hält WO211 die Welt an. Die Gäste behalten ihren Save-Lock und können warten oder explizit in ihre eigene Partie zurückkehren. Der Rückkehrpfad für diese Pause speichert keinen abschließenden Henry-Snapshot in die persönliche Kampagne. [Epochensender](../dotnet/KcdMp.Client/GameBridge.Wo206.cs) · [H20-Abbruch und Pause](../dotnet/KcdMp.Client/GameBridge.Wo124.cs) · [WO211](../dotnet/KcdMp.Client/GameBridge.Wo211.cs)

## Automatisierte Abnahme

```bash
dotnet test dotnet/KcdMp.Relay.Tests -c Release --filter 'FullyQualifiedName~HostAbsentRelayTests'
dotnet test dotnet/KcdMp.Client.Tests -c Release --filter 'FullyQualifiedName~HostAbsentPauseTests|FullyQualifiedName~WorldReconnect|FullyQualifiedName~JoinCalendarIntegrationTests'
pwsh -NoProfile -File tools/Test-JoinReadiness.ps1 -Check EndToEnd
```

Die Relay-Matrix öffnet zwei, drei und vier echte TCP-Verbindungen. Sie beginnt einen Join, einen Kampftreffer oder einen Dialogvorgang, schließt dann die Host-Verbindung und belegt über echte Relay-Reads die unterdrückte Folgeaktion. Zusätzlich prüft sie einen zurückkehrenden Host mit falscher Epoche, falscher Run-ID und fehlendem Run-Beleg. Die Client-Tests verwenden produktive Agentenpfade und kontrollierte Spiel- und DLL-Grenzen.

## Abnahme im Spiel

Auf gleicher Build-ID einer laufenden Runde beitreten, die Host-Verbindung während eines echten Dialogs und Kampfs einzeln trennen und kurz darauf wiederherstellen. Auf beiden Gästen `MP-HOST-ABSENT pause`, sichtbaren Hinweis und native Hold-Bestätigung prüfen. Die gemeinsamen Welt- oder Schadensschreiber bleiben unbesetzt. Nach der Rückkehr muss `MP-HOST-ABSENT resume` dieselbe Welt-Epoche und Run-ID zeigen. In einem zweiten Durchlauf über den Menüeintrag heimkehren. Dateiliste und SHA-256 der eigenen Saves vor und nach Pause und Heimkehr vergleichen. Den Rollenwechsel wiederholen. `dmg_double=0`, `rp_owner_n>0`, `owner_violations=0` und `ours_us_p95 < 2000` anhand der Spielprotokolle bewerten.

NightCoop nutzt die Welt des Hosts und lässt Zustände vom zuständigen Besitzer anwenden. Seamless Co-op trennt die Heimkampagne von der Coop-Sitzung. Die KCT-Pause bewahrt diese Zuständigkeit während einer Host-Unterbrechung. [NightCoop SDK](https://nightcoop.net/sdk/docs/concepts.md) · [Seamless Co-op FAQ](https://ersc-docs.github.io/faq/)
