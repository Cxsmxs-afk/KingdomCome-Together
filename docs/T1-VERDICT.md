# Ampel für gesammelte Sitzungslogs

Die erste Archivdatei aus **Collect Logs** enthält `ERGEBNIS.txt`. In `BRIEFING.md` steht dieselbe Auswertung unter **Ergebnis**. Die Werte gelten für den gesammelten Spielstart. Die Grenzwerte und Messquellen stehen in `VerdictRules.cs`.

| Ampel | Wertung der Agent-Warteschlange |
| --- | --- |
| Grün | p99 ≤ 250 ms und Maximum ≤ 1000 ms anhand der Queue-Messreihe |
| Gelb | p99 ≤ 1000 ms und Maximum ≤ 2000 ms, oberhalb der grünen Grenze |
| Rot | p99 > 1000 ms oder Maximum > 2000 ms |
| Grau | Es fehlt eine Messreihe für die Entlastung. Ausdrücklich protokollierte hohe Verzögerungen können trotzdem Gelb oder Rot auslösen. |

Bei den einzelnen Regeln steht Grau für fehlende oder nicht anwendbare Messungen. Die Warteschlangenmessung bildet die Grundlage für die Gesamtampel. Ein festgestellter Warn- oder Fehlerwert kann sie verschlechtern. Die ausstehenden Spielbeobachtungen stehen beim jeweiligen Paket.

## Host und Joiner vergleichen

```powershell
python tools/verdict/pair.py <Host-Ergebnis> <Joiner-Ergebnis>
```

Für beide Seiten sind `ERGEBNIS.txt` oder der entpackte Ordner gültig. Mit einer `tar`-Version, die Zstandard unterstützt, nimmt der Befehl auch `.tzst`-Archive an. Der Bericht `ERGEBNIS-PAAR.md` landet im aktuellen Verzeichnis. Rückgabe `0` bedeutet Grün, `1` einen anderen Paarstatus und `2` einen Eingabe- oder Ausgabefehler.

## Szenario mit zwei Instanzen

Das Rig benötigt zwei verbundene Modding-Tools-Instanzen und das LogPack-Tool aus demselben Build. Nach dem Verbinden startet dieser Befehl das Szenario.

```powershell
python tools/rig/scenarios.py verdict
```

Das Szenario wartet auf eine neue `MP-T3-STATS`-Zeile pro Rolle, sammelt beide Bundles und legt den Paarbericht beim Szenarioergebnis ab. Mit synthetischen Tests sind 230&nbsp;s Queue-Rückstand, eine saubere Messreihe und ein leeres Log abgedeckt. Der Ablauf mit zwei laufenden Spielinstanzen ist offen.
