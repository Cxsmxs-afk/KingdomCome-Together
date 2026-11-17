# Architektur v2 — Koop, das sich nativ anfühlt

## Stehende Regel

The top goal of this fork is a smooth co-op for KCD2 that feels native, and playing together should be as fun as Elden Ring Seamless Co-op or similar. Every decision is measured against that; completely new approaches are allowed when they serve it.

Dieselbe Regel steht in `AGENTS.md`. Sie ist der Maßstab für alles darunter. Eine Änderung, die die jetzige Maschine erhält, aber Henry, das Pferd oder die Leute im Dorf weiter ruckeln, zu spät oder auseinander zeigt, hat sie nicht erfüllt. Ein neuer Weg ist erlaubt, wenn er genau das leistet. Ein MMO mit eigener Welt, Accounts und persistenten Servern ist nicht das Ziel: Elden Ring Seamless Co-op und der Kampagnen-Koop von Cyberpunk (NightCoop) sind es. Zwei Leute spielen *diese* Geschichte in *einer* Welt, und es fühlt sich an wie das Einzelspieler-Spiel.

Dieses Dokument schlägt die Architektur vor. Es schreibt keinen Code um. Der laufende Test-Branch mit den Latenz-Pflastern bleibt ein eigener Pull Request und wird hier nicht vermischt.

Die gemessene Grundlage für den Körper ist `docs/DESIGN-AUDIT.md` (Stand 0.65.0, gelesen auf `79018ea`). Wo dieses Dokument eine Zahl aus dem Audit nennt, ist das `[Audit]`. Wo es eine Datei in diesem Baum nennt, ist das `[Code]`. Wo ein fremdes Projekt nur aus seiner öffentlichen Beschreibung bekannt ist, steht das dabei. Nichts davon ersetzt zwei echte Spiele auf zwei Windows-PCs.

---

## 1. Ist-Zustand, Ende zu Ende

Zwei vollständige Spiele. Der Host lädt seinen Spielstand. Der Joiner lädt denselben Stand und sieht darin seinen eigenen Heinrich als Figur. Die Welt, die Quest, die Uhr und die Leute gehören dem Host. Das ist schon die richtige Autorität. Sie ist nicht das Problem.

```
Host-Spiel                         Joiner-Spiel
  Lua  kdcmp.lua (~37.000 Zeilen)    Lua  dieselbe Datei
   |  System.LogAlways                ^  ExecuteString (HTTP)
   |  KCD2-MP-DATA / KCD2-MP-EVT      |
   v                                  |
  kcd.log  <-- Agent liest den Schwanz
   |                                  ^
   | Named Pipe (lokal, DLL)          | Named Pipe
   v                                  |
  KCDMP.dll  Hooks, Körper, Kampf     KCDMP.dll
   |
   v
  Agent  KcdMp.Client  (GameBridge plus über hundert Teildateien)
   |
   | TCP :7778, Nagle aus          oder Steam Networking Sockets
   v
  Relay  KcdMp.Server
   |
   v
  Agent des Joiners --> HTTP-Konsole des Spiels und Pipe der DLL

Master-Server: nur ein Verzeichnis von Relays. Er simuliert keine Welt.
Launcher: startet Spiel, Agent und Relay. Kein Spielzustand.
```

Die Dateien dazu:

| Stück | Datei | Was es wirklich tut |
|---|---|---|
| Mod im Spiel | `kdcmp/Data/Scripts/Startup/kdcmp.lua` | Eine Datei, 36.990 Zeilen. Timer, Geister, Puppets, Quests, Dialog, Pferde. |
| Raus aus dem Spiel | dieselbe Datei, `System.LogAlways` mit `[KCD2-MP-DATA]` und `[KCD2-MP-EVT]` | Der Mod „sendet“, indem er eine Logzeile schreibt. |
| Agent liest das | `dotnet/KcdMp.Client/LogTailGameTransport.cs` (1.245 Zeilen) | Schwanz von `kcd.log`, hunderte Tags. Das Log hat keine eigene Uhr. |
| Rein ins Spiel | `dotnet/KcdMp.Client/HttpGameTransport.cs` | `GET` auf die Debug-Konsole `localhost:1403`, Lua über `ExecuteString`. Eine Runde pro Batch, Timeout 0,8 s. |
| Warteschlange davor | `dotnet/KcdMp.Client/BatchQueue.cs` | FIFO. Ein neuer Stand ersetzt den alten *Inhalt*, bleibt aber hinter allen Einmal-Befehlen. Level leben 15 s, Kanten 120 s. Höchstgrenze 256. |
| Agent gesamt | `dotnet/KcdMp.Client/GameBridge.cs` (7.934 Zeilen) und 109 weitere `GameBridge*.cs`; 230 C#-Dateien im Client | Session, Join, Kampf, Quest, die Pipe, die Konsole. |
| Relay | `docs/NETWORKING.md`, `TcpSocketService.cs` | TCP, Port 7778, `NoDelay` an. Events zuerst, Zustand danach. |
| Steam | `dotnet/KcdMp.Steam/SteamNative.cs` | Steam Networking Sockets ist da. Der Alltags-Weg in der Anleitung ist trotzdem TCP. |
| DLL | `native/KCDMP/`, 99 cpp, davon 58 `wo*.cpp` | Named Pipe (`pipe_server.h`), Anker statt fester Adressen (`anchors.h`), Körperantrieb (`body_drive.h`). |
| Wer entscheidet | `docs/DESIGN-AUDIT.md` Abschnitt 4.10 | Eine Kopie entscheidet nicht. Ihr Körper lebt und wird *gefragt*. Ein Halter. Eine Zeitlinie. |

