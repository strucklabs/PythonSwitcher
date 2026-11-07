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
    "Test update (dry run)"  - for a pip package or the pip summary: shows
                               what pip would change (including packages
                               pulled in, downgrades and replaced special
                               builds such as CUDA builds) without
                               installing anything.
    "Copy update command"    - copies the command that updates this entry
                               (for the pip summary: all outdated packages).
                               The commands work in PowerShell; commands
                               without quotes also work in the Command Prompt.
  "Check this entry again" on a pip package also checks whether pip would
  really install the newer version (pip sometimes keeps the old version
  silently and still reports success) and updates the summary and the
  dependency check of that Python version.
  Updates that would break other installed packages (for example
  "sympy requires mpmath<1.4") are shown as a note with the compatible
  version range and the newest fitting version; they are left out of the
  summary command. pip itself only warns about this after installing.
  In addition, every remaining update is tested with a dry run during the
  check (and the summary command as a whole). Updates that would replace a
  special build, downgrade or break something are shown as a note. With
  more than 15 outdated packages per Python version, this per-package test
  is skipped - use "Test update (dry run)" then.
  Limit: checks rely on the version requirements packages declare. Some
  incompatibilities are not declared (for example older compiled packages
  with NumPy 2), so test important environments after updating.
  "Dependencies" shows the result of "pip check" (broken or missing
  dependencies, status "conflict").
  Special builds such as "2.3.0+cu121" (CUDA/GPU) are shown as a note
  without update command, because an update from PyPI would replace them
  with the standard build.
  Python installations marked as "externally managed" (for example by uv)
  show newer package versions only as a note, without an update command,
  because pip refuses to change them.
- Remove: Removes a version from the list. Only versions downloaded with this
  program can optionally be deleted as well. Before removing, the program
  looks for virtual environments (projects) in your user folder that were
  created with this version and warns about them - they stop working if the
  version is uninstalled or deleted.

Good to know
------------
- "python" and "py" are two different commands. "python" follows the PATH
  order set by this program. "py" (the Python launcher) uses its own default
  (environment variable PY_PYTHON or a py.ini file); the Python tab shows
  which version "py" starts and where that default comes from.
- Python versions managed by uv (a Python tool used for example by InvokeAI)
  are listed with the source "uv (managed)". Their files are never deleted by
  this program; update or remove them with uv.
- The version check marks programs that are installed several times under the
  same winget ID, and for the py launcher it shows which file is actually used.
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
