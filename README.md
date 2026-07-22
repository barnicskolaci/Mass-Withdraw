# MassWithdraw

**Mass Withdraw** is a lightweight, fast retainer inventory tool for Final Fantasy XIV.
It lets you instantly withdraw all items from your current retainer straight into your bags.

---

## ✨ Features

- **Smart Preview** — shows how many items will move, how much bag space you have, and an ETA.
- **Automatic Transfers** — safely moves all eligible items in sequence.
- **Unique-Item Protection** — skips items you already own.
- **Filter System** — choose which item types to withdraw, with quick toggles in the retainer list.
- **Withdraw All Retainers** — batch process all retainers at once, with gear automatically routed to the Armoury Chest when there's room.
- **Auto-Anchor Window** — the plugin window stays neatly aligned next to the Retainer Inventory.
- **Fast & Lightweight** — no lag, no setup, just open and withdraw.
- **Plugin Integration** — expose filters and batch operations via IPC for external scripts and plugins.

---

## 🔧 Commands

| Command | Description |
|----------|-------------|
| `/masswithdraw config` | Open the config window. |
| `/masswithdraw transfer` | Immediately starts a transfer if possible (prints a message if not). |
| `/masswithdraw filter` | Manage item type filters. |
| `/masswithdraw withdrawall` | Start the batch "Withdraw All Retainers" operation. |
| `/masswithdraw cancelall` | Cancel any running batch operation. |

---

## 🔌 IPC Integration

Other plugins can control **Mass Withdraw** via IPC channels (prefix: `MassWithdraw.`):

| Channel | Signature | Effect |
|---------|-----------|--------|
| `SetFilter` | `(string name, bool enabled) → bool` | Sets a filter; `false` return = unrecognized name |
| `GetFilter` | `(string name) → bool` | Reads a filter's state |
| `ToggleFilter` | `(string name) → bool` | Flips a filter, returns the new state |
| `ClearFilters` | `() → void` | Clears all filters |
| `GetFilterNames` | `() → string[]` | The 6 canonical filter names |
| `StartWithdrawAll` | `() → bool` | Starts the batch; `false` if already running / no retainers |
| `CancelWithdrawAll` | `() → bool` | Cancels the batch; `false` if nothing was running |
| `IsWithdrawAllRunning` | `() → bool` | Whether the batch is currently active |

---

## 🪙 Installation

1. In-game, open `/xlsettings` → Developer → Custom Plugin Repositories
2. Add the following URL: https://kanww.github.io/Mass-Withdraw/repo.json
3. Click Save, then open `/xlplugins`.
4. Search **Mass Withdraw** and install.

---

## 🪄 License

See the [LICENSE.md](LICENSE.md) file for full text.

---

## ❤️ Credits

- Dalamud API — [goatcorp/Dalamud](https://github.com/goatcorp/Dalamud)  
- FFXIVClientStructs — [aers/FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs)  