`pipe_server.h` sagt den Zweck der Pipe selbst: lokal, kein Port, und „Throughput is irrelevant here — these are discrete combat events, not a per-frame stream.“ Die Pipe war für Treffer und Tod gebaut. Der Strom der Positionen läuft weiter über Log und HTTP-Konsole. `body_drive.h` ist der neuere, richtige Weg fürs Gehen: einmal pro Frame ein Movement-Request an den Controller der Engine, auf dem Main-Thread, nach dem KI-Proxy. Das Audit hat genau das als Straße B vorgeschlagen. Straße B ist angefangen, nicht zu Ende.

---

## 2. Strukturelle Schwächen

Keine Stilkritik. Jede Zeile ist ein Fehlermodus, der im Code oder im Audit steht.

### 2.1 Drei Kanäle zwischen Agent und Spiel, keiner davon ist ein Snapshot

`[Audit 4.5]` zählt sie: Pipe (Antwort auf dem Main-Thread), HTTP-Konsole (324 Lua-Funktionen und 31 Befehle), Log-Schwanz (358 Tags). Dieselbe Dreiheit auf der anderen Maschine.

Was das kostet, gemessen im Audit:

- Ein Request pro Frame durch die Pipe hat den Joiner bis 0.65.0 um bis zu 53 s hinterherlaufen lassen, bis es gebündelt wurde.
- Eine Kopie binden ist ein Dreischritt Lua → Agent → DLL. Die Reihenfolge hat Kopien 10–28 m daneben stehen lassen.
- Eine Konsolenzeile während eines Ladevorgangs ist weg. Die Konsole schneidet still ab etwa 2.100 Zeichen ab.
- Das Log hat keine Uhr. Die Alterung eines Zustands ist „wann der Agent die Zeile gelesen hat“, nicht „wann der Host sie gemeint hat“.
- Das Alter der Aktivitäts-Spur lag am 99. Perzentil bei 0,75–1,4 s, großteils Rundreisen zwischen Agent und Frame.

Der sichtbare ~9-s-Rückstand aus echten Sitzungen passt zu derselben Form, auch ohne neue Messung: `BatchQueue` ist FIFO, ein neuer Geist bleibt hinter den Einmal-Befehlen, die Konsole antwortet oft erst nach dem 0,8-s-Timeout, und eine volle Schlange von 256 Einträgen ist mehrere Sekunden Spielthread. Das Audit nennt die Joiner-Schlange nach einem Quest-Profil-Nachzug mit 8–12 s. Nagle ist es nicht (`NoDelay` ist an). Die Uhr des Pings ist es nicht (der Pong wird beim Lesen genommen, nicht hinter der Schlange).

### 2.2 Ältestes zuerst, auf mehreren Ebenen

- `BatchQueue.Add` hängt einen ersetzten Level ans Ende. Der neueste Stand wartet hinter Toasts, Türen und Questzeilen.
- `LinkScheduler` (`dotnet/KcdMp.Protocol/LinkScheduler.cs`) schickt Events zuerst und verwirft sie nicht. Ein Schub von Events hält die Position dahinter fest. Das ist Head-of-Line, auch wenn das TCP-Nagle aus ist.
- Die Render-Verzögerung der Puppets folgt dem gemeldeten Rückstand und baut ihn nur langsam ab. Ein geheilter Stau bleibt noch Sekunden sichtbar.
- `AgeLimitMs` auf `main` ist 500. Ein später Stand wird verworfen statt zwei Sekunden zu spät gespielt. Das ist richtig für „neuester Stand gewinnt“ und falsch, wenn die einzige Kopie eines Körpers dadurch Stille wird. Die Stille-Regel des Puppets und das Sende-Limit ziehen in verschiedene Richtungen.

### 2.3 Der Körper wird noch von zu vielen Stellen angefasst

`[Audit 4.10 und 6]` Ein Besitzer pro Kopie war erklärt, und trotzdem haben viele Stellen die Position geschrieben. Die DLL ist nach Work-Orders geschnitten (58 `wo*.cpp`), der Agent genauso. Es gibt keine Datei, der „der Körper“ gehört. `body_drive` ist die Ausnahme und noch nicht die einzige Hand.

Das Audit hat auf 43.077 Proben gemessen: etwa 85 % der Körper innerhalb 0,5 m, etwa 67 % in derselben Tätigkeit. Beim Gehen schreibt der Positions-Schreiber einen Schritt, der 1,85-mal so lang dauert wie der des Hosts; der Movement-Request der Engine liegt bei 1,05. `[Audit 2.2]` Schreiben verliert, Fragen gewinnt. Das ist der Beleg, kein Geschmack.

