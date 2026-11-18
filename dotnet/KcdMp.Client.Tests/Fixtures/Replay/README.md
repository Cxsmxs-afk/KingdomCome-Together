# Synthetische T2-Aufzeichnungen

Jedes Paar enthält einen Sollablauf und eine gezielte Gegenprobe mit der Endung `.old.kcdrec`. Die Eingaben sind von Hand erzeugt. Der Client-Test führt die aufgezeichneten Frames über ein echtes Loopback-Relay und prüft die dafür ausgewählten Produktionsregeln. Die Spielantworten stammen aus `ReplayGameTransport`. Beim Treppentest sind die Empfangspositionen modelliert. Die Sichtprüfung von Animation, Bewegung und Spielwirkung erfolgt im Spiel.

| Szenario | Gegenprobe |
| --- | --- |
| `weather-during-load` | Das Wetter wird bereits während des Ladens geschrieben. |
| `pickup-under-load` | Die Pickup-Antwort wartet auf einen blockierten Spielframe. |
| `wolf-pack-fight` | Ein NPC-Schadenspaket adressiert die lokale Spielerseele. |
| `death-respawn` | Der Wiederbelebungsnachweis stammt aus einer alten Weltgeneration. |
| `knockdown` | Die vitalen Zustandsdaten kennzeichnen den KO als Tod. |
| `bow-shot` | Die `Fire`-Phase meldet eine Armbrust für die nachgewiesene Bogenklasse. |
| `remote-walk-stairs` | Bei identischer gesendeter Route korrigiert der Empfänger mitten auf der Treppe hart. Die Freigabe für einen Ortswechsel fehlt. |
| `remote-fight-down-up` | Ein bereits bestätigter Schlag erreicht den Empfänger im KO-Fenster erneut. |

Jede Gegenprobe muss genau ihre erwartete Verletzung melden. Ein fehlender Relay-Frame oder ein fehlgeschlagener Transportaufruf lässt den Test fehlschlagen.
