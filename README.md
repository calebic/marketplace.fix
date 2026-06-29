<div align="center">

# 🏁 BeamNG Marketplace Config Editor

### Make your modded vehicles actually show up in Career Mode.

A sleek desktop tool for **BeamNG.drive** players running the **RLS mod**. Some modded vehicles ship without the marketplace metadata Career Mode needs to list them — this editor finds the gaps, flags them, and lets you fix them in seconds.

[![Download](https://img.shields.io/badge/⬇_Download-Latest_Release-22D3EE?style=for-the-badge)](../../releases/latest)
[![Platform](https://img.shields.io/badge/Windows_10_/_11-2DD4BF?style=for-the-badge)](#)
[![Discord](https://img.shields.io/badge/Discord-@ic.ey-8B5CF6?style=for-the-badge)](#-support)

</div>

---

## 📸 Screenshots

<!-- Drop your images in a /docs or /screenshots folder and update these paths -->

<div align="center">

| Config list & editor | Slide-in settings |
| :---: | :---: |
| ![Main window](https://i.imgur.com/gW5RcgX.png) | ![Settings panel](https://i.imgur.com/34HZoo6.png) |

</div>

> _Replace these with your own captures — the main window and the slide-in panel show off the neon theme nicely._

---

## ✨ Why use it

Modded cars often won't appear in the RLS Career marketplace because they're missing a few pieces of metadata — brand, value, population, and the like. Hunting those down by hand across dozens of config files is tedious. This tool does it for you:

- **🔍 One-click scan** — point it at your mods folder and it finds every config
- **🚦 Smart gap detection** — instantly see which configs are *Ready* and which need attention
- **⚡ Fast on big packs** — built to load and scroll smoothly even with 100+ configs
- **🪄 Auto-fill defaults** — set your common values once, apply them with a click
- **🎚️ Population presets** — dial in how often a vehicle appears, from ultra-rare to everywhere
- **🪞 Vehicles mirroring** — optional sync into your vehicles folder for stubborn cases
- **💾 Backup before save** — never lose a working config
- **🌗 Light & dark themes** — neon-soaked dark mode by default

---

## 🚀 Getting started

1. **Download** the latest `.exe` from the [Releases page](https://github.com/calebic/marketplace.fix/releases/tag/RENEWED)
2. **Run it** — no install needed, just double-click _(see note below)_
3. **Browse** to your BeamNG mods folder and click **Scan**
4. **Fix** any flagged configs, set Insurance Class / Year / Value / Population, and hit **Save**
5. **Launch BeamNG** — your vehicles now show up in the Career marketplace

> 💡 If a vehicle still won't appear, re-save with **Input into 'Vehicles'** enabled in Settings.

> 🛡️ **First launch:** Windows SmartScreen may warn because the app isn't code-signed. Click **More info → Run anyway**. The first start is also a beat slower while the app unpacks — this is normal and only happens once.

---

## 🧭 How it works

The tool scans your mods for vehicle info files and `.pc` configs, then highlights any missing marketplace fields. Fill them in, save, and the corrected values let the vehicle surface in Career Mode.

**A few things worth knowing:**

- **Population** is a *weight*, not a fixed count — higher means a vehicle is more likely to appear when the dealership pool is generated. Presets are shortcuts; **Custom** uses your exact number.
- **Insurance Class** accepts `dailyDriver`, `commercial`, or `prestige`.
- **Region** is automatically set to `northAmerica` on save.

### Population guide

| Tier | Range | Feel |
| :--- | :--- | :--- |
| Ultra-rare | 1–50 | Barely ever |
| Rare | 50–200 | Special find |
| Uncommon | 200–800 | Occasional |
| Common | 800–3,000 | Everyday car |
| Very common | 3,000–10,000 | Shows up a lot |
| Floods the list | 10,000+ | Always present |

> A normal daily that appears often without crowding everything out sits comfortably around **1,000–3,000**.

---

## 💻 Requirements

- **Windows 10 or 11** (64-bit)
- That's it — the release `.exe` is self-contained and needs **no .NET install**

---

## 🛠️ Building from source

Want to build it yourself? You'll need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows.

```bash
# Produces a single self-contained .exe
dotnet publish -p:PublishProfile=SingleFile
```

The finished file lands in `bin\Release\net8.0-windows\win-x64\publish\`. See [`BUILD.md`](BUILD.md) for Visual Studio steps and the one-click `Build-Release.bat`.

**Built with:** C# · .NET 8 · WPF · [MahApps.Metro](https://mahapps.com/)

---

## 💬 Support

Questions, bugs, or ideas? Find me on **Discord: `@ic.ey`** — send a friend request and I'm happy to help.

Always looking for new tools and add-ons to make Career Mode better, so suggestions are welcome.

---

<div align="center">

Made for the BeamNG Career Mode community 🏎️

</div>
