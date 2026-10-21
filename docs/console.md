# Native Konsole

Die GUI erstellt eine private Sitzung mit einem unveränderlichen Profilsnapshot und öffnet den vertrauenswürdigen .NET-Konsolenstarter. Die Nutzereingabe wird erst in der von MXC gestarteten Shell ausgeführt. Kein Startfehler fällt auf eine ungeschützte Host-Shell zurück.

## macOS/Linux

macOS öffnet eine ausführbare `.command`-Datei über Terminal.app. Darin ersetzt `exec` den Starterprozess durch den eigenen Konsolenhelfer. Der Helfer startet die konfigurierte Shell mit `MxcContainer.SpawnWithPty`; Tastatureingaben, Cursorsteuerung, Farben, Strg+C und Fenstergröße werden weitergereicht. Linux verwendet einen vorhandenen Desktop-Terminalemulator.

Der äußere Terminalmodus wird gespeichert, auf raw gesetzt und beim normalen Ende wiederhergestellt. POSIX-Deskriptoren und eigene I/O-Threads vermeiden, dass `System.Console` den Modus zurücksetzt oder Steuerzeichen abfängt. Die Fenstergröße wird über die portable .NET-API gelesen; kein selbst deklarierter variadischer `ioctl`-Aufruf. Terminalausgabe wird parallel zum Prozessende gelesen, damit Darwin nicht beim Schließen eines noch vollen PTY-Puffers hängt.

ESC-basierte OSC-/DCS-/APC-/PM-/SOS-Steuerstrings werden im SDK-PTY-Ausgabepfad herausgefiltert, insbesondere Clipboard-/Passthrough-Sequenzen. Normale CSI-Zeichenfolgen für Farben und Cursor bleiben erhalten. Ein Terminalemulator ist dennoch eine zusätzliche Schnittstelle zum Host; die Demo ist kein Audit seiner gesamten Escape-Sequenz-Verarbeitung.

## Windows

Die GUI startet den Konsolenhelfer explizit mit `CREATE_NEW_CONSOLE` in einem eigenen Fenster, auch beim GUI-Start aus einem bestehenden Terminal. Der Helfer prüft den ProcessContainer mit dem .NET-SDK und verlangt einen BaseContainer ohne DACL-Augmentierung.

Da die öffentliche .NET-SDK-PTY-API ProcessContainer nicht unterstützt, startet der Helfer den offiziellen `wxc-exec.exe` 1.0.1 **mit vererbter Konsole und ohne Umleitung von stdin/stdout/stderr**. Der native MXC-Runner nimmt dann seinen `StdioMode::Inherit`-Pfad; `CREATE_NO_WINDOW` wird dort nicht gesetzt. Damit arbeiten normale Windows-Konsolenprogramme direkt mit echten Konsolenhandles. ConPTY muss dafür nicht über die MXC-SDK-API allokiert werden. Der vom Betriebssystem gewählte Konsolenhost darf Windows Terminal sein.

Der Executor wird vor Verwendung über fest eingebettete SHA-256 geprüft. Das JSON enthält ausschließlich das stabile ProcessContainer-Schema, eine explizite Umgebung und die Offline-Policy. `fallback.allowDaclMutation` und Telemetrie sind false. Es gibt keinen Audit-, Experimental-, VM- oder IsolationSession-Pfad.

Der native Executor startet mit `CREATE_NEW_PROCESS_GROUP`, wodurch er selbst Strg+C ignoriert. Ein kleiner .NET-Bootstrapper läuft **innerhalb** des ProcessContainers, setzt `SetConsoleCtrlHandler(NULL, FALSE)` und startet die Benutzershell. So erhält die Shell Strg+C, ohne dass der native Executor durch seinen eigenen Cleanup-Handler die gesamte Sitzung beendet. Der Bootstrapper ist nur lesend freigegeben; er öffnet keinen Host-Prozess. Strg+Pause kann weiterhin die Sitzung beenden.

