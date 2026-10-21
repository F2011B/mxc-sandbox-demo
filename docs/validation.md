# Prüfnachweise

Stand: 2026-10-20. Demo 0.2.1, .NET 10, Microsoft.Mxc.Sdk 1.0.1, Avalonia 12.1.4. Die native MXC-Bibliothek und der Windows-Executor stammen unverändert aus offiziellen Microsoft-Paketen.

## Umfang

Die GUI startet jetzt eine eigene Konsole. Die macOS/Linux-Konsole verwendet SDK-PTYs; die Windows-Konsole verwendet einen neuen Console-Subsystem-Prozess, den offiziellen Executor mit geerbter Konsole und einen innerhalb der Sandbox laufenden .NET-Shell-Bootstrapper. Kein RPC-Ersatz, Benutzerwechsel oder VM-Backend.

## Verifikation dieser Änderung

- Vollständiger Release-Build ohne Warnungen und Fehler.
- macOS: 34 lokale Profil-/Proxy-/Datei-/Netzwerk-/Prozessprüfungen; ohne externen HTTPS-Aufruf.
- macOS: 10 Konsolenprüfungen inklusive echter Host-TTY-Eingabe, Strg+C, Farben, Fenstergrößenwechsel, Stop/Timeout, PTY-Proxy samt direktem Umgehungsversuch, Prozessidentität, Stop-Marker einer laufenden Host-Konsole und `omp --version` aus der Shell. Zusätzlich startet die echte OMP-TUI mit isoliertem HOME/offline; Enter überspringt die Startanimation, Pfeil nach unten wechselt die Provider-Auswahl, der Stop-Marker beendet sie. Keine Anmeldung oder Modellanfrage.
- Linux ARM64: dieselben 8 Konsolenprüfungen ohne die hostabhängige OMP-Installation im eingeschränkten Docker-Testcontainer bestanden (`artifacts/console-linux-tty.log`). Die aktualisierten 34 Profil-/Proxy-/Datei-/Netzwerkprüfungen sind ebenfalls bestanden; der frühere 33er-Lauf mit externem HTTPS bleibt ein separater Nachweis.
- Reeller Start über Terminal.app, einschließlich Start des Konsolenhelfers und erfolgreicher `isatty`-Prüfung im Workload.
- GUI-Konfiguration, Rechtewechsel, Start-/Stop-Zustände im headless Smoke-Test; eigene Terminaltests decken den tatsächlichen Konsolenpfad ab.
- Windows-x64-Paket inklusive .NET-Laufzeit, nativem Executor und Gast-Bootstrapper cross-publiziert. Die Executor-Dateien werden gegen festgelegte SHA-256 geprüft.
- Windows 11 Pro x64 25H2, Build 26200.9711: **11/11 native Prüfungen bestanden**, mit unverändertem offiziellen Executor 1.0.1. Produktinstallation 0.2.1 inklusive normalem .NET-Gast-Bootstrapper, alle 634 Paketdateien per SHA-256 gegen die Installation geprüft; keine NativeAOT- oder Sondercompiler-Abhängigkeit. [Anonymisierter Prüfbericht](test-results/windows-2026-10-20.json), [OMP-Konsolenpuffer](test-results/windows-omp-2026-10-20.txt), [Paket/Installation](test-results/windows-deployment-2026-10-20.json).
- Windows-Testumfang: Medium-Token desselben Kontos, kein Administrator; BaseContainer ohne DACL-Augmentierung; installierter GUI-Konsolen-Einstiegspunkt; echte Tastatureingabe/Arbeitsverzeichnis/Exit-Code; RO/RW/gesperrte Dateien; keine geerbte Host-Umgebung; laufender Host-TCP-Dienst erreichbar aus Host und blockiert aus Sandbox; OMP-Version und normaler Start mit `omp` ohne Testoptionen sowie echte Provider-Auswahl mit Enter/Pfeiltasten; Strg+C bei laufendem Kindprozess und fortsetzbare Shell; Stop samt Kindprozess; Timeout; unveränderte ACLs der Testordner und des Laufwerkswurzelverzeichnisses.
- SSH Session 0 enthält keinen angemeldeten Desktop. Nur der Testharness erzeugt eine neue private Window Station und einen neuen Desktop mit passendem initialem Sicherheitsdeskriptor. Keine Anpassung bestehender Windows-Richtlinien, Firewallregeln, Registry oder Desktop-ACLs. Das Produkt nutzt den normalen Benutzerdesktop. Sichtbarer GUI-Klicktest und native Ordnerdialoge bleiben offen.

