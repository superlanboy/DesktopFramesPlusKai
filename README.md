<h1 align="center">Desktop Frames + Kai</h1>
<p align="center"><i>Organize your desktop like magic!</i></p>
<p align="center"><b>A free, open-source Stardock Fences alternative for Windows 10/11</b> — group desktop icons into frames, mirror folders, and tidy your desktop.</p>

<p align="center">
  <img width="150" height="150" alt="Desktop Frames" src="https://github.com/user-attachments/assets/a88f7771-8ae8-4be8-86dc-4e8aabfa5a77" />
</p>

---

> ### 🔱 This is a personal fork
> A personal fork of [**limbo666/DesktopFramesPlus**](https://github.com/limbo666/DesktopFramesPlus) (MIT), which is itself a continuation of the original **BirdyFences** by HakanKokcu. All upstream work belongs to those authors — see [Credits](#-credits).
>
> **About the name:** *Kai* (改) is Japanese for a *revised / modified version* — fitting for a fork — and a nod to the fact that its enhancements were built with **AI** assistance.
>
> This fork adds a Windows Explorer–style **Details view**, **per-frame transparency**, global + per-frame **hotkeys**, **dark-mode menus**, themed chrome, and various **performance** fixes. See [**Fork Enhancements**](#-fork-enhancements).

---

## Why this fork exists

I put this together for my own use after **Fences 3 stopped being supported** on my setup, and because I wasn't a fan of the **current publisher's monetization model**. I wanted a free, open-source desktop-organizer that does what I need without the subscription/upsell direction, so I forked an existing MIT-licensed project and extended it to fill the gaps.

It's shared publicly in case it's useful to anyone in the same boat. It's a hobby project, provided as-is, with no warranty or support commitment.

## 🤖 A note on AI assistance

In the interest of transparency: the **fork-specific features in this repo were built with substantial help from [Claude](https://www.anthropic.com/claude) (Anthropic's AI assistant)** — used for writing, refactoring, and debugging the added code. I'm flagging this openly rather than hiding it. The upstream project is the work of its original human authors; the AI assistance applies to the changes made in *this* fork.

---

## About Desktop Frames +

Desktop Frames + creates **virtual frames** on your desktop, letting you group and organize icons cleanly. It supports:

- **Multiple frame types:** Data frames (custom shortcuts), Portal frames (live mirror of a folder, with navigation and filters), and Note frames (quick text).
- **Tabs, workspace profiles, and a smart-desktop auto-sort engine.**
- **Dynamic visibility:** peek-behind, roll-up, auto-hide, and focus mode.
- **Broad launch support:** files, folders, web links, Store apps, Steam games, Spotify URIs, run-as-admin / run-as-different-user.
- **Theming:** per-frame colours, global tint, and a wallpaper-matching "Chameleon" mode.

The original project is well documented upstream — see the [upstream manual](https://github.com/limbo666/DesktopFramesPlus/blob/main/desktop_frames_simple_manual.md) and [tips](https://github.com/limbo666/DesktopFramesPlus/blob/main/TIPS.md).

---

## 🔧 Fork Enhancements

Everything in this section is what this fork adds on top of the upstream project.

### Portal Details view
A Windows Explorer–style list view for Portal frames, as an alternative to the icon grid.
- **Toggle per frame:** right-click a Portal frame → **View → Icons / Details** (remembered per frame).
- **Columns:** Name, Date modified, Type, Size — resizable, with widths saved per frame.
- **Click-to-sort** with a **▲ / ▼** indicator; right-click **Sort by / Group by** with Explorer-style buckets (Today / Yesterday / Earlier this week, size ranges, …).
- **Native shell context menu** on right-click (Open with, Send to, cut/copy/paste, Properties, shell extensions), lazily loaded so the first right-click stays fast.
- **Zebra striping** (global default + per-frame override), and chrome (headers, scrollbars, selection) themed to the frame's colour.

### Icon (normal) view
- **Sort by** menu (Name / Date / Type / Size) with **ascending / descending**, plus a "Sorted by …" heading.
- Themed scrollbar to match the frame.

### Frames & title bar
- **Per-type title glyph** (folder / note / shortcut), tinted to match the other title-bar icons; Portal frames show their folder path on hover.
- **Rename Frame** from the context menu; hotkey shown in the title.
- **Per-frame transparency** override (Customize dialog) on top of the global Frame Tint.

### Hotkeys
- **Show / Hide all frames** (default `Ctrl + Alt + H`, customizable).
- **Per-frame focus** hotkey (press-to-capture; supports groups and the Windows key).
- Optional **double-click empty desktop** to toggle native desktop icons.

### Dark mode & theming
- **Dark-mode context menus** throughout (frame, icon, Notes editor, and the native shell menu) that follow the OS light/dark setting.

### Performance & footprint
- **Extension-based caching** of shell icons and type names for fast Portal loads.
- **Lazy context menus**, batched Portal reconciler, working-set trimming, and removal of unused startup work.

---

## Building from source

> Compatible with Windows 10/11 · fully portable

This fork is source-first (no prebuilt releases). It targets **.NET 8 (`net8.0-windows7.0`)** and, because of COM references, must be built with the **full MSBuild** from Visual Studio / Build Tools (not `dotnet build`):

1. Install **Visual Studio 2022** (or Build Tools) with the **.NET desktop** workload.
2. Build `Code/Desktop Frames/Desktop Frames.csproj` in `Release`.
3. Run `Desktop Frames.exe` from the output folder. Config files are created on first run in a user-writable location.

For the original, prebuilt tool, use the [upstream releases](https://github.com/limbo666/DesktopFramesPlus/releases).

---

## 📜 License

MIT — see [License.md](License.md). The MIT terms of the upstream project are retained.

## 🙏 Credits

- Original **BirdyFences** by **HakanKokcu**.
- **Desktop Frames +** upstream by **limbo666 / Nikos Georgousis** — please star and support the [upstream project](https://github.com/limbo666/DesktopFramesPlus).
- This personal fork is maintained by **superlanboy**, building on the above under the MIT License, with development help from Claude (see the AI note above).
