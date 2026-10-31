@echo off
rem AeroBag yerel demosu: scripts\demo  (seçenekler için: scripts\demo -? ya da Get-Help scripts\demo.ps1 -Detailed)
rem Yürütme ilkesi bu betik için atlanır; sisteme kalıcı bir ayar yazılmaz.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0demo.ps1" %*
