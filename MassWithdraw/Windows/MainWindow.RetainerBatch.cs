using System;
using System.Collections.Generic;
using Dalamud.Memory;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GameStructs = FFXIVClientStructs.FFXIV.Client.Game;

namespace MassWithdraw.Windows;

public partial class MainWindow
{
#region Batch State
    private enum BatchPhase
    {
        Idle,
        SelectingRetainer,
        WaitingForSelectString,
        WaitingForRetainerBag,
        TransferRunning,
        ClosingBag,
        WaitingForRetainerMenu,
        ClosingRetainerMenu,
        WaitingForListReturn,
    }

    // Text of the "Entrust or withdraw items."/"Quit." SelectString entries, read once
    // from the Addon Excel sheet so these match the player's actual client language.
    private const uint EntrustOrWithdrawItemsAddonRowId = 2378;
    private const uint QuitAddonRowId = 2383;
    private static string? entrustOrWithdrawItemsLabel;
    private static string? quitLabel;

    private BatchPhase batchPhase = BatchPhase.Idle;
    private int batchRetainerIndex;
    private int batchRetainerCount;
    private int batchRetainersProcessed;
    private DateTime batchPhaseStartedAtUtc;
    private DateTime batchNextActionAtUtc;

    // Minimum gap between two synthetic UI actions on different addons. A previous
    // crash investigation (back-to-back FireCallback/ReceiveEvent with no delay) found
    // that firing a second synthetic UI event before the first one's addon transition
    // settles can crash the game's native callback dispatch, so every phase here waits
    // at least one throttle interval before acting again.
    private static readonly TimeSpan BatchActionThrottle = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan BatchPhaseTimeout = TimeSpan.FromSeconds(8);

    // If the SelectString menu is fully loaded and populated but nothing matched the
    // expected label for this long, stop waiting for a text match and just click the
    // first entry — "Entrust or withdraw items." is always the first option, so this
    // is a safe fallback rather than sitting idle for the full BatchPhaseTimeout.
    private static readonly TimeSpan SelectStringFallbackGrace = TimeSpan.FromSeconds(2);

    internal bool IsBatchRunning => batchPhase != BatchPhase.Idle;
#endregion

#region Entry points
    /**
     * * Starts withdrawing from every retainer in turn
     * <return type="bool">True if the batch actually started; false if one was already running, retainer data wasn't ready, or there are no retainers</return>
     */
    public unsafe bool StartWithdrawAllRetainers()
    {
        if (IsBatchRunning)
            return false;

        var manager = GameStructs.RetainerManager.Instance();
        if (manager == null)
        {
            Plugin.ChatGui.PrintError("[MassWithdraw] Retainer data isn't ready yet.");
            return false;
        }

        var count = manager->GetRetainerCount();
        if (count <= 0)
        {
            Plugin.ChatGui.PrintError("[MassWithdraw] No retainers found.");
            return false;
        }

        batchRetainerIndex = 0;
        batchRetainersProcessed = 0;
        batchRetainerCount = count;
        EnterBatchPhase(BatchPhase.SelectingRetainer);

        Plugin.ChatGui.Print($"[MassWithdraw] Withdrawing from {count} retainer(s)…");
        return true;
    }

    /**
     * * Cancels an in-progress "withdraw all retainers" batch
     * <return type="bool">True if a batch was actually running and got cancelled; otherwise, false</return>
     */
    public bool CancelBatch()
    {
        if (!IsBatchRunning)
            return false;

        if (transferSession.Running)
            cancellationTokenSource?.Cancel();

        AbortBatch("Cancelled.");
        return true;
    }
#endregion

#region State machine
    private void EnterBatchPhase(BatchPhase phase)
    {
        batchPhase = phase;
        batchPhaseStartedAtUtc = DateTime.UtcNow;
    }

    private void AbortBatch(string reason)
    {
        Plugin.ChatGui.PrintError(
            $"[MassWithdraw] Withdraw-all stopped: {reason} ({batchRetainersProcessed}/{batchRetainerCount} retainer(s) done.)");
        batchPhase = BatchPhase.Idle;
    }

    private void CheckBatchTimeout(DateTime now, string message)
    {
        if (now - batchPhaseStartedAtUtc > BatchPhaseTimeout)
            AbortBatch(message);
    }

