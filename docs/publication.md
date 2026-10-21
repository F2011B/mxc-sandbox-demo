# Veröffentlichung und Datenschutz

Der eigene Quellcode steht unter MIT. Copyright und Git-Metadaten verwenden ausschließlich das Pseudonym `Dark Light` und `darklight@noreply.com`. Abhängigkeiten behalten ihre jeweiligen Lizenzen.

Die veröffentlichte main-Historie beginnt mit einem bereinigten Quellcode-Snapshot. Frühere Entwicklungs-Commits bleiben ausschließlich lokal; sie sind keine Vorfahren des veröffentlichten Commits. Keine Veröffentlichung mit `--all`, `--mirror` oder `--tags`.

Windows-Prüfberichte sind in den unter `redactedFields` genannten Feldern anonymisiert. Benutzeridentitäten, SIDs und persönliche Installationspfade wurden durch Platzhalter ersetzt. Testresultate und Prüfsummen wurden nicht geändert. Rohlogs, Build-Verzeichnisse, Binärpakete und private Sicherungen gehören nicht ins Repository.

Vor jedem Push:

```sh
python3 scripts/check-publication.py main
```

Die Prüfung durchsucht alle erreichbaren Commit-Metadaten, Nachrichten, Dateinamen und Text-Blobs der angegebenen Historie. Sie verweigert fremde Autoren/Committer, persönliche Benutzerpfade, vollständige Benutzer-SIDs, abweichende E-Mail-Adressen, bekannte Token-/Privatschlüssel-Formate, Binärdateien und Build-Artefakte. Optionale private Suchbegriffe lassen sich als JSON-Liste über stdin mit `--identifiers-stdin` übergeben; sie werden weder ausgegeben noch gespeichert. Dieser Zusatz wird vor der ersten Veröffentlichung für die lokal bekannten Identitäten verwendet.

Die Prüfung erkennt keine beliebigen Namen ohne Kontext und ersetzt keine manuelle Sichtung. Neue Testnachweise vor dem Einchecken anonymisieren. Das GitHub-Kontoprofil wird durch dieses Projekt nicht verändert. Quellpfade werden beim .NET-Build auf einen neutralen Präfix abgebildet; Build-Ausgaben werden in dieser Quellcode-Veröffentlichung dennoch nicht hochgeladen.
