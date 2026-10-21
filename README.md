# MXC Sandbox Demo 0.2.1

.NET/Avalonia-Konfiguration mit **eigener interaktiver Konsole**. Nach **Sandbox starten** öffnet sich auf macOS Terminal.app und unter Windows eine native Konsole. Darin arbeitest du mit einer Shell und startest OMP ganz normal. Kein RPC-Ersatz für die OMP-Oberfläche, kein Docker zur Laufzeit, kein Benutzerwechsel und keine VM.

## Benutzen

Auf macOS nach dem Bauen die App `artifacts/MXC Sandbox Console.app` öffnen:

```sh
open "artifacts/MXC Sandbox Console.app"
```

1. **OMP freigeben** findet die OMP-Installation aus dem Host-PATH oder unter `%LOCALAPPDATA%\Programs\omp` und gibt sie nur lesend frei. Alternativ über **Ordner auswählen** hinzufügen. Ein Beispiel für eine Homebrew-Installation ist `/opt/homebrew/Cellar/omp/18.8.9`.
2. Projektordner auswählen und **Lesen und schreiben** setzen. Bei Bedarf als **Arbeitsverzeichnis** eintragen. Alle anderen persönlichen Dateien bleiben grundsätzlich gesperrt; MXC stellt zusätzlich seine Systempfade bereit.
3. Auf macOS bei Bedarf den Filter-Proxy aktivieren und die erforderlichen Ziele als `host:port` eintragen. Standard ist offline. Der Windows-Proxy bleibt in dieser Demo gesperrt, bis sein identitätsgebundener Pfad auf Windows implementiert und verifiziert ist.
4. Startprogramm beim Standard lassen: `/bin/bash --noprofile --norc -i` bzw. `cmd.exe /D /Q`. **Sandbox starten** öffnet das eigene Konsolenfenster.
5. Dort z.B. `pwd`, `ls`, `omp --version` und anschließend `omp` eingeben. Farben, Pfeiltasten und Strg+C gehen an die Sandbox. `exit` beendet die Sitzung; **Beenden** in der GUI stoppt sie ebenfalls.

Das Zeitlimit ist in Sekunden angegeben: **0 = ohne Limit**, ansonsten bis 86400 Sekunden. Profile können als JSON gespeichert und geladen werden; Laden startet keinen Prozess.

Die Shell erhält einen eigenen temporären HOME-Ordner. Bestehende Host-Anmeldungen und API-Schlüssel werden nicht übernommen. Für Modellanfragen benötigt OMP innerhalb der Sandbox seine eigene Anmeldung bzw. API-Konfiguration und die dafür benötigten Proxy-Ziele. Unter Windows ist die Oberfläche derzeit offline nutzbar; Modellanfragen über das Netzwerk benötigen den noch fehlenden Windows-Proxy. Für persistente OMP-Einstellungen kann in der Konsole `PI_CODING_AGENT_DIR` auf einen eigenen, explizit schreibfreigegebenen Ordner gesetzt werden. Keine Zugangsdaten ins GUI-Befehlsfeld schreiben.

Nur explizit lesefreigegebene Installationsordner und deren vorhandene `bin`-Unterordner werden dem System-PATH nachgeordnet hinzugefügt. Weitere Laufzeiten (z.B. Python oder Node) benötigen eigene Lesefreigaben, wenn sie nicht zu den von MXC bereitgestellten Systempfaden gehören.

## Windows-Paket

Das lokal gebaute Paket liegt unter `artifacts/win-x64/`, einschließlich .NET-Laufzeit. Auf einem geeigneten aktuellen Windows-11-System `MxcSandbox.Desktop.exe` starten. Keine Installation oder VM erforderlich. Die Anwendung verweigert DACL-Fallbacks und prüft den nativen ProcessContainer vor dem Start.

**Auf Windows 11 x64 nativ geprüft:** elf Tests mit demselben Benutzerkonto, Medium-Integrität und ohne Administratorrechte bestanden. Dazu gehören echte Konsoleneingaben, OMP 18.8.9 mit Enter/Pfeiltasten, Strg+C, Stop, Timeout, Ordnerrechte und Offline-Netzwerk. Der interaktive Pfad verwendet den offiziellen, SHA-256-geprüften `wxc-exec.exe` 1.0.1 mit geerbter echter Konsole. Der Proxy bleibt unter Windows gesperrt.

