param([string]$AppDirectory = "$PSScriptRoot/../artifacts/win-x64")
$ErrorActionPreference = 'Stop'
$directory = [IO.Path]::GetFullPath($AppDirectory)
$desktop = Join-Path $directory 'MxcSandbox.Desktop.exe'
if (-not (Test-Path $desktop)) { throw 'Run package-windows.ps1 first.' }
Start-Process $desktop
Write-Host 'Sichtbare Windows-Desktop-Abnahme (echter Windows-11-Rechner erforderlich):'
Write-Host '1. Eigenen Testordner RW und einen separaten Ordner RO freigeben.'
Write-Host '2. Netzwerk vorerst deaktiviert lassen. Startprogramm: cmd.exe /D /Q.'
Write-Host '3. Starten: Es muss eine eigene Konsole mit normaler Tastatureingabe erscheinen.'
Write-Host '4. Prüfen: dir, type auf RO-Datei; echo auf RW-Datei; RO-Schreiben und unfreigegebene Dateien müssen scheitern.'
Write-Host '5. curl.exe --noproxy * https://example.com muss offline scheitern.'
Write-Host '6. OMP freigeben klicken (oder OMP-Installation RO auswählen), omp starten: Cursor, Pfeiltasten, Farben und Strg+C prüfen.'
Write-Host '7. exit /b 7: GUI muss Exit-Code 7 anzeigen. Stop und Schließen der Konsole müssen Kindprozesse beenden.'
Write-Host 'Dies ist eine manuelle Abnahmeanleitung, kein automatisierter Windows-Erfolgsnachweis.'
