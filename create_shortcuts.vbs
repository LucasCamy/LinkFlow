Dim objShell, strDesktop, objShortcut
Set objShell = CreateObject("WScript.Shell")
strDesktop = objShell.SpecialFolders("Desktop")
Set objShortcut = objShell.CreateShortcut(strDesktop & "\LinkFlow.lnk")
objShortcut.TargetPath = "D:\Projetos\LinkFlow\bin\LinkFlow\LinkFlow.App.exe"
objShortcut.WorkingDirectory = "D:\Projetos\LinkFlow\bin\LinkFlow"
objShortcut.Description = "LinkFlow Hardware Suite"
objShortcut.Save

Set objShortcut2 = objShell.CreateShortcut("D:\Projetos\LinkFlow\LinkFlow.lnk")
objShortcut2.TargetPath = "D:\Projetos\LinkFlow\bin\LinkFlow\LinkFlow.App.exe"
objShortcut2.WorkingDirectory = "D:\Projetos\LinkFlow\bin\LinkFlow"
objShortcut2.Description = "LinkFlow Hardware Suite"
objShortcut2.Save