### 2.4 Die lokale KI der Kopie spielt mit

Eine Kopie ist ein echter NPC der Engine. Ihre KI, ihr Sitzplatz, ihre Arbeit und ihr Kampf laufen weiter, solange man sie nicht anhält. Der Host-Strom und diese lokale Maschine ziehen denselben Körper. Deshalb flackern Gäste zwischen Stühlen, und deshalb läuft ein Puppet nach ein paar Sekunden Stille in *dieser* Welt woanders hin. NightCoop sagt dasselbe über Cyberpunk und löst es hart: die Kopie hat keine KI, und ein lokaler Treffer auf der Kopie wird vom Strom des Besitzers im nächsten Frame überschrieben.

### 2.5 Vier Spuren, vier Verzögerungen, ein Körper

Position, Aktion, Tätigkeit und Kampf eines Körpers reisen getrennt. Auf dem Host waren sie nie getrennt. Auf dem Joiner werden sie wieder zusammengesetzt. `[Audit, Urteil]` Das ist der Grund, warum ein Schmied am Amboss steht, aber den Hammer nicht hält, und warum ein Schlag und die Reaktion nicht zusammenkommen.

### 2.6 Eine Lua-Datei als Betriebssystem

36.990 Zeilen, Lua 5.1, ein Limit von 200 Locals pro Funktion. Jede neue Hilfe muss global sein oder der Compiler bricht ab. Der heiße Pfad (Interp, Puppet, Boden, Pferd) teilt sich die Datei mit Quest, Dialog, Inventar und Dutzenden Schaltern. Ein Fehler in einem kalten Pfad liegt neben dem Code, der jede Frame läuft. Das Audit zählt 147 Schalter, fast alle an, und einen Geh-Stapel aus elf Schichten. Jede Schicht hat ihren eigenen Fehlermodus, und keine löscht die vorige.

### 2.7 Der Retail-Build kann diesen Transport nicht

Die Debug-Konsole und ein Teil der Logzeilen gibt es nur in den Modding Tools. `[Audit 4.5, Verweis auf WO-212]` Vierzig von 95 Logzeilen, die der Agent liest, sind im Retail-Build wegkompiliert. Alles, was weiter auf `ExecuteString` und `kcd.log` steht, ist an diese zweite Spiel-Installation gebunden. Die Anleitung sagt das auch: das normale Spiel aus Steam ist Einzelspieler.

### 2.8 Was schon stimmt und bleiben soll

- Host-Spielstand ist die Welt. Join ist „diesen Stand laden“, nicht „zwei Welten mergen“.
- Schaden, Tod, Uhr, Wetter, Türen sind Werte und Ereignisse, keine Posen.
- Die Pipe und die Anker (RTTI, Strings, nicht rohe RVAs) sind der richtige Ort für Engine-Aufrufe. Ein Patch, der Code verschiebt, bleibt lebendig; ein Patch, der die Funktion ändert, schaltet das Stück ab statt abzustürzen.
- Steam Networking Sockets kann zuverlässige und unzuverlässige Nachrichten. Der Relay muss das Rad nicht neu erfinden.
- `body_drive` zeigt, dass der Main-Thread der DLL der Ort für „einmal pro Frame“ ist.

---

## 3. Wie gute Koop-Mods das hinbekommen

Öffentliche Beschreibungen, Stand Herbst 2026. Kein Quellcode dieser Projekte wurde gelesen, außer wo eine SDK-Seite die Regeln selbst hinschreibt.

### Elden Ring Seamless Co-op

Der Vergleich, den die stehende Regel nennt. Der Host besitzt die Welt-Flags. Gäste laden diese Welt. Ab 1.5.0 werden die Flags beim Laden vollständig vom Host übernommen, nicht nur ein kleiner Teil, und das Spiel unterscheidet Flags, die lokal bleiben, von Flags, die der Host setzt. Events sind danach immer noch nicht perfekt. Ein Gast, der das Pferd in seiner eigenen Kampagne noch nicht hat, bekommt es in der Welt des Hosts nicht geschenkt.

Übertragbar: eine Welt, die des Hosts; Fortschritt als Flags und Stufen, nicht als jede einzelne Skriptzeile; alle spielen denselben Build. Nicht übertragbar: FromSofts eigenes Netz (zuverlässig für Schaden, unzuverlässig für Position) und die Soul-Mechanik. KCD2 hat kein fertiges Replikationsprotokoll. „Die Engine macht den Koop schon“ gilt hier nicht. Das Audit sagt ausdrücklich, das eigene Netz dieser Build ist tot.

### Skyrim Together Reborn (Tilted Online)

Client und teilweise autoritativer Server. Der Server hat die Spielwelt nicht. Wer eine Zelle und einen Actor geladen hat, *besitzt* ihn: `LocalActor` läuft normal und schickt Änderungen, `RemoteActor` bekommt sie und blockiert lokale Änderungen. Quests, Dialog und Skript laufen über den Party-Leader. Saves liegen beim Leader, nicht auf dem Server. Das Netz sitzt auf GameNetworkingSockets (zuverlässige und unzuverlässige Lanes), die Hooks im Prozess.

