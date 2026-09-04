Set sh = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
dir = fso.GetParentFolderName(WScript.ScriptFullName)
dotnet = sh.ExpandEnvironmentStrings("%LOCALAPPDATA%\Microsoft\dotnet")
If fso.FolderExists(dotnet) Then
  sh.Environment("Process")("DOTNET_ROOT") = dotnet
  sh.Environment("Process")("PATH") = dotnet & ";" & sh.Environment("Process")("PATH")
End If
sh.Run """" & dir & "\Chengshi.App.exe"" --tray", 0, False