Der native Executor startet außerdem suspendiert, wird einem Job mit `KILL_ON_JOB_CLOSE` zugewiesen und erst dann fortgesetzt. Stop beendet diesen Job. Ein unerwartetes Ende des Konsolenhelfers schließt dessen Jobhandle und beendet damit auch den Executor und seine Job-Kinder. Der MXC-Executor behält seine eigene Isolation und Prozessverwaltung.

Der native Windows-11-Test besteht elf Prüfungen einschließlich OMP 18.8.9: Die TUI zeichnet in den echten Konsolenpuffer, Enter öffnet den Provider-Dialog, Pfeil nach unten wechselt dessen Auswahl. Strg+C beendet einen laufenden `timeout.exe`-Kindprozess und die Shell kann danach weitere Befehle ausführen. Stop beendet Shell und Kindprozess; das Sitzungslimit beendet die Sandbox ebenfalls. Der installierte GUI-Einstiegspunkt `--console-smoke` öffnet über denselben Produktcode eine separate Konsole und liefert Exit-Code 0.

Win32k bleibt für .NET und OMPs native Erweiterung verfügbar. Clipboard, Input-Injection, fremde USER-Handles/Atome und Desktop-Systemsteuerung bleiben durch die explizite UI-Policy eingeschränkt. Kein Host-Benutzerwechsel oder DACL-Fallback. [Windows-Testnachweis](test-results/windows-2026-10-20.json).

Die Prüfung erfolgte über SSH in Session 0, ohne sichtbare Desktop-Anmeldung. Der Testharness erstellt dafür neue private Window-Station-/Desktop-Objekte und startet den Testworker mit einem auf Medium reduzierten Token desselben Kontos. Das löst die USER32-Initialisierung im Dienstkontext, ohne bestehende Desktop-ACLs zu ändern. Die Produktanwendung erzeugt keinen privaten Desktop, sondern verwendet den normalen Benutzerdesktop. Dessen sichtbarer Klick-/Fenstertest bleibt separat offen.

## Sitzungslebensdauer

Sitzungen liegen unter dem benutzereigenen Anwendungsdatenordner `MxcSandboxDemo/control/`, mit zufälligem Unterverzeichnis. Ein `started`-Marker wird exklusiv angelegt: Ein Snapshot kann nicht mehrfach gestartet werden. Der Helfer prüft PID und Startzeit des GUI-Prozesses und beobachtet den Stop-Marker. Die GUI beobachtet PID/Startzeit des Helfers und das atomar geschriebene Ergebnis. Schließen der GUI fordert Stop an; `exit` aktualisiert die GUI mit dem Exit-Code.

Der Kontrollordner und der laufende Anwendungsordner sind in der Workload-Dateipolicy ausdrücklich gesperrt, auch unter einer breiteren Elternfreigabe. Keine Installations-, Steuer- oder Login-Verzeichnisse anderer Host-Programme schreibend freigeben. Die bekannten macOS-Grenzen bei derselben Benutzeridentität und absichtlich abgekoppelten Prozessen bestehen weiter.

## Quellen

- [Microsoft .NET API](https://github.com/microsoft/mxc/blob/main/docs/api-reference/dotnet/v1/api.md)
- [ProcessContainer-Runner](https://github.com/microsoft/mxc/blob/main/src/mxc-sdk/src/backends/process_container/common/base_container_runner.rs)
- [Native STDIO-Abstraktion](https://github.com/microsoft/mxc/blob/main/src/mxc-sdk/src/core/mxc_common/sandbox_process.rs)
- [Windows-Window-Station-Verbindung](https://learn.microsoft.com/en-us/windows/win32/winstation/process-connection-to-a-window-station)
- [Windows-Konsolenvererbung](https://learn.microsoft.com/en-us/windows/console/creation-of-a-console)
- [Offizielles MXC-Release 1.0.1](https://github.com/microsoft/mxc/releases/tag/v1.0.1)