Übertragbar, und zum großen Teil schon die Form dieses Mods: eine Maschine simuliert, die andere zeigt. Der Unterschied, der sich anfühlt: der Remote-Actor wird von Hooks im Spielprozess getrieben, nicht von einer zweiten Anwendung, die Lua durch eine HTTP-Konsole schiebt.

### Cyberpunk 2077 — zwei Mods, nicht verwechseln

**NightCoop** ist der Kampagnen-Koop und der nähere Vergleich zu Seamless und zu diesem Fork. Beta 0.2.0 im November 2026, bis acht Spieler, nur RED4ext, im Prozess. Öffentliche Regeln:

- Die Welt, die Quests, das Leben der NPCs und die Stadt gehören dem Spielstand des Hosts. Jeder Gast bringt seinen eigenen V mit (Aussehen, Stufe, Ausrüstung). Der eigene Kampagnenstand des Gastes wird nicht umgeschrieben.
- Direktes UDP, oder ein Hub, wenn das NAT keinen direkten Weg hat. Der Hub leitet verschlossene Umschläge weiter und sieht das Spiel nicht. Ein kurzer Abriss beendet die Sitzung nicht.
- Ein NPC hat einen Besitzer, meist den Host. Die anderen sehen eine Kopie *ohne eigene KI*, die nur spielt, was der Besitzer tut: Position, Animation, Leben, Tod. Ein Effekt, ein Stoß oder ein `Kill()` auf der Kopie ist lokal und wird vom Strom überschrieben. Schaden geht als Anfrage an die Maschine, auf der der NPC wirklich lebt.
- Das SDK trennt die Leitung: unzuverlässig ist der Default (bis 1.024 Byte), zuverlässig und geordnet pro Kanal (bis 16.384). `LATEST_ONLY` schickt von mehreren Posen derselben Frame nur die neueste. Die Poll-Queue wirft das Älteste weg, wenn sie voll ist.
- Quests laufen stufenweise in derselben Reihenfolge, nicht Befehl für Befehl. Dialog ist eine Mehrheitswahl. Cutscenes laufen gleichzeitig. 0.1.1 hat Catch-up-Keys für Host und Gäste nach Lag nachgelegt, statt den Rückstand abzuspielen.
- Geteilte Wesen: die Kopie folgt der Transform, ein NPC spielt den Animationsstrom, und ein NPC kann an den nächsten Spieler übergeben werden.

**OPEN//77** ist das andere Projekt und kein Kampagnen-Koop. Dedizierte, autoritative Server, Accounts, Lua-Ressourcen, die Einzelspieler-Bevölkerung wird beiseitegeräumt, der Server entscheidet, wer in der Welt lebt. Positionen, Inventar, Fahrzeuge, Loot, Uhr und Wetter sind Server-Wahrheit, die Clients nur zeichnen. Solo-Speichern, Solo-Laden und Zeitsprung sind in der Sitzung gesperrt, weil sie die gemeinsame Zeit zerbrechen. Snapshots tolerieren Duplikate und falsche Reihenfolge. Das fühlt sich nach einem eigenen Online-Spiel an, nicht nach „die Story zu zweit“. Für diesen Fork ist es die Gegenprobe: sobald der Server die Welt selbst simuliert, ist man nicht mehr im Spiel von Warhorse, sondern in einem zweiten Spiel, das man nicht hat.

### GTA FiveM / RedM (OneSync)

Der Server merkt sich die Entities. Simuliert wird der Client, dem die Entity *gehört*, meist der nächste Spieler. Verlässt sie die Nähe, wird sie verwaist oder wandert. Der Cull-Radius ist eng (bei FiveM etwa 424 Einheiten). Server-Setter erzeugen Entities, ohne dem Client zu vertrauen. RedM ist dasselbe Modell auf der anderen RAGE-Engine.

Übertragbar ist nur die Idee: Besitz wandert mit der Nähe, und wer nicht besitzt, schreibt nicht. Nicht übertragbar ist der Server, der Entities erzeugen und ihren Zustand lesen kann. Diesen Server gibt es für KCD2 nicht. Ein „OneSync für Böhmen“ wäre ein neues Spiel.

### Was sich daraus für CryEngine / KCD2 ergibt

