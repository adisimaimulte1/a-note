
<div align="center">

<img src="assets/logo/A-Note_Logo_Original_HQ.png" width="116" alt="A-Note logo">

![Windows](https://img.shields.io/badge/Windows-10%20%2F%2011-D65A00?style=for-the-badge\&logo=windows\&logoColor=white)
![WinUI](https://img.shields.io/badge/WinUI-3-D65A00?style=for-the-badge\&logo=windows\&logoColor=white)
![Local](https://img.shields.io/badge/Local--first-Yes-D65A00?style=for-the-badge)

# A-NOTE

### Handwritten notes without the noise.

**Pen-first notebooks · local storage · photos · custom pages · touch-friendly controls**

</div>

---

## What A-Note does

A-Note is a $\color{#D65A00}{\textsf{local-first Windows notebook}}$ built around $\color{#D65A00}{\textsf{handwriting}}$, fast navigation and a minimal interface that stays out of the way while you write.

|                                          |                                                                                     |
| ---------------------------------------- | ----------------------------------------------------------------------------------- |
| $\color{#D65A00}{\textsf{Notebook library}}$ | Create, edit, favorite, search, sort and filter notebooks.                           |
| $\color{#D65A00}{\textsf{Pen-first editor}}$  | Pressure-aware ink, highlighting, targeted erasing and lasso selection.              |
| $\color{#D65A00}{\textsf{Flexible pages}}$    | Add, duplicate and delete blank, ruled or grid pages.                                |
| $\color{#D65A00}{\textsf{Natural navigation}}$ | Scroll with touch, a pen-scroll tool or the page scrollbar.                          |
| $\color{#D65A00}{\textsf{Photos}}$             | Insert, move, scale, rotate and duplicate images with their page.                    |
| $\color{#D65A00}{\textsf{Local autosave}}$     | Notebook data, ink and attachments remain on the device and save automatically.      |

A-Note does not require an account and does not include cloud sync, telemetry or an online service.

---

## Install

Download the latest installer from [GitHub Releases](https://github.com/adisimaimulte1/A-Note/releases/latest):

```text
A-Note-Setup-<version>.exe
```

A-Note installs per user to:

```text
%LOCALAPPDATA%\Programs\A-Note
```

Open the downloaded Setup EXE and follow the installer. It can create an optional desktop shortcut and launches A-Note when setup finishes. The app is self-contained, so a separate .NET installation is not required.

### Updating

Download the newest Setup EXE from [GitHub Releases](https://github.com/adisimaimulte1/A-Note/releases/latest) and run it normally. Program files are replaced in the installation directory while notebooks remain in the separate local-data directory.

---

## Notebook library

Each notebook has a $\color{#D65A00}{\textsf{title}}$, $\color{#D65A00}{\textsf{subject}}$ and $\color{#D65A00}{\textsf{accent color}}$. Titles and subjects are limited to 60 characters, and notebook titles are unique regardless of capitalization.

The library can:

- search notebook titles and subjects;
- sort by the default notebook order or alphabetically A–Z;
- show favorites only;
- filter by notebook color or subject;
- edit notebook details without recreating its pages; and
- permanently delete a notebook and all of its pages after confirmation.

Notebook accents carry into the editor, including the highlighter and editor controls.

---

## Writing

The pull-down editor toolbar keeps the main tools close to the page.

| Tool                                      | What it does                                                               |
| ----------------------------------------- | -------------------------------------------------------------------------- |
| $\color{#D65A00}{\textsf{Pen}}$          | Draw pressure-aware handwriting with a compatible pen.                     |
| $\color{#D65A00}{\textsf{Highlighter}}$  | Add translucent notebook-colored marks beneath regular ink.                |
| $\color{#D65A00}{\textsf{Eraser}}$       | Erase pen strokes or highlighter strokes independently.                    |
| $\color{#D65A00}{\textsf{Select}}$       | Lasso strokes, move the selection or delete it.                            |
| $\color{#D65A00}{\textsf{Scroll}}$       | Temporarily use the pen for smooth page scrolling with inertia.            |
| $\color{#D65A00}{\textsf{Photos}}$       | Import an image and manipulate it directly on the current page.            |
| $\color{#D65A00}{\textsf{Undo / Redo}}$  | Step through ink changes made during the current editing session.          |

Drawing intentionally accepts $\color{#D65A00}{\textsf{pen input}}$; touch remains available for scrolling and photo gestures instead of leaving accidental ink. Undo and redo are session-based and are reset when page ink is loaded again.

---

## Pages

Every notebook contains at least one page. Pages can be:

- created and navigated as a continuous vertical notebook;
- switched between $\color{#D65A00}{\textsf{blank}}$, $\color{#D65A00}{\textsf{ruled}}$ and $\color{#D65A00}{\textsf{grid}}$ paper;
- duplicated with their handwriting, paper style and photos; or
- deleted after confirmation.

Deleting the only remaining page clears its handwriting instead of removing the page itself.

---

## Photos

Photos behave like page content rather than external links. Imported files are copied into A-Note's local storage, so moving or deleting the original file does not remove the page attachment.

Select a photo to $\color{#D65A00}{\textsf{move}}$, $\color{#D65A00}{\textsf{scale}}$, $\color{#D65A00}{\textsf{rotate}}$ or delete it. One-finger dragging and two-finger touch gestures are supported. Rotation snaps near 45° intervals, and images remain constrained inside the page.

The picker accepts common Windows image formats, including PNG, JPEG, BMP, GIF, WebP, TIFF, ICO, SVG, HEIC/HEIF and AVIF. Whether a particular format renders depends on the image codecs available in Windows.

---

## Keyboard shortcuts

| Shortcut                    | Context          | Action                              |
| --------------------------- | ---------------- | ----------------------------------- |
| `Ctrl+Shift+N`              | Library          | Create a notebook.                  |
| `Ctrl+F`                    | Library          | Focus notebook search.              |
| `Ctrl+N`                    | Editor           | Add a page.                         |
| `Ctrl+Z`                    | Editor           | Undo the latest session ink change. |
| `Ctrl+Y` / `Ctrl+Shift+Z`   | Editor           | Redo the latest session ink change. |
| `Delete`                    | Editor           | Delete the selected ink.            |
| `F10`                       | Editor           | Toggle the pull-down toolbar.       |
| `Esc`                       | Editor           | Close the pull-down toolbar.        |

---

## Autosave & local data

Changes are autosaved after a short pause, and dirty pages are flushed when returning to the library or closing the app. A-Note stores data in:

```text
%LOCALAPPDATA%\A-Note
```

| Path                    | Contents                                                |
| ----------------------- | ------------------------------------------------------- |
| `a-note.db`             | SQLite notebook, page and app-setting metadata.         |
| `ink\`                  | Per-page handwriting in local JSON files.               |
| `attachments\`         | Copied image files and per-page photo metadata.         |

Ink and photo metadata are written atomically and retain a `.bak` recovery copy after replacement. SQLite uses write-ahead logging and foreign-key enforcement.

$\color{#D65A00}{\textsf{Local-first is not the same as backed up.}}$ A-Note currently has no built-in sync, export or backup command, so include this folder in your normal backup routine.

Uninstalling A-Note removes the program but intentionally leaves this data directory in place. To erase every local notebook, uninstall the app and then manually remove `%LOCALAPPDATA%\A-Note` after confirming that you no longer need its contents.

---

## Compatibility

| Platform                                                   | Support                                                        |
| ---------------------------------------------------------- | -------------------------------------------------------------- |
| $\color{#D65A00}{\textsf{Windows 11 x64}}$                | Primary supported experience.                                  |
| $\color{#D65A00}{\textsf{Windows 10 version 2004+ x64}}$  | Targeted by the app; pen and codec behavior depends on Windows. |
| $\color{#D65A00}{\textsf{ARM64}}$                         | No native installer is currently provided.                     |

A compatible active pen is required for handwriting. Touch, mouse and keyboard remain useful for navigation and controls, but mouse drawing is intentionally disabled.

---

## Build the installer

Development requires $\color{#D65A00}{\textsf{64-bit Windows}}$, the **.NET 8 SDK**, **Windows 10/11 SDK build tools** (including `makepri.exe`) and **Inno Setup 6**.

Build and run a development copy:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\run.ps1
```

Build the versioned, self-contained installer:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Installer.ps1
```

`VERSION` is the source of truth for the public software version. Release publishing targets `win-x64`, stages the unpackaged WinUI resources, and writes the installer to:

```text
artifacts\installer\A-Note-Setup-<version>.exe
```

---

## Planned

A-Note is planned to expand with:

- $\color{#D65A00}{\textsf{PDF export}}$;
- $\color{#D65A00}{\textsf{handwriting recognition}}$ and personalized handwriting adaptation;
- $\color{#D65A00}{\textsf{local semantic search}}$; and
- $\color{#D65A00}{\textsf{local AI study tools}}$ for summaries, flashcards and quizzes.

These features are roadmap items and are not included in the current release.

---

## Credits

- [Windows App SDK / WinUI 3](https://github.com/microsoft/WindowsAppSDK)
- [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/)
- [Space Grotesk](https://fonts.google.com/specimen/Space+Grotesk) — distributed with its SIL Open Font License

---

<div align="center">

Built for $\color{#D65A00}{\textsf{writing first}}$ by [Adrian Contraș](https://github.com/adisimaimulte1).

</div>
