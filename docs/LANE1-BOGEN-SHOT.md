# B3 Sichtbarer Pfeil

**Status grau.** Am Lane-Basisstand `5fc9133e` gibt es einen typisierten `Fire`-Schritt und einen separaten Pfad für gemessene Missilekontakte. Die dokumentierte Schießkomponente erzeugt im normalen Spiel ein echtes Geschoss und löst Wahrnehmung aus. Der alte Lua-Einstieg `KCD2MP_W201Shot` weist verzögerte Aufrufe ab. Ein nachgewiesener kosmetischer Pfeil für den Partneravatar fehlt. [Engine-Aufzeichnung](WO-119-action-fidelity.md#32-ranged) · [P6-Befund](T3-P6-BOW.md#befund-und-entfernte-wege) · [Alter Lua-Einstieg](../kdcmp/Data/Scripts/Startup/kdcmp.lua#L25723)

Die native Engine-Schnittstelle bietet `spawn`, `entity_load_geometry` und `remove`. Der Grabmarker nutzt ein Geometriemodell auf einer Klasse ohne Physik. Das ist ein Ansatz für eine Probe mit einem im Spiel bestätigten Pfeilmodell. Sichtbarkeit, Bewegung und Bereinigung des Modells am Partneravatar fehlen als Laufzeitbelege. [Engine-Schnittstelle](../native/KCDMP/engine.h#L43) · [Beispiel für eine Entität ohne Physik](../native/KCDMP/respawn_actions.cpp#L824)

## Übergabe an H4

- **Probe.** Ein neuer nativer Darstellungsbaustein soll beim tatsächlichen Loslassen genau einen kosmetischen Pfeil am Partneravatar sichtbar machen. Den P6-Schritt und seine Sequenz, den sichtbaren Flug und das Entfernen der Darstellung gemeinsam erfassen. Während der Probe sollen auf dem Empfänger weder eine echte `Arrow`- oder `Bolt`-Entität noch ein Missilekontakt, ein Trefferverdict oder eine Gesundheitsänderung aus der Darstellung entstehen. [P6-Kontaktpfad](../dotnet/KcdMp.Client/GameBridge.P6Missile.cs)
- **Gegenprobe.** Die Folge mit deaktivierter Darstellung, doppelter Sequenz und anschließendem Figuren- oder Weltwechsel prüfen. Dabei darf kein weiterer Pfeil entstehen. Nach dem Wechsel darf keine Darstellung der alten Figur in der Welt verbleiben. Den regulären, vom Schützen ausgelösten Treffer separat gegen dieselbe Basis prüfen.
- **Zähler.** Eine bestätigte `Fire`-Animation kann nach dem B2-Posennachweis als `ranged_fire_shown` zählen. Für einen sichtbaren Pfeil braucht es den eigenen Laufzeitbeleg. Ein tatsächlicher Missilekontakt behält seinen vorhandenen Schadens- und Zählerpfad. [P6-Zähler und Prüfung](T3-P6-BOW.md#zähler-und-prüfung)

Die Zwei-PC-Abnahme liegt im Hauptchat.
