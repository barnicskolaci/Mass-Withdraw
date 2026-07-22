using System.Linq;
using Dalamud.Plugin.Ipc;

namespace MassWithdraw;

public sealed partial class Plugin
{
    // IPC channel names are "<InternalName>.<Method>", the standard Dalamud convention —
    // other plugins/scripts subscribe with PluginInterface.GetIpcSubscriber<...>(name).
    private ICallGateProvider<string, bool, bool>? ipcSetFilter;
    private ICallGateProvider<string, bool>? ipcGetFilter;
    private ICallGateProvider<string, bool>? ipcToggleFilter;
    private ICallGateProvider<object>? ipcClearFilters;
    private ICallGateProvider<string[]>? ipcGetFilterNames;
    private ICallGateProvider<bool>? ipcStartWithdrawAll;
    private ICallGateProvider<bool>? ipcCancelWithdrawAll;
    private ICallGateProvider<bool>? ipcIsWithdrawAllRunning;

    private void RegisterIpc()
    {
        ipcSetFilter = PluginInterface.GetIpcProvider<string, bool, bool>("MassWithdraw.SetFilter");
        ipcSetFilter.RegisterFunc((name, enabled) => MainWindow.TrySetFilterEnabled(name, enabled));

        ipcGetFilter = PluginInterface.GetIpcProvider<string, bool>("MassWithdraw.GetFilter");
        ipcGetFilter.RegisterFunc(name =>
        {
            MainWindow.TryGetFilterEnabled(name, out var enabled);
            return enabled;
        });

        ipcToggleFilter = PluginInterface.GetIpcProvider<string, bool>("MassWithdraw.ToggleFilter");
        ipcToggleFilter.RegisterFunc(name =>
        {
            MainWindow.TryToggleFilter(name, out var newState);
            return newState;
        });

        ipcClearFilters = PluginInterface.GetIpcProvider<object>("MassWithdraw.ClearFilters");
        ipcClearFilters.RegisterAction(() => MainWindow.ClearFilters());

        ipcGetFilterNames = PluginInterface.GetIpcProvider<string[]>("MassWithdraw.GetFilterNames");
        ipcGetFilterNames.RegisterFunc(() => MainWindow.FilterNames.ToArray());

        ipcStartWithdrawAll = PluginInterface.GetIpcProvider<bool>("MassWithdraw.StartWithdrawAll");
        ipcStartWithdrawAll.RegisterFunc(() => MainWindow.StartWithdrawAllRetainers());

        ipcCancelWithdrawAll = PluginInterface.GetIpcProvider<bool>("MassWithdraw.CancelWithdrawAll");
        ipcCancelWithdrawAll.RegisterFunc(() => MainWindow.CancelBatch());

        ipcIsWithdrawAllRunning = PluginInterface.GetIpcProvider<bool>("MassWithdraw.IsWithdrawAllRunning");
        ipcIsWithdrawAllRunning.RegisterFunc(() => MainWindow.IsBatchRunning);
    }

    private void UnregisterIpc()
    {
        ipcSetFilter?.UnregisterFunc();
        ipcGetFilter?.UnregisterFunc();
        ipcToggleFilter?.UnregisterFunc();
        ipcClearFilters?.UnregisterAction();
        ipcGetFilterNames?.UnregisterFunc();
        ipcStartWithdrawAll?.UnregisterFunc();
        ipcCancelWithdrawAll?.UnregisterFunc();
        ipcIsWithdrawAllRunning?.UnregisterFunc();
    }
}
