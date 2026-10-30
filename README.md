# Git Folder Bash Auto Layout

Windows utility that scans the subfolders in `%USERPROFILE%\git`, lets you select projects with checkboxes, opens one Git Bash window per selected folder, and automatically tiles the windows across the primary monitor.

## Features

- Detects every immediate subfolder in a configurable Git workspace directory
- Opens a Git Bash window in each selected folder
- Automatically calculates a full-screen grid based on the number of selected folders
- Supports selecting a different workspace directory
- Can create a desktop shortcut from inside the application
- Includes a prebuilt Windows executable

## Usage

1. Run `FourWindowWorkspace.exe`.
2. Select one or more project folders.
3. Click **선택 폴더 실행 + 전체화면 자동배치**.
4. The selected projects open in Git Bash and are tiled across the working area of the primary display.

The application expects Git for Windows at:

```text
C:\Program Files\Git\git-bash.exe
```

## Build

The project is a single C# WinForms source file compatible with the .NET Framework compiler included with Windows.

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" `
  /target:winexe /platform:anycpu /optimize+ `
  /reference:System.dll /reference:System.Core.dll `
  /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
  /reference:Microsoft.CSharp.dll `
  /out:FourWindowWorkspace.exe FourWindowWorkspace.cs
```

## Notes

- The program arranges windows on the primary monitor and excludes the taskbar area.
- Re-running the launcher opens new Git Bash windows for the selected folders.
- Administrator-elevated windows may require running the launcher as administrator.