Auf dem Testrechner ist Version 0.2.1 unter `%LOCALAPPDATA%\Programs\MxcSandboxConsole\0.2.1` eingerichtet. Die Desktop-Verknüpfung **MXC Sandbox Console** öffnen, **OMP freigeben** anklicken, den Projektordner mit Schreibrechten auswählen und **Sandbox starten** drücken. In der neuen Konsole `omp` eingeben. Die bereitgestellte OMP-Datei liegt unter `%LOCALAPPDATA%\Programs\omp\omp.exe`; eine Änderung des Host-PATH ist nicht nötig.

Die Windows-Abnahme lief per SSH in Session 0 mit einem eigens erzeugten privaten Test-Desktop. Es wurden keine bestehenden Desktop-ACLs, Host-Dateirechte, Windows-Richtlinien, Registry-Einstellungen oder Firewallregeln angepasst. Das Produkt verwendet den normalen Desktop des angemeldeten Benutzers. Ein sichtbarer Klicktest einschließlich des Ordnerdialogs bleibt offen, weil kein Desktop-Benutzer angemeldet war. [Testnachweis](docs/test-results/windows-2026-10-20.json), [manuelle Desktop-Abnahme](scripts/verify-windows.ps1).

Erneut bauen (.NET 10 SDK, PowerShell):

```powershell
./scripts/package-windows.ps1 -Runtime win-x64
./scripts/test-windows.ps1 -OmpDirectory "$env:LOCALAPPDATA/Programs/omp"
# Bei erhöhtem SSH-Token in Session 0: zusätzlich -StandardUser
```

Beim ersten Mal lädt das Skript das offizielle MXC-Releasearchiv (ca. 517 MiB), prüft seine veröffentlichte SHA-256 und extrahiert ausschließlich die nativen Windows-Executor-Dateien. Die Laufzeitanwendung lädt nichts nach.

## Entwicklung und Tests

```sh
./scripts/run.sh
./scripts/test.sh
./scripts/package-macos.sh
# Linux-Integrationsprüfungen in Docker:
./scripts/test-linux.sh
```

Zusätzliche Diagnostik:

```sh
dotnet run --project tests/MxcSandbox.Checks -- --internet
dotnet run --project tests/MxcSandbox.Checks -- --unit-only
dotnet run --project tests/MxcSandbox.ConsoleChecks -c Release -- /absoluter/pfad/MxcSandbox.Console /opt/homebrew/Cellar/omp/18.8.9
dotnet run --project src/MxcSandbox.Desktop -- --console-smoke
dotnet run --project src/MxcSandbox.Desktop -- --render /tmp/mxc-gui.png
```

Die Anwendung nutzt das offizielle Microsoft.Mxc.Sdk 1.0.1 und Avalonia 12.1.4. macOS/Linux verwenden SDK-PTYs, Windows den nativen Executor aus demselben offiziellen Release. [Sicherheitsgrenzen](docs/security.md), [Prüfnachweise](docs/validation.md), [Konsolenarchitektur](docs/console.md).

## Lizenz und Veröffentlichung

Der eigene Quellcode steht unter der [MIT-Lizenz](LICENSE), Copyright (c) 2026 Dark Light. Abhängigkeiten wie Microsoft.Mxc.Sdk und Avalonia behalten ihre jeweiligen Lizenzen.

Veröffentlicht wird eine bereinigte Quellcode-Historie mit dem Pseudonym Dark Light. Die Windows-Prüfberichte enthalten anonymisierte Identitäts-/Pfadfelder; Prüfergebnisse und Prüfsummen bleiben erhalten. Build-Ausgaben und ursprüngliche lokale Historien werden nicht hochgeladen. Siehe [Datenschutz beim Veröffentlichen](docs/publication.md).

Die lokal gebaute macOS-App ist nicht Apple-notarisiert.