| Technik | Hier sinnvoll? | Warum |
|---|---|---|
| Host-Spielstand ist die Welt | Ja, ist es schon | Wie Seamless, Skyrim Together, NightCoop. |
| Eigene V / eigener Heinrich, Welt des anderen | Ja, ist es schon | Nicht anfassen. |
| Snapshot plus feste Render-Verzögerung, neuester Stand gewinnt | Ja | Die lokale Vorhersage *ist* das Spiel des Spielers. Vorhergesagt wird nur die Figur des anderen, und zwar indem man sie zwischen zwei echten Proben interpoliert, nicht indem man die Engine rät. |
| Delta-komprimierte Snapshots | Ja, später | Erst wenn *ein* Snapshot existiert. Heute sind es vier Spuren. |
| Besitz: einer simuliert, die anderen zeigen eine Kopie ohne KI | Ja, das ist der Kern | NightCoop und Skyrim Together. Die Kopie darf nicht mitentscheiden. |
| Treffer auf der Maschine des Besitzers | Ja | Steht im NightCoop-SDK wörtlich und passt zu „Schaden ist ein Wert“. |
| UDP mit zuverlässigen Kanälen | Ja, aber zweitrangig | Steam Sockets kann das. Die 9 s entstehen lokal, vor dem Socket. |
| Deterministischer Lockstep | Nein | Die KI und die Physik von CryEngine sind nicht deterministisch über zwei Prozesse. Niemand der genannten Mods macht das. |
| Dedizierter Server, der die Welt simuliert | Nein | Wie der Tilted-Server: er hat das Spiel nicht. OPEN//77 kann es, weil sie die Bevölkerung ersetzen. Das ist ein anderes Produkt. |
| Im-Prozess-Hooks statt Konsole | Ja, der größte Schritt | RED4ext, SKSE-Nachfolger, ASI-Loader. Die DLL ist schon im Prozess. Die heiße Schleife ist es nicht. |
| Quest Zeile für Zeile spiegeln | Nein | Seamless und NightCoop spiegeln Stufen und Flags. Jede gespiegelte Skriptzeile ist ein weiterer FIFO-Eintrag. |

---

## 4. Zielarchitektur

Eine Sitzung, zwei Spiele, ein Welt-Besitzer. Der Unterschied zu heute ist, *wo* eine Pose entschieden und *wie* sie gezeigt wird.

```
Host-Spiel, ein Frame
  DLL, Main-Thread
    liest Heinrich, Pferd, die Leute im Radius   -> Ring, ein Sample, Host-Zeit
    wendet auf Kopien nur Anfragen an
       (Movement, Aktivität, Kampf-Input, Werte)
  Agent
    liest den Ring, schickt Snapshots unzuverlässig, neueste gewinnen
    schickt Ereignisse zuverlässig und geordnet
       (Treffer, Tod, Quest-Stufe, Inventar, Uhr, Dialogwahl)

Joiner-Spiel, ein Frame
  DLL
    Snapshot liegt um eine feste Verzögerung zurück (ein RTT plus ein paar Ticks)
    die Kopie hat keine KI
    ein Halter: getrieben, Tätigkeit, Kampf, Ragdoll, getragen, im Dialog
    wer nicht Halter ist, fasst den Körper nicht an
  Lua
    nur noch, was Skript sein muss: Menü, Dialogtext, die Dinge ohne native Anfrage
```

Regeln, gegen die jede spätere Änderung gehalten wird:

1. **Eine Zeit.** Jede Probe trägt die Uhr des Hosts. Gezeigt wird sie um eine feste Spanne später. Ankunftszeit ist nicht die Spielzeit.
2. **Neuester Pose-Stand gewinnt.** Eine alte Pose wird verworfen, nicht abgespielt. Ereignisse werden nicht verworfen.
3. **Eine Kopie entscheidet nicht.** Ihre KI ist aus. Schaden, Tod und Aufwachen passieren auf der Maschine des Besitzers und kommen als Ereignis zurück.
4. **Ein Halter.** Die Tabelle aus Audit 4.10. `body_drive` ist der Halter fürs Gehen. Tätigkeit, Kampf und Pferd werden Halter derselben Tabelle, nicht neue Schichten daneben.
5. **Heiß ist nativ.** Was jede Frame passiert, läuft in der DLL auf dem Main-Thread. Der Agent sieht Bytes, nicht Lua-Text.
6. **Kalt bleibt zuverlässig.** Quest-Stufe, Inventar, Uhr, Dialogwahl, Tod. Eine Stufe, nicht das Skript, das die Stufe gesetzt hat.
7. **Löschen gehört dazu.** Ein Weg, der den Positions-Schreiber, den Log-Schwanz oder eine Geh-Schicht ersetzt, löscht sie in derselben Änderung.

Die feste Verzögerung ist das, was „nativ“ von „vorausgesagt“ unterscheidet. Der lokale Heinrich *ist* das Spiel, der braucht keine Vorhersage. Die Figur des anderen liegt 80–150 ms in der Vergangenheit und wird zwischen zwei echten Proben interpoliert. Springt der Abstand über ein paar Meter, wird einmal gesetzt, nicht nachgezogen. Das ist dasselbe Muster wie `body_drive` (bis auf einen halben Meter heranfahren, den Rest setzen) und wie NightCoops Catch-up: aufholen, nicht den Rückstand vorspielen.

---

## 5. Optionen

Aufwand ist die Größe der Änderung, nicht eine Kalenderzeit. S = ein abgegrenztes Stück in einem bestehenden Modul. M = ein Modul, mit einem Zwei-Spieler-Test als Abschluss. L = mehrere Module, und ein Teil lässt sich ohne das echte Spiel nicht beweisen.

