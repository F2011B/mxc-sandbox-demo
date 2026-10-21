# Abschlussprüfung 0.2.1

Prüfziel: Konfigurieren und Starten öffnet eine echte Konsole, in der OMP und normale Shell-Befehle mit den MXC-Datei-/Netzwerkregeln arbeiten.

Go für den geprüften Demo-Umfang auf macOS ARM64, Linux ARM64 und Windows 11 x64. Aktuelle Nachweise: macOS 34 allgemeine und 10 Konsolenprüfungen inklusive OMP; Linux 34 allgemeine und 8 Konsolenprüfungen; Windows 11 native Prüfungen einschließlich des installierten Konsolen-Einstiegspunkts, OMP und Job-Beendigung ohne Administratorrechte. Kein externer Sicherheitsaudit und keine Freigabe für den noch fehlenden Windows-Proxy oder ungeprüfte Windows-Builds.

Geprüft wurden die nativen und GUI-Quellen, offizielle MXC-/Win32-Dokumentation, aktuelle macOS-/Linux-Testlogs und die in Identitäts-/Pfadfeldern anonymisierten Windows-Prüfberichte unter docs/test-results/. Der Abschlusscheck erfolgte als separate nur lesende Durchsicht der Änderungen und Belege; kein unabhängiger externer Reviewer.

- macOS verwendet einen separaten Terminal.app-Prozess und einen SDK-PTY-Workload; echte Tastatureingaben, Strg+C, Farben, Größe, Exit, Stop und Dateirechte sind geprüft. Die reale OMP-TUI reagiert auf Enter/Pfeiltasten in der isolierten Ersteinrichtung und wird von der Sandbox-Steuerung beendet.
- Proxyregeln bleiben auch im PTY-Modus erhalten; erlaubte Verbindung und direkter Umgehungsversuch sind gemeinsam getestet.
- Windows startet eine neue Konsole und den hashgeprüften nativen Executor; ein innerhalb der Sandbox laufender .NET-Bootstrapper stellt normales Strg+C für die Shell wieder her.
- Kein Benutzerwechsel, VM-Backend, RPC-UI oder ungeschützter Shell-Fallback.
- Kontrolldateien und laufende Programmdateien sind für den Workload gesperrt. Sitzungen werden einmal gestartet und an PID plus Starttoken gebunden.
- Windows-Proxy bleibt gesperrt. Echte Windows-Konsolen-I/O, OMP-TUI und Prozessbeendigung sind nativ geprüft. Die SSH-Testumgebung verwendet ausschließlich neue private Desktop-Objekte; sichtbare GUI- und Dialog-Klicktests bleiben mangels Desktop-Anmeldung offen. Windows ARM64 wurde nicht nativ getestet.
- Die dokumentierten macOS-Grenzen bei Prozessmetadaten und absichtlich abgekoppelten Prozessen bleiben bestehen.

Die GitHub-Veröffentlichung umfasst ausschließlich die bereinigte main-Historie unter MIT mit pseudonymen Commit-Metadaten. Frühere lokale Historien werden nicht hochgeladen. Binärpakete und umfangreiche macOS-/Linux-Logs bleiben außerhalb des Git-Index; die kompakten Windows-Nachweise sind versioniert.

Verbleibende Grenzen: kein Windows-Modellaufruf, kein sichtbarer Windows-Desktop-Klicktest, keine Windows-ARM64-Abnahme. Für den beschriebenen Offline-OMP-Durchstich ist kein weiterer Implementierungsschritt erforderlich.
