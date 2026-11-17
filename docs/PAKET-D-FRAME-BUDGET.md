# Paket D und F1: Joiner-Frames, Pipe und begründete Fader

Basis: PR #15, `codex/host-story-authority`, `ce77d57`. Neue Branch `codex/joiner-frame-budget`, PR gegen diese Basis.

## Aus dem kritischen Frame entfernt

Die physische Pipe-Ausgabe lag auf dem Spiel-Thread: `WriteFile` und ein wartendes `GetOverlappedResult` unter einer Schreibsperre konnten den Frame bis zum lesenden Agenten festhalten. Jetzt kopieren Produzenten nur in einen Ausgangspuffer. Ein eigener Writer erledigt I/O ohne die Produzenten-Sperre. Ein blockierter Leser beendet nach einer begrenzten Wartezeit die Verbindung; Überlauf verliert keine zuverlässigen Fakten still. Verbindungs-Generation, Handle-Lebensdauer und FIFO schützen vor verspäteten Antworten einer alten Pipe. Nur reine Positionswerte desselben NPCs werden ersetzt. Erstwerte, Leben, HP, KO, gezogene Waffen und Angriffe bleiben zuverlässig und geordnet.

Ein gemeinsames Budget zählt Einzelanfragen und Batch-Unteraufträge: höchstens acht Anfragen pro Frame, höchstens zwei Millisekunden zwischen Engine-Aufrufen. Ein zweiter Drain kann das Budget desselben Frames nicht zurücksetzen. Auch die allgemeine Einmal-Queue trägt Überschüsse geordnet in spätere Frames. Der Host-Kalender-Prehook bleibt vor dem originalen Modul-Update.

Die QuickSave-/Snapshot-, Leak- und Aufräumwege scannen die SaveList nicht mehr vollständig. Sie lesen den Cache ausdrücklich als Cache; Protokolle behaupten keine bestätigte frische Continue-Auswahl. Der Snapshot prüft sowohl QuickSave-Dateien auf Disk als auch konservativ den Cache-Zähler gegen 95. Kann die Rotation bei 100 nicht ausgeschlossen werden, wird kein Snapshot angefordert. Damit kann eine stale Liste späteres Nachladen erfordern; sie darf keinen eigenen alten Save verdrängen. Die explizite Leave-Auswahl fragt Kandidaten aus dem Cache und aktualisiert die Liste höchstens einmal.

Ledger-Eingang stellt Inventararbeit nur an. Kleine Lua-Portionen bearbeiten höchstens acht Operationen pro 50-ms-Fenster, mit Zeitprüfung zwischen Operationen und höchstens acht Gegenständen pro Mengenstück. Mehrere Pipe-/HTTP-Aufrufe teilen diese Queue. Ein offenes Inventar, Menü, Load oder Snapshot hält sie zurück. Die Quicksave-Halteklammer wird auch bei Fehlern im `finally` gelöst. Welt-/Rollenreset verwirft ausstehende Timer; der vom Spiel abgefragte Ledger-Epochentoken verweigert verspätete HTTP-Befehle. APPLIED-Protokolle folgen erst der Rückmeldung einer ausgeführten Zeile, nicht dem Versand des Auftrags.

## Fehlender Paket-C-Hook

Die native Bereitschaft wird vor dem Rollenwechsel abgefragt. Fehlende NPC-KI-/Scheduler- oder Story-Anbindung erhält einen stabilen Fehlergrund bis `/join-status`, Banner und eigenem Launcher-Dialog. Deutsch und Englisch nennen den betroffenen Schutz, ein mögliches Spielupdate, das Aktualisieren der Mod auf beiden PCs und den Protokollbericht. Eine fehlende DLL-Antwort hat einen eigenen Grund. Vor- und Nachladefehler bleiben unterscheidbar; ein Hook-Fehler wird weder als Uhrfehler noch als bloßes `join failed` ausgegeben.

## F1: kein Schwarzbild auf Verdacht

Eine unbekannte schwarze Anzeige löst keinen pauschalen Fader-Eingriff nach drei Sekunden mehr aus. Eine bekannte wartende Gesprächsphase bleibt an ihre konkrete Szene gebunden. Bei Video-Landungen bleibt das Bild sichtbar, solange das Ziel fehlt; Vorbereitung und Zielsuche liegen vor dem kurzen eigenen Fader. Übergänge haben einen bekannten Eigentümer und Lease-Token. Veraltete Freigaben dürfen keinen anderen Übergang öffnen; Erfolg, Ablehnung, Fehler, Abbruch und Verbindungsreset bereinigen nur den eigenen Fader. Ein nackter unbekannter op33-Auftrag wird verweigert.

## Grenzen und Spielprüfung

Das Zwischenaufruf-Budget kann einen bereits laufenden Engine-Aufruf nicht unterbrechen. Der vor einem ausdrücklich angeforderten Load notwendige `UpdateSaveGameDescriptions`-Vollscan bleibt enginegebunden und protokolliert seine Dauer; er wird nicht blind auf einen Worker verschoben oder aus `ours_us` herausgerechnet. Auch wiederkehrende Engine-Arbeit ist kein hart echtzeitfähiger Code. Eine Garantie, dass `ours_us` unter allen Umständen nie eine Sekunde erreicht, ist deshalb ohne Spielmessung nicht belegt.

Offline werden Puffer-Reihenfolge und Zustandswechsel, Verbindungswechsel, Überlauf, blockierter Writer, gemeinsame Acht-Anfragen-Grenze, Ledger-Bursts, große Stückzahlen, Hold, Menü, alte Timer/HTTP-Befehle, Hook-Meldungen DE/EN sowie Fader-Eigentum und Abbruch geprüft. Windows-CI baut DLL, Programme, Pak, Installer und Drop-in-Zip samt Inhalts- und Installationsprüfung. Der Live-Save-Roundtrip benötigt wie bisher das Spiel.

Zweirechnerlauf: dieselbe Szene und derselbe Save vor/nach Paket D; normale NPC-Bewegung, wiederholtes Quicksave des Hosts, Joiner-Snapshot, Inventar/Menü auf/zu, Alt-Tab und kurze Pipe-Unterbrechung. `mp_main_cost on`, FRAME- und WO218-PIPE-Ausgaben sammeln: Subs/Frame maximal acht; `ours_us`-Maximum und größte Aufgabe getrennt von der Original-Engine-Zeit vergleichen. Es darf keine wartende Pipe-I/O im Spiel-Thread geben. Beobachtete FPS, die echten 3-s-Hänger, verbleibende Engine-Vollscan-Dauer und unbekannte Engine-Fader bleiben bis dahin unbestätigt.
