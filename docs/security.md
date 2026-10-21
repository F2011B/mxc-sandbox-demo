# Sicherheitsgrenzen des Durchstichs

Die Anwendung erstellt echte native MXC-Sandbox-Prozesse und startet interaktive Shells darin. Microsoft.Mxc.Sdk 1.0.1 enthält die native Laufzeit. Für die Windows-Konsole kommt zusätzlich der hashgeprüfte offizielle MXC-Executor 1.0.1 zum Einsatz. Es gibt keinen ungeschützten Fallback, keinen Audit-Modus, keine VM und keinen Benutzerwechsel in der Demo.

## Dateien und Umgebung

- GUI-Freigaben werden als `FilesystemPolicy.ReadonlyPaths` bzw. `ReadwritePaths` übertragen; Freigaben umfassen Unterbäume. Überlappende Freigaben und Symlink-Aliase werden abgelehnt, damit die Demo keine plattformabhängige Vorrangregel erraten muss.
- Auf Windows werden Schreibfreigaben abgelehnt, die den Sandbox-Installationsordner einschließen oder darin liegen. Der Gast-Bootstrapper ist nur lesend verfügbar; übrige Produkt- und Steuerdateien bleiben ausdrücklich gesperrt. Dies verhindert das Unterschieben von DLLs in die vertrauenswürdige Installation über eine breite Schreibfreigabe.
- Jede Ausführung erhält einen neuen privaten temporären Arbeitsordner mit Schreibrechten. `HOME`, `TEMP` und `TMPDIR` zeigen darauf. Diese Ordner bleiben für die Inspektion erhalten; ihr Pfad lässt sich in der Konsole über `pwd` ablesen, wenn kein anderes Arbeitsverzeichnis gewählt wurde. Keine rekursive automatische Löschung von durch die Sandbox kontrollierten Verzeichnisbäumen.
- Host-Umgebungsvariablen, Zugangsdaten und Proxy-Variablen werden nicht übernommen. Ein kleiner Satz Laufzeitvariablen wird explizit gesetzt. MXC setzt die Proxy-Variablen selbst.
- MXC gewährt zusätzlich Systemdateien und Laufzeitpfade. Auf macOS zählen insbesondere `/System`, `/Library`, `/usr/bin`, `/private/etc` und die aktive Entwicklerinstallation dazu. Freigaben bedeuten deshalb nicht, dass ausschließlich die ausgewählten Verzeichnisse sichtbar wären.
- RW-Freigaben können unter macOS auch Unix-Sockets im freigegebenen Baum öffnen. Keine Docker-, SSH-Agent- oder anderen Dienst-Sockets freigeben, wenn deren Kontrolle nicht beabsichtigt ist.
- Seatbelt verwendet dieselbe Benutzeridentität und gibt Prozessmetadaten über `sysctl-read` frei. Es ist keine vollständige Abschottung von Host-Geheimnissen und keine Lösung für beliebigen feindlichen Code auf einem geheimnistragenden Mac. Die GUI kennzeichnet diese Einschränkung.

## Netzwerk

`egress.default`, `ingress.default` und `ingress.hostLoopback` sind immer `Deny`. Offline ist der Standard. Im Proxy-Modus wird nur der vom Programm selbst gestartete Loopback-Proxy als `NetworkRuntimeConfig.NetworkProxy` übergeben. Die Sandbox darf nicht direkt ins Internet oder zu anderen lokalen TCP-Diensten verbinden. Ein Programm, das die Proxy-Variablen ignoriert, verliert seine Verbindungsmöglichkeit.

Der Proxy läuft nur während der jeweiligen Ausführung und lauscht auf `127.0.0.1`. Er erlaubt exakte Paare `host:port`, keine Wildcards. Er unterstützt HTTP GET/HEAD und CONNECT-Tunnel (z.B. HTTPS mit beliebigen Methoden innerhalb des Tunnels). Andere HTTP-Methoden ohne TLS, Request-Bodies, Protokoll-Upgrades, mehrdeutige Header und Header über 16 KiB werden abgelehnt. Der Proxy ist kein allgemeiner produktionsreifer HTTP-Server.

DNS wird im Proxy aufgelöst. Der Proxy verbindet zu einer bereits geprüften IP, ohne den Namen erneut aufzulösen. Lokale, private und reservierte Adressen sind standardmäßig gesperrt. Die explizite LAN-Option gibt solche Adressen für die aufgeführten Ziele frei; damit können auch sensible interne Dienste erreichbar werden. Exakte Zielregeln gelten weiter.

CONNECT prüft das Verbindungsziel und den Port. Es prüft keine URLs, TLS-SNI, HTTP-Hostnamen innerhalb eines verschlüsselten Tunnels oder Inhalte. Ein freigegebener Server kann selbst als Relay dienen. Es gibt keine TLS-Aufbrechung und keine automatische Zertifikatinstallation. Der Proxy ist nicht als Grenze gegenüber anderen lokalen Prozessen desselben Benutzers gedacht. Ports werden nicht zwischen Läufen weiterverwendet, wenn die automatische Auswahl aktiv ist.