Die aktuellen macOS-/Linux-Ergebnisse liegen unter `artifacts/windows-final-macos.log`, `artifacts/windows-update-macos-omp.log` und `artifacts/windows-update-linux.log`; frühere Läufe unter `artifacts/console-*.log`. Linux-Ergebnisse werden dort separat aufgeführt; weder ein einzelner Docker-Lauf noch ein Skip ersetzt eine Windows-Abnahme.

Die veröffentlichten JSON-Nachweise wurden ausschließlich in den unter `redactedFields` aufgeführten Identitäts-/Pfadfeldern anonymisiert. Die numerischen Ergebnisse, Sandbox-Probe, Testanzahl und Prüfsummen bleiben erhalten.

## Sicherheitsprüfungen

Profil-Roundtrip und Eingabevalidierung; Ordnerüberlappungen, fehlende Ordner, Rechte und Arbeitsverzeichnis; exakte Proxy-Ziele/Ports, private Adressen, CONNECT, Header-Schutz, DNS/IP-Prüfungen und Live-Verbindungsschluss. Zusätzlich wird das Kontrollverzeichnis trotz freigegebenem Elternordner ausdrücklich gegen Lesen/Schreiben geprüft.

Die nativen Dateiprüfungen kontrollieren sowohl erlaubte Zugriffe als auch blockierte Schreib-/Leseversuche, Symlink-Ausbrüche und Host-Umgebungsvariablen. Die Konsolentests kontrollieren echte `isatty`-Ergebnisse, tatsächlich geschriebene Dateien und gemessene Terminalgröße; reine Echo-Ausgabe reicht nicht als Beweis.

Der Windows-JSON-Vertrag wird zusätzlich plattformunabhängig auf Offline-Policy, UI-Beschränkungen, isolierte Umgebung und verweigerten DACL-Fallback geprüft. Schreibfreigaben der Produktinstallation und überlappende Laufwerkswurzel-Freigaben werden durch Regressionstests zurückgewiesen. Die nativen Windows-Kerneltests ergänzen diese Vertragsprüfungen; sie ersetzen keinen Sicherheitsaudit.

## Reproduktion

```sh
./scripts/test.sh
./scripts/test-linux.sh
./scripts/package-macos.sh
"artifacts/MXC Sandbox Console.app/Contents/MacOS/MxcSandbox.Desktop" --console-smoke
"artifacts/MXC Sandbox Console.app/Contents/MacOS/MxcSandbox.Desktop" --render "$PWD/artifacts/gui.png"
```

```powershell
./scripts/package-windows.ps1 -Runtime win-x64
./scripts/test-windows.ps1 -OmpDirectory "$env:LOCALAPPDATA/Programs/omp"
# Für SSH Session 0 mit erhöhtem Ausgangstoken: -StandardUser ergänzen.
./scripts/verify-windows.ps1
```

Linux-Tests laufen als UID 1000 ohne Linux-Capabilities, Host-Mounts, Host-Netzwerk oder Docker-Socket. Testweise sind verschachtelte Namespaces, proc-Mounts und das einzelne TUN-Gerät zugelassen. Root-Dateisystem read-only, 128 MiB tmpfs, maximal 128 Prozesse und 512 MiB RAM. Docker ist ausschließlich Testinfrastruktur.

Der Terminalharness (`script`) ist nur Testinfrastruktur. SDK-Aufrufe erfolgen im Produkt in einem frischen Konsolenhelfer; managed gestartete Test-Kindprozesse werden von den übrigen nativen SDK-Testfällen getrennt.

## Offene Grenzen

- Windows-Proxy weiterhin gesperrt; ein identitätsgebundener Proxy und dessen Windows-Abnahme fehlen.
- macOS-Prozessmetadaten und absichtlich abgekoppelte Prozesse sind nicht vollständig abgeschottet.
- Native Ordner-/Dateidialoge sind integriert, aber nicht per Betriebssystem-Klicktest automatisiert.
- OMP-Version und interaktive Ersteinrichtung samt Tastatureingabe/Stop geprüft; keine Modellanfrage mit privaten Host-Zugangsdaten ausgeführt. Unter Windows bleibt OMP derzeit offline; Modellanfragen benötigen den noch fehlenden identitätsgebundenen Proxy.
- Keine CPU-/RAM-/Plattenquoten, Signierung/Notarisierung oder Enterprise-Verwaltung in diesem Durchstich.