| | Option | Nutzen fürs Gefühl | Risiko | Aufwand |
|---|---|---|---|---|
| A | So weitermachen: Schalter, Schichten, FIFO flicken | Die bekannten Ruckler werden seltener und kommen wieder | Niedrig pro Änderung, hoch in der Summe. Das Audit zeigt den Geh-Stapel nach acht Work-Orders | S jeweils, ohne Ende |
| B | Straße B zu Ende: Körper nur noch über Engine-Anfragen, eine Halter-Tabelle, eine Zeitlinie | Gehen, Schritt, Tätigkeit und Kampf können aussehen wie beim Host. Das ist der größte sichtbare Gewinn | Mittel bei Bewegung (der Request existiert). Hoch bei Tätigkeiten mit Werkzeug und bei Paar-Angriffen. Das Audit nennt B4 als größtes Unbekanntes | L, aber in Phasen, die einzeln spielbar sind |
| C | Heiße Schleife aus der Konsole holen: Ring in der DLL, Agent liest Bytes | Die 9 s und der Log-Schwanz hören als *Form* auf. Posen können eine feste Verzögerung haben | Mittel. Die Pipe und der Frame-Hook existieren. Falsch verdrahtet ruckelt es oder das Spiel stirbt beim Laden | M für die Pose, L bis Quest und Inventar nachgezogen sind |
| D | Netz tauschen: alles auf UDP / nur noch Steam | Weniger Head-of-Line auf der Leitung | Niedrig für die Leitung, nutzlos allein. Die Schlange sitzt vor dem Socket | M, und erst nach C |
| E | Lockstep oder Dedicated Server, der Böhmen simuliert | Auf dem Papier „richtig multiplayer“ | Es gibt keine headless Simulation. Man würde ein zweites Spiel bauen | Nicht tun |
| F | NightCoop nachbauen, soweit die Engine es hergibt: Besitzer, Kopie ohne KI, Anfrage für Schaden, neueste Pose, Quest als Stufe | Das ist B + C in der Sprache, die bei Cyberpunk schon funktioniert | Dieselben Unbekannten wie B und C. Zusätzlich: welche KI-Sperre eine Kopie wirklich stumm hält, ist ohne das Spiel nicht beweisbar | L, das ist der empfohlene Weg |

**Empfehlung: F, ausgeführt als B dann C dann D.** Nicht E. A nur noch als Pflaster, solange eine Phase nicht draußen ist.

Warum diese Reihenfolge: das Gefühl entscheidet sich im Körper, und der Körper entscheidet sich auf dem Main-Thread. Das Netz ist schon schnell genug, wenn man ihm nicht eine Sekunde alte Lua-Schlange vorsetzt. Ein UDP-Umbau vor dem Ring würde die 9 s nur zuverlässiger ausliefern.

---

## 6. Phasen

Jede Phase ist mit zwei Spielern auf den Modding Tools prüfbar und lässt das Spiel spielbar, auch wenn die nächste nie kommt. „Fertig“ heißt: der Zwei-Spieler-Check aus dem Abschnitt steht, und die Schicht, die ersetzt wurde, ist weg.

### Phase 0 — dieser Text und die Regel

Dokument und `AGENTS.md`. Kein Verhalten. Zwei Spieler testen hier nichts.

### Phase 1 — ein Halter, der Gang, den es schon gibt

`body_drive` für jeden gehenden Menschen in der Nähe, der Writer nur noch für das, was der Request nicht kann (Sturz, Luft, der letzte halbe Meter). Die Geh-Schichten, die derselbe Körper noch schreibt, werden in derselben Phase gelöscht, nicht auf „aus“ gestellt.

Check: nebeneinander durch ein Dorf. Die Füße des anderen Dorfbewohners stehen im Takt, kein Gleiten, kein Flackern zwischen zwei Punkten. Ein Stuhl-Gast, den die Engine festhält, bleibt sitzen und wird nicht im Sekundentakt umgesetzt.

Aufwand M. Risiko mittel, weil die Engine den Körper weiter anfassen kann. Beweisbar nur im Spiel.

### Phase 2 — eine Zeitlinie für diesen Körper

Pose, Gangart und „er geht gerade“ tragen dieselbe Host-Zeit und werden um eine feste Spanne später gezeigt. Die Spanne ist ein gemessener RTT plus ein kleiner Puffer, gedeckelt, und sie schnappt herunter, wenn der Strom aufholt. Sie wächst nicht mit der Länge der Schlange.

Check: der Partner sprintet, bleibt stehen, dreht sich. Die Figur bleibt im Takt und steht, wenn er steht, ohne eine Sekunde nachzuziehen. `mp_perf` zeigt die Verzögerung, und sie bleibt unter ein paar hundert Millisekunden, solange die Leitung normal ist.

Aufwand M. Ein Teil (die Uhr, der Puffer) ist ohne Spiel an einem aufgezeichneten Strom testbar. Ob die Animation dazu passt, nicht.

### Phase 3 — der Ring, die Konsole verliert die Pose

Die DLL schreibt einmal pro Frame den Stand von Heinrich, Pferd und den Leuten im Radius in einen lokalen Ring, mit Host-Zeit. Der Agent liest den Ring. `ExecuteString` bekommt keine Positionszeile mehr. Der Log-Eintrag `[KCD2-MP-DATA]` bleibt eine Weile als Vergleich und wird dann gelöscht. Die Konsole behält Quest, Dialog und Menü.

