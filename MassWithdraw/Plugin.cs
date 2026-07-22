using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using MassWithdraw.Windows;
using MassWithdraw;

namespace MassWithdraw;

public sealed partial class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;

    private const string CommandName = "/masswithdraw";

    public Configuration Configuration { get; init; }
    public readonly WindowSystem WindowSystem = new("MassWithdraw");

    private ConfigWindow ConfigWindow { get; init; }
    public MainWindow MainWindow { get; init; }
    private RetainerWatcher? retainerWatcher;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(Configuration, ToggleConfigUi);

        AddWindowCompat(ConfigWindow);
        AddWindowCompat(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the Mass Withdraw window.\n" +
            "/masswithdraw transfer → Triggers the transfer for the open retainer.\n" +
            "/masswithdraw config → Opens the configuration window.\n" +
            "/masswithdraw withdrawall → Withdraws from every retainer in turn.\n" +
            "/masswithdraw cancelall → Cancels an in-progress withdraw-all.\n" +
            "/masswithdraw filter list → Lists filter names and their state.\n" +
            "/masswithdraw filter clear → Clears all filters.\n" +
            "/masswithdraw filter <name> on|off|toggle → Sets or toggles one filter.",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        this.retainerWatcher = new RetainerWatcher(
            framework: Framework,
            isRetainerOpen: () => this.MainWindow.IsRetainerUIOpen() || this.MainWindow.IsRetainerListOpen(),
            setMainWindowOpen: open => this.MainWindow.IsOpen = open,
            isEnabled: () => this.Configuration.AutoOpenOnRetainer
        );

        RegisterIpc();

        Log.Information($"[MassWithdraw] Plugin initialized successfully. Ready for /masswithdraw command.");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        UnregisterIpc();

        RemoveAllWindowsCompat();

        ConfigWindow.Dispose();
        MainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);

        this.retainerWatcher?.Dispose();
        this.retainerWatcher = null;
    }

    private void OnCommand(string command, string args)
    {
        var a = (args ?? string.Empty).Trim();

        if (a.Length == 0)
        {
            if (!this.MainWindow.IsRetainerUIOpen() && !this.MainWindow.IsRetainerListOpen())
            {
                Plugin.ChatGui.PrintError("[MassWithdraw] Open your Retainer’s inventory window first.");
                return;
            }

            this.MainWindow.IsOpen = true;
            return;
        }

        if (a.StartsWith("transfer", StringComparison.OrdinalIgnoreCase))
        {
            this.MainWindow.StartTransferFromCommand();
            return;
        }
        if (a.StartsWith("config", StringComparison.OrdinalIgnoreCase))
        {
            this.ToggleConfigUi();
            return;
        }
        if (a.StartsWith("withdrawall", StringComparison.OrdinalIgnoreCase))
        {
            this.MainWindow.StartWithdrawAllRetainers();
            return;
        }
        if (a.StartsWith("cancelall", StringComparison.OrdinalIgnoreCase))
        {
            if (!this.MainWindow.CancelBatch())
                Plugin.ChatGui.Print("[MassWithdraw] No withdraw-all batch is running.");
            return;
        }
        if (a.StartsWith("filter", StringComparison.OrdinalIgnoreCase))
        {
            HandleFilterCommand(a.Substring("filter".Length).Trim());
            return;
        }

        Plugin.ChatGui.Print("[MassWithdraw] Unknown subcommand. Available options:");
        Plugin.ChatGui.Print("[MassWithdraw] /masswithdraw transfer               → Trigger the mass withdraw transfer if possible");
        Plugin.ChatGui.Print("[MassWithdraw] /masswithdraw config                 → Open the configuration window");
        Plugin.ChatGui.Print("[MassWithdraw] /masswithdraw withdrawall            → Withdraw from every retainer in turn");
        Plugin.ChatGui.Print("[MassWithdraw] /masswithdraw cancelall              → Cancel an in-progress withdraw-all");
        Plugin.ChatGui.Print("[MassWithdraw] /masswithdraw filter list            → List filter names and their state");
        Plugin.ChatGui.Print("[MassWithdraw] /masswithdraw filter clear           → Clear all filters (withdraw everything)");
        Plugin.ChatGui.Print($"[MassWithdraw] /masswithdraw filter <name> <on|off|toggle> → e.g. \"filter {MainWindow.FilterNames.First()} toggle\"");
    }

    private void HandleFilterCommand(string rest)
    {
        if (rest.Length == 0 || rest.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            var states = MainWindow.FilterNames.Select(name =>
            {
                MainWindow.TryGetFilterEnabled(name, out var enabled);
                return $"{name}={(enabled ? "on" : "off")}";
            });
            Plugin.ChatGui.Print($"[MassWithdraw] Filters: {string.Join(", ", states)}");
            return;
        }

        if (rest.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            this.MainWindow.ClearFilters();
            Plugin.ChatGui.Print("[MassWithdraw] Filters cleared.");
            return;
        }

        var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var name = parts[0];
        var mode = parts.Length > 1 ? parts[1].Trim() : "toggle";

        bool? desired = mode.ToLowerInvariant() switch
        {
            "on" or "true" or "enable" or "enabled"     => true,
            "off" or "false" or "disable" or "disabled" => false,
            _                                            => null,
        };

        bool ok;
        bool newState;
        if (desired.HasValue)
        {
            ok = this.MainWindow.TrySetFilterEnabled(name, desired.Value);
            newState = desired.Value;
        }
        else
        {
            ok = this.MainWindow.TryToggleFilter(name, out newState);
        }

        if (!ok)
        {
            Plugin.ChatGui.PrintError($"[MassWithdraw] Unknown filter \"{name}\". Valid names: {string.Join(", ", MainWindow.FilterNames)}");
            return;
        }

        Plugin.ChatGui.Print($"[MassWithdraw] {name} filter {(newState ? "enabled" : "disabled")}.");
    }
    
    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();

    /*
     * WindowSystem.AddWindow/RemoveAllWindows are invoked via reflection so this build
     * keeps loading against Dalamud API revisions where the WindowSystem binding shifts underneath it.
     */
    private void AddWindowCompat(Window window)
    {
        var method = typeof(WindowSystem).GetMethod("AddWindow", new[] { typeof(Window) })
            ?? throw new MissingMethodException(typeof(WindowSystem).FullName, "AddWindow");
        method.Invoke(WindowSystem, new object[] { window });
    }

    private void RemoveAllWindowsCompat()
    {
        typeof(WindowSystem).GetMethod("RemoveAllWindows", Type.EmptyTypes)?.Invoke(WindowSystem, Array.Empty<object>());
    }
}