    private unsafe void AdvanceBatch()
    {
        if (!IsBatchRunning)
            return;

        var now = DateTime.UtcNow;
        if (now < batchNextActionAtUtc)
            return;

        switch (batchPhase)
        {
            case BatchPhase.SelectingRetainer:
                if (batchRetainerIndex >= batchRetainerCount)
                {
                    Plugin.ChatGui.Print($"[MassWithdraw] Finished withdrawing from {batchRetainersProcessed} retainer(s).");
                    batchPhase = BatchPhase.Idle;
                    return;
                }

                if (!TryGetAddon("RetainerList", out var retainerList))
                {
                    CheckBatchTimeout(now, "the retainer list closed unexpectedly.");
                    return;
                }

                FireCallback(retainerList, true, 2, (uint)batchRetainerIndex, null, null);
                batchNextActionAtUtc = now + BatchActionThrottle;
                EnterBatchPhase(BatchPhase.WaitingForSelectString);
                return;

            case BatchPhase.WaitingForSelectString:
                var allowFallback = now - batchPhaseStartedAtUtc > SelectStringFallbackGrace;
                if (TrySelectEntrustItemsEntry(allowFallback))
                {
                    batchNextActionAtUtc = now + BatchActionThrottle;
                    EnterBatchPhase(BatchPhase.WaitingForRetainerBag);
                    return;
                }
                CheckBatchTimeout(now,
                    "a retainer didn't show the expected \"Entrust or withdraw items.\" option (a pending venture report may be blocking it) — check that retainer manually.");
                return;

            case BatchPhase.WaitingForRetainerBag:
                if (IsRetainerUIOpen())
                {
                    StartTransfer(transferDelayMs);
                    batchRetainersProcessed++;
                    EnterBatchPhase(transferSession.Running ? BatchPhase.TransferRunning : BatchPhase.ClosingBag);
                    return;
                }
                CheckBatchTimeout(now, "the retainer's inventory never opened.");
                return;

            case BatchPhase.TransferRunning:
                if (!transferSession.Running)
                    EnterBatchPhase(BatchPhase.ClosingBag);
                return;

            case BatchPhase.ClosingBag:
                if (TryGetAddon("InventoryRetainer", out var bag) || TryGetAddon("InventoryRetainerLarge", out bag))
                {
                    bag->Close(true);
                    batchNextActionAtUtc = now + BatchActionThrottle;
                }
                EnterBatchPhase(BatchPhase.WaitingForRetainerMenu);
                return;

            case BatchPhase.WaitingForRetainerMenu:
                // Closing the bag returns to that retainer's own SelectString menu, not
                // straight back to RetainerList — but accept landing on RetainerList
                // directly too, in case some state skips the menu.
                if (TryGetAddon("RetainerList", out _))
                {
                    batchRetainerIndex++;
                    EnterBatchPhase(BatchPhase.SelectingRetainer);
                    return;
                }
                if (TryGetAddon("SelectString", out _))
                {
                    EnterBatchPhase(BatchPhase.ClosingRetainerMenu);
                    return;
                }
                CheckBatchTimeout(now, "didn't return to the retainer menu after closing its inventory.");
                return;

            case BatchPhase.ClosingRetainerMenu:
                if (TrySelectQuitEntry())
                {
                    batchNextActionAtUtc = now + BatchActionThrottle;
                    EnterBatchPhase(BatchPhase.WaitingForListReturn);
                    return;
                }
                CheckBatchTimeout(now, "couldn't find the \"Quit.\" option on the retainer menu.");
                return;

            case BatchPhase.WaitingForListReturn:
                if (TryGetAddon("RetainerList", out _))
                {
                    batchRetainerIndex++;
                    EnterBatchPhase(BatchPhase.SelectingRetainer);
                    return;
                }
                CheckBatchTimeout(now, "didn't return to the retainer list.");
                return;
        }
    }
#endregion

#region Raw addon interaction
    // Matches AutoRetainer/ECommons' GenericHelpers.IsAddonReady: IsVisible alone can be
    // true for a frame or more before the addon's node list/component data (e.g. a
    // SelectString's entries) is actually populated, so require full-load too.
    private static unsafe bool TryGetAddon(string name, out AtkUnitBase* unit)
    {
        unit = null;
        for (var rootIndex = 0; rootIndex < 2; rootIndex++)
        {
            var addon = Plugin.GameGui.GetAddonByName(name, rootIndex);
            if (addon == null || addon.Address == nint.Zero)
                continue;

            var u = (AtkUnitBase*)addon.Address;
            if (u == null || !u->IsVisible || u->UldManager.LoadedState != AtkLoadState.Loaded || !u->IsFullyLoaded())
                continue;

            unit = u;
            return true;
        }
        return false;
    }