Check: derselbe Dorf-Lauf wie Phase 1, plus eine Minute, in der einer das Spiel in den Hintergrund legt oder ein Quest-Nachzug die Konsole füllt. Die Figuren dürfen kurz springen. Sie dürfen nicht mehrere Sekunden eingefroren hinterherlaufen. Im Log darf `delay` der Pose nicht mit der Länge der Lua-Schlange wachsen.

Aufwand M, Grenze zu L, weil der Ring, der Agent und das Abschalten des alten Pfads zusammengehören. Ohne Windows und ohne das Spiel nicht beweisbar. Die DLL muss einen Frame-Hook haben, der den Ring füllt, ohne den Main-Thread über das heutige Budget zu ziehen (das Audit wollte unter 1,5 ms in der langsamen Frame).

### Phase 4 — die Kopie entscheidet nicht

Die KI der Kopie ist aus, mit der Sperre, die das Spiel wirklich akzeptiert (nicht mit einer, die beim nächsten Dialog wieder aufwacht). Schaden, Tod und Aufwachen laufen auf dem Host und kommen als Ereignis. Ein Treffer auf der Kopie, den der Host nicht bestätigt hat, wird vom nächsten Snapshot überschrieben.

Check: ein Bandit, beide schlagen. Er fällt bei beiden, zeitnah, und steht nicht wieder auf, weil die Kopie ihn lokal für lebendig hält. Ein Dorfbewohner läuft nicht davon, während zwei Sekunden keine Pakete kommen.

Aufwand L. Das größte Unbekannte. Mehrere Work-Orders haben die lokale KI schon „aus“ geglaubt.

### Phase 5 — Quest und Inventar als Stufen, nicht als Skriptschwall

Eine Quest-Stufe, ein Inventar-Diff, eine Dialogwahl: zuverlässig, geordnet, idempotent. Der Joiner wendet die Stufe an. Er spielt nicht 80 Profil-Nachrichten nach, die er schon hat. Die Konsole bleibt für den Text, den nur Lua sagen kann, und ihre Schlange darf eine Stufe vorziehen, wenn sie vollläuft.

Check: eine Quest einen Schritt weiter, eine Truhe, ein Gespräch mit Abstimmung. Beides bleibt synchron, auch wenn gleichzeitig gerannt wird. Ein Toast, der zwei Sekunden zu spät wäre, darf fehlen. Ein Tod oder ein Quest-Schritt nicht.

Aufwand L. Inhaltlich viele Einzelfälle (Truhen sind heute absichtlich getrennt, Diebstahl-Markierung hat keinen Skript-Setter). Der Transport dafür ist Phase 3.

### Phase 6 — die Leitung, erst jetzt

Pose unzuverlässig, neueste gewinnt, über den Steam-Socket, der schon da ist. Ereignisse auf einem zuverlässigen Kanal. TCP bleibt der Fallback für LAN ohne Steam, mit derselben Trennung. Der Relay leitet weiter, er simuliert nicht.

Check: derselbe Lauf über Steam und über LAN. Ein verlorenes Pose-Paket macht einen kleinen Sprung, keinen Stillstand. Ein verlorenes Tod-Paket kommt nach.

Aufwand M, nachdem Phase 3 die Nachricht überhaupt als Snapshot kennt.

### Was nicht in den Phasen steht

Retail ohne Modding Tools. Das Audit schätzt den Port auf eine eigene, lange Strecke, und die Konsole existiert dort nicht. Phase 3 ist die Voraussetzung, nicht ein Abfallprodukt. Lippen, Stimme, Würgegriffe, weit auseinander spielen: eigene Lücken, keine Architekturfrage. Weit auseinander bleibt falsch, solange eine Maschine die Welt simuliert. Das ist dieselbe ehrliche Grenze wie die Leine heute.

---

## 7. Was nur das echte Spiel beantworten kann

Ohne zwei Windows-PCs und die Modding Tools ist das hier unbekannt, und ein Test in diesem Repository beweist es nicht:

- Ob der Movement-Request bei jedem menschlichen NPC im Dorf hält, oder nur bei denen, die das Audit angeschaut hat. Reiter, Stürze und Leute in der Luft sind im Kommentar von `body_drive.h` ausdrücklich nicht der Driver.
- Welche Sperre eine Kopie stumm hält, während ein Dialog, ein Sitzplatz oder ein Kampf sie wieder aufwecken will.
- Ob ein nativer Aktivitäts-Request den Hammer in die Hand legt. Das Audit hält das für das größte Unbekannte (B4).
- Ob ein Ring auf dem Main-Thread unter dem Frame-Budget bleibt, wenn zwanzig Leute im Radius stehen.
- Ob die Host-Uhr, die der Agent schon schätzt, stabil genug für eine feste Render-Verzögerung ist, oder ob die beiden Spiel-Uhren auseinanderlaufen, sobald einer lädt.
- Ob Steam-Sockets auf den Leitungen der Tester die unzuverlässige Pose wirklich zustellen, oder ob der Fallback-TCP der Alltag bleibt.
- Alles am Retail-Build. Die Anker können brechen, die Konsole fehlt, ein Teil der Logzeilen fehlt.

