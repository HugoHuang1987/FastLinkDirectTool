Option Explicit

Dim shell, fileSystem, scriptDirectory, powershellPath, monitorScript, command, exitCode

Set shell = CreateObject("WScript.Shell")
Set fileSystem = CreateObject("Scripting.FileSystemObject")

scriptDirectory = fileSystem.GetParentFolderName(WScript.ScriptFullName)
powershellPath = shell.ExpandEnvironmentStrings( _
    "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe")
monitorScript = fileSystem.BuildPath(scriptDirectory, "Monitor-FastLink.ps1")

command = Chr(34) & powershellPath & Chr(34) & _
    " -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass" & _
    " -File " & Chr(34) & monitorScript & Chr(34) & _
    " -DurationHours 720 -IntervalSeconds 10"

' Window style 0 keeps the child process hidden. Waiting keeps this scheduled
' task in Running state so its IgnoreNew policy continues to prevent duplicates.
exitCode = shell.Run(command, 0, True)
WScript.Quit exitCode