    // Mirrors the well-tested Callback.Fire helper other retainer-automation plugins use:
    // builds an AtkValue[] from the given values and fires it at the addon's own callback.
    // `null` entries become an untyped/zero AtkValue (matching the reserved slots the game
    // expects in fixed-shape callbacks like the RetainerList row-select one below).
    private static unsafe void FireCallback(AtkUnitBase* unit, bool updateState, params object?[] values)
    {
        var atkValues = stackalloc AtkValue[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            atkValues[i] = values[i] switch
            {
                int iv => new AtkValue { Type = AtkValueType.Int, Int = iv },
                uint uv => new AtkValue { Type = AtkValueType.UInt, UInt = uv },
                _ => new AtkValue { Type = AtkValueType.Undefined },
            };
        }
        unit->FireCallback((uint)values.Length, atkValues, updateState);
    }

    private static unsafe bool TrySelectEntrustItemsEntry(bool allowIndexFallback)
    {
        if (!TryGetAddon("SelectString", out var addon))
            return false;

        var selectString = (AddonSelectString*)addon;
        var entryCount = selectString->PopupMenu.PopupMenu.EntryCount;
        if (entryCount <= 0)
            return false;

        var index = FindSelectStringEntryIndex(selectString, entryCount, GetEntrustOrWithdrawItemsLabel());
        if (index >= 0)
        {
            FireCallback(addon, true, index);
            return true;
        }

        Plugin.Log.Warning(
            $"[MassWithdraw] Withdraw-all: no SelectString entry matched \"{GetEntrustOrWithdrawItemsLabel()}\". Entries: {DescribeEntries(selectString, entryCount)}");

        if (!allowIndexFallback)
            return false;

        Plugin.Log.Warning("[MassWithdraw] Withdraw-all: falling back to the menu's first entry.");
        FireCallback(addon, true, 0);
        return true;
    }

    // Dismisses a retainer's own SelectString menu (the one with "Entrust or withdraw
    // items."/etc) by clicking "Quit.", the same way closing out of it manually would —
    // no index fallback here, since guessing wrong could trigger an unintended action
    // (e.g. reassigning a venture) instead of just backing out.
    private static unsafe bool TrySelectQuitEntry()
    {
        if (!TryGetAddon("SelectString", out var addon))
            return false;

        var selectString = (AddonSelectString*)addon;
        var entryCount = selectString->PopupMenu.PopupMenu.EntryCount;
        if (entryCount <= 0)
            return false;

        var index = FindSelectStringEntryIndex(selectString, entryCount, GetQuitLabel());
        if (index < 0)
        {
            Plugin.Log.Warning(
                $"[MassWithdraw] Withdraw-all: no SelectString entry matched \"{GetQuitLabel()}\". Entries: {DescribeEntries(selectString, entryCount)}");
            return false;
        }

        FireCallback(addon, true, index);
        return true;
    }

    private static unsafe int FindSelectStringEntryIndex(AddonSelectString* selectString, int entryCount, string label)
    {
        for (var i = 0; i < entryCount; i++)
        {
            var entryPtr = selectString->PopupMenu.PopupMenu.EntryNames[i].Value;
            if (entryPtr == null)
                continue;

            var text = MemoryHelper.ReadSeStringNullTerminated((nint)entryPtr).TextValue;
            if (text.StartsWith(label, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static unsafe string DescribeEntries(AddonSelectString* selectString, int entryCount)
    {
        var parts = new List<string>();
        for (var i = 0; i < entryCount; i++)
        {
            var ptr = selectString->PopupMenu.PopupMenu.EntryNames[i].Value;
            parts.Add(ptr == null
                ? $"[{i}]=<null>"
                : $"[{i}]=\"{MemoryHelper.ReadSeStringNullTerminated((nint)ptr).TextValue}\"");
        }
        return string.Join(", ", parts);
    }

    private static string GetEntrustOrWithdrawItemsLabel()
    {
        if (entrustOrWithdrawItemsLabel != null)
            return entrustOrWithdrawItemsLabel;

        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Addon>();
        entrustOrWithdrawItemsLabel = sheet?.GetRow(EntrustOrWithdrawItemsAddonRowId).Text.ToDalamudString().TextValue
            ?? "Entrust or withdraw items.";
        return entrustOrWithdrawItemsLabel;
    }

    private static string GetQuitLabel()
    {
        if (quitLabel != null)
            return quitLabel;

        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Addon>();
        quitLabel = sheet?.GetRow(QuitAddonRowId).Text.ToDalamudString().TextValue ?? "Quit.";
        return quitLabel;
    }
#endregion
}
