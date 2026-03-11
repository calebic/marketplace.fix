# BeamNG Marketplace Config Editor

A **WPF + MahApps desktop tool** for fixing and preparing mod vehicle configs so they appear correctly in the **BeamNG Career Marketplace**.

---

# 📥 Download

Get the latest version from the releases page:

👉 https://github.com/calebic/marketplace.fix/releases

---

# 🚗 What This Tool Does

The program scans your BeamNG mods and helps ensure vehicle configs contain the fields required by the **Career Marketplace system**.

It will:

* Scan your `mods` folder
* Support **both unpacked mods and zip mods**
* Detect vehicle `info_*.json` configs
* Highlight missing marketplace fields
* Provide a fast editor to fix and save values

This helps improve **marketplace visibility and compatibility** for modded vehicles.

---

# ⭐ Core Features

* 🔎 **Search, filtering, and grouping** for large mod lists
* ⚠️ **Missing field highlighting** for faster fixing
* ⚡ **Auto-Fill system** with configurable defaults
* 🛡 **Insurance Class dropdown**

  * `dailyDriver`
  * `commercial`
  * `prestige`
* 🌎 Automatically sets **Region = `northAmerica`** for marketplace compatibility
* 💾 Optional **Backup before save** (`.bak`)
* 🔁 Optional **Input into Vehicles mirror mode**
* 📖 Built-in **Help popup** with:

  * quick start
  * option reference
  * field guide
  * keybinds
  * troubleshooting
* 🌙 **Dark / Light mode toggle** with persistent settings

---

# 🔁 Input into `vehicles` (Optional)

When enabled, saving a config will also mirror required files to:

```
BeamNG.drive/vehicles/<model>/
```

Mirrored files include:

* `info_<config>.json`
* matching `<config>.pc` (if present)
* matching preview images (`.jpg`, `.jpeg`, `.png`, `.webp`)
* `default` preview image(s) if present

This can help when a config **still doesn’t appear in the marketplace after a normal mod save**.

---

# 🔄 Marketplace Refresh Command

If you want to refresh the marketplace instantly while the game is running, open the console and run:

```lua
career_modules_vehicleShopping.invalidateVehicleCache()
career_modules_vehicleShopping.updateVehicleList(true)
```

---

# 💰 Pricing Notes

If a vehicle still appears very expensive, the marketplace also factors in:

* vehicle **age**
* vehicle **mileage**
* dealer **range / multipliers**
* **part value**

So even if the base **Value** in the config is lower, the final listing price can still be higher.

---

# 📊 Population Guide

`Population` is a **weight value**, not a fixed count.

Higher numbers increase the chance that a vehicle appears in dealer inventory.

### Suggested Ranges

| Category        | Population     |
| --------------- | -------------- |
| Ultra rare      | 1 – 50         |
| Rare            | 50 – 200       |
| Uncommon        | 200 – 800      |
| Common          | 800 – 3,000    |
| Very common     | 3,000 – 10,000 |
| Floods the list | 10,000+        |

### Examples

* Everyday vehicles → **1,000 – 3,000**
* Exotic / special trims → **50 – 300**
* Low-demand / junkyard variants → **100 – 500**

---

# 💬 Support

If you have questions or suggestions:

Discord: **ic.ey**
