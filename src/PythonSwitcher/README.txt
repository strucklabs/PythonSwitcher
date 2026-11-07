PythonSwitcher
==============

Manages several Python and Git versions on a Windows PC:
add them, remove them and choose which version is used.

Getting started
---------------
Double-click PythonSwitcher.exe. No installation is required, and .NET does
not need to be installed.

On first start, the program automatically searches for existing versions.

How to use
----------
- Activate: The selected version is used from now on whenever "python", "pip"
  or "git" is typed in a terminal. To do this, the program moves its entries
  to the front of the PATH environment variable. All other entries are kept.
- Add folder: Add a version that was not found automatically.
- Download: Download an official version (Python from python.org, Git as
  PortableGit from GitHub). The checksum and digital signature are verified
  before anything is run.
- Version check (separate tab): At the push of a button, shows what is
  installed around Python (Python, Git, tools, editors, programming languages
  and pip packages) and what has a newer version, each with a hint on how to
  update it. Nothing is changed. For editors and languages, winget (App
  Installer by Microsoft) is used if available. Installed Python and Git
  versions are read fresh on every check.
  Right-click an entry for:
    "Check this entry again" - re-checks only this entry, for example to see
                               whether an update worked.
    "Copy update command"    - copies the command that updates this entry
                               (for the pip summary: all outdated packages).
- Remove: Removes a version from the list. Only versions downloaded with this
  program can optionally be deleted as well.

Good to know
------------
- Changes apply to terminals and programs started afterwards. Windows that
  are already open keep their previous state.
- Windows reads the system PATH (applies to all users) first, then the user
  PATH. If another version is listed there, the program asks whether the
  system PATH should be changed too. This requires administrator rights.
- The previous PATH is backed up before every change. "Undo last PATH change"
  restores it. If a backup contains entries for folders that no longer exist
  (for example a deleted version), you can choose to leave them out.
- "Backups to keep" (bottom of the window, default 50) limits the number of
  backups; older ones are deleted automatically. Press Enter to apply.

Data and backups
----------------
%LOCALAPPDATA%\PythonSwitcher
  settings.json        list of versions and settings
  Backups\             PATH backups (plain text files)
  python\, git\        downloaded versions

Data from older versions (einstellungen.json, Sicherungen\) is moved to the
new names automatically.
