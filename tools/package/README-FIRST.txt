SeedLab machine report - please read this first
===============================================

What this is
------------
SeedLab is a hobby tool that reproduces the world generation of the game Valheim on a PC, offline.
This package is a small test program for it. It checks that your PC computes SeedLab's world-
generation arithmetic exactly - bit for bit - the same way as the PC it was built on, at every
instruction-set level your processor has (AVX-512, AVX2 and plain code), and it measures how fast
the PC runs that work. You do not need Valheim, and nothing from the game is in this package.

What it does, and what it does not do
-------------------------------------
- It reads only: your processor's name, model, cores and instruction sets; how much memory the PC
  has; the Windows version and build; the version of Windows' C runtime library (ucrtbase.dll);
  whether a laptop is on mains power.
- It does NOT read your user name or computer name, your files, serial numbers, product keys,
  network details, or anything outside this folder.
- It installs nothing and changes no settings. It runs on its own copy of .NET, in the "dotnet"
  folder here; a .NET already installed on the PC is neither used nor touched.
- It writes only inside this folder: a temporary "work" folder that it deletes when it finishes,
  and the report, seedlab-machine-report.txt.

Before you run it
-----------------
1. (Recommended) Check the download. The zip's SHA-256 is on the Releases page, in SHA256SUMS.txt.
   In PowerShell, in the folder holding the zip:
       Get-FileHash .\SeedLab-MachineReport-1.0.0-win-x64.zip
   The Hash it prints must be the same as the one on the Releases page.
2. Right-click the zip, choose Properties, tick "Unblock" if it is there, and click OK. Then
   extract it (right-click, "Extract All..."). Unblocking first avoids most Windows warnings.
3. Close other programs - games, browsers, videos - because part of the test measures speed.
   Plug a laptop into mains power.

Run it
------
4. Open the extracted folder and double-click "Run SeedLab machine report.bat".
5. If a blue "Windows protected your PC" box appears (SmartScreen), click "More info" and then
   "Run anyway". It appears because the program is new and not signed, not because anything is
   wrong. If an "Open File - Security Warning" box asks, click "Run".
6. A black window shows the progress. It takes about 3 to 5 minutes; please leave the PC alone
   meanwhile. When it says "Finished", press any key to close the window.

Send it back
------------
7. Send back the file seedlab-machine-report.txt from this folder. That one file is all that is
   needed. Open it in Notepad first if you like, to see exactly what it holds: the hardware and
   Windows facts above, the result of every check, and the same data again as a JSON block.
8. Afterwards, delete the whole folder and the zip. Nothing else was left on the PC.

If something goes wrong
-----------------------
- "You must install .NET to run this application": the "dotnet" folder did not extract completely,
  or security software removed a file from it. Extract the zip again, into a short folder such as
  C:\SeedLabTest, and run it from there.
- The window closes at once, or shows an error: take a screenshot of it and send that, together
  with seedlab-machine-report.txt if the file exists.
- You can stop it at any time with Ctrl+C. If you close the window instead, a "work" folder may be
  left inside this folder; deleting the whole folder removes it.

Licences
--------
The program's own code is under the MIT License (LICENSE.txt). The "dotnet" folder is Microsoft's
.NET runtime, redistributed unmodified under Microsoft's licence terms in dotnet\LICENSE.txt (its
third-party notices are in dotnet\ThirdPartyNotices.txt); running the program uses it under those
terms. Other third-party notices: THIRD-PARTY-NOTICES.txt. Valheim is a trademark of Iron Gate AB;
this is an independent project, not affiliated with or endorsed by Iron Gate or Coffee Stain.