Ohne den Quellcode der Engine zusätzlich unbekannt: ob es einen unterstützten Weg gibt, die KI eines einzelnen Actors abzuschalten, ohne den Rest seines Körpers (Physik, Ragdoll, Animation) mit abzuschalten. Die öffentliche CryEngine-Doku beschreibt das nicht für diese Warhorse-Build. Anker und Versuche können es finden. Ein Design-Dokument kann es nicht behaupten.

---

## 8. Kleine Fixes, nicht Teil dieses Vorschlags

Gefunden, bewusst nicht in diesem Branch. Sie gehören in den Test-Branch oder in eine eigene, kleine Änderung. Sie erfüllen die stehende Regel nicht allein.

| Fix | Warum klein | Warum es die Architektur nicht ersetzt |
|---|---|---|
| In `BatchQueue` den neuesten Level vor die alten Kanten ziehen und Kanten, die schon Sekunden alt sind, fallen lassen | Die Schlange ist lokal und getestet ohne Spiel | Die Pose reist weiter als Lua-Text durch die Konsole |
| Events im `LinkScheduler` nicht mehr strikt vor jedem Zustands-Stand, sobald sie selbst alt sind | Eine Bedingung in `TryTake` | Die vier Spuren bleiben vier Spuren |
| Die Puppet-Verzögerung schnappen lassen, wenn der gemeldete Rückstand fällt, und `MP-DESYNC kind=latency delay_ms=` plus `latency_ms` in `mp_perf` | Reine Anzeige und ein Faktor | Sie misst die Schlange, sie leert sie nicht |
| `pos_native` (Pipe statt Logzeile) gegen den Log-Vergleich laufen lassen und das Ergebnis aus einer echten Sitzung holen | Der Pfad existiert und schaltet sich nach 20 Fehlschlägen ab. Im Kommentar in `kdcmp.lua` steht, er sei live noch nicht belegt | Er liest nur den lokalen Spieler, nicht die Kopien |
| Keine neue heiße Lua-Funktion mehr in `kdcmp.lua`, bis Phase 3 den Ring hat | Disziplin | Die Datei bleibt 37.000 Zeilen |

Ein laufender Branch flickt die ersten drei Punkte bereits, damit die aktuelle Fassung testbar bleibt. Dieser Pull Request enthält sie nicht.

---

## 9. Offene Fragen

1. Ist „fühlt sich an wie Seamless“ zuerst der andere Heinrich und die Leute im Dorf, oder zuerst Quest, Truhe und Gespräch? Die Phasen setzen den Körper vor die Quest. Wenn die Quest zuerst weh tut, kommt Phase 5 vor Phase 2, Phase 3 bleibt dazwischen.
2. Bleibt es bei zwei Spielern? NightCoop geht bis acht, die Leine und eine simulierte Welt sprechen für zwei, höchstens eine kleine Gruppe in derselben Stelle.
3. Darf die nächste Fassung weiter die Modding Tools verlangen? Phase 3 ist so gebaut, dass Retail später möglich wird. Sie verlangt die Tools noch zum Prüfen.
4. Wie viel Bruch bei einem Spiel-Patch ist recht? Anker statt RVAs sind der jetzige Vertrag. Ein Ring auf einem Frame-Hook kann öfter brechen als eine Lua-Zeile.
5. Truhen und Kräuter sind heute absichtlich getrennt. Sollen sie in Phase 5 in die Welt des Hosts, oder bleibt „jeder plündert seine“?
6. Dialog: Mehrheitswahl wie NightCoop, oder weiter der, der das Gespräch führt? Heute ist es näher am zweiten, mit einer Stimme des Partners.

---

## 10. Quellen

- Dieses Repository: `docs/DESIGN-AUDIT.md`, `docs/NETWORKING.md`, `docs/KNOWN-ISSUES.md`, die Dateien aus Abschnitt 1.
- Skyrim Together: [Tilted Online, technische Übersicht](https://wiki.tiltedphoques.com/tilted-online/technical-documentation/overview) (LocalActor / RemoteActor, Server ohne Spielwelt).
- Elden Ring Seamless Co-op: Änderungsnotiz 1.5.0, Welt-Flags vollständig vom Host, Events weiterhin unvollständig (Nexus, „Changes in 1.5.0“).
- NightCoop: [nightcoop.net](https://nightcoop.net/) (Host-Welt, eigener V, UDP oder Hub), [SDK NPC-Effekte](https://nightcoop.net/sdk-npc-effects.html) (Kopie ohne KI, Anfrage an den Besitzer), [SDK-API](https://nightcoop.net/sdk-api.html) (unzuverlässig als Default, `LATEST_ONLY`, zuverlässig pro Kanal).
- OPEN//77: [How it works](https://open2077.net/docs/platform) und Devblog vom 25. August 2026. Dedizierter Server, nicht der Kampagnen-Koop. Hier nur als Abgrenzung.
- FiveM OneSync: [Cfx-Dokumentation](https://docs.fivem.net/docs/scripting-reference/onesync/) (Besitz beim nahen Client, Server merkt sich die Entity, simuliert sie nicht selbst).