## Lebensdauer und Ressourcen

Der Stop-Knopf fordert über den Konsolenhelfer `MxcProcess.Kill()` bzw. die Windows-Job-Beendigung an. Das Zeitlimit ist 0 (ohne Limit) oder 1–86400 Sekunden. Die Anwendung verlässt sich nicht auf die bloße Cancellation eines SDK-Awaits. Sitzungssteuerung, direkte Konsole und Grenzen des Terminalzugangs stehen in [console.md](console.md). Ausgabe wird parallel gelesen; die GUI-Warteschlange und sichtbare Ausgabe sind begrenzt. Der Proxy hat maximal 32 gleichzeitige Verbindungen, 10 Sekunden für Header/Verbindungsaufbau und eine maximale Verbindungslaufzeit von 10 Minuten. Stop schließt auch Proxy-Verbindungen.

Prozessbeendigung folgt den Fähigkeiten des nativen Backends. Auf macOS ist dies keine belastbare Garantie gegen absichtlich abgekoppelte Prozesse; Sitzungs-/Prozessgruppenbeendigung ersetzt keinen unabhängigen Ressourcencontainer. Keine CPU-, RAM-, Dateigrößen- oder Plattenquoten. Temporäre Ergebnisse müssen bei Bedarf manuell entfernt werden.

## Plattformen

- **macOS ARM64:** Seatbelt, ohne Administratorrechte und ohne VM. Native Dateirechte, Offline-, Proxy-, Timeout- und Stop-Tests ausgeführt. MXC 1.0.1 liefert in seinem NuGet-Paket kein osx-x64-Asset.
- **Linux x64/ARM64:** Bubblewrap. Benötigt `bwrap` und nutzbare User-Namespaces. Proxy zusätzlich `slirp4netns`, `iptables` und `ip`. Fehlende Voraussetzungen führen zum Fehler; kein Host-Netzwerk-Fallback. Tests laufen in einem wegwerfbaren Docker-Testcontainer als Benutzer 1000 ohne Linux-Capabilities und ohne Host-Mounts (Seccomp und proc-Maskierung dort für verschachtelte Namespaces gelockert; einzelnes TUN-Gerät für slirp4netns). Die GUI und normale Sandbox benötigen Docker nicht.
- **Windows 11 x64:** ProcessContainer auf Build 26200.9711 mit Medium-Integrität und ohne Administratorrechte nativ geprüft. Die Probe muss BaseContainer und `needsDaclAugmentation=false` bestätigen. `allowDaclMutation=false` bleibt gesetzt; kein schwächerer ACL-Fallback. Konsoleneingaben, OMP-TUI, Strg+C, Stop, Timeout, Dateirechte und gesperrter Loopback-Zugriff sind geprüft. Windows ARM64 ist paketierbar, aber nicht nativ getestet. Proxy-Läufe werden vor dem Sandbox-Start verweigert: Ein identitätsgebundener Proxy-Peer ist noch nicht implementiert.

Für die Windows-Konsole ist `ui.disable=false` notwendig: Der vollständige Win32k-Kill-Switch verhindert die Initialisierung des .NET-Gaststarters und von OMPs nativer Erweiterung. Die Anwendung setzt weiterhin Clipboard `none`, Input-Injection `false`, Handle-/Atom-Isolation `container`, Desktop-Systemsteuerung `false`, Systemeinstellungen `none` und IME `false`. Damit bleibt eine Win32k-Angriffsfläche bestehen; die Demo ist kein Sicherheitsaudit des Windows-Kernels. Dies sind Regeln des neu gestarteten Sandbox-Prozesses, keine Änderungen an Windows-Systemrichtlinien.

Die SSH-Tests erstellen ausschließlich neue private Window-Station-/Desktop-Objekte mit eigenem Sicherheitsdeskriptor und niedriger Integritätsmarkierung, damit AppContainer-Prozesse in Session 0 USER32 initialisieren können. Bestehende Desktop-ACLs werden nicht verändert. Dieser Code liegt nur im Testprojekt und ist keine Voraussetzung des Produkts. Es werden weder Benutzerkonten angelegt noch Registry- oder Firewall-Einstellungen geändert. Der ACL-Test vergleicht die drei Testordner und das Laufwerkswurzelverzeichnis vor/nach dem Lauf; dies ist keine vollständige Inventarisierung aller Windows-Einstellungen.

## Quellen

- [Microsoft .NET V1 API](https://github.com/microsoft/mxc/blob/main/docs/api-reference/dotnet/v1/README.md)
- [MXC Seatbelt-Backend](https://github.com/microsoft/mxc/blob/main/docs/backends/seatbelt/seatbelt-backend.md)
- [MXC Bubblewrap-Backend](https://github.com/microsoft/mxc/blob/main/docs/backends/bwrap/bubblewrap-backend.md)
- [Netzwerkschema](https://github.com/microsoft/mxc/blob/main/docs/schema.md#directional-networking-supported-contracts)
