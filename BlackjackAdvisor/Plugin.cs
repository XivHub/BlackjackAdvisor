using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using BlackjackAdvisor.Windows;
using XivHubPluginKit.UI;

namespace BlackjackAdvisor
{
    public sealed class Plugin : IDalamudPlugin
    {
        public static string Name => "Blackjack Advisor";

        private const string commandName = "/bj";

        [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
        [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
        [PluginService] public static IChatGui ChatGui { get; private set; } = null!;
        [PluginService] public static IObjectTable ObjectTable { get; private set; } = null!;
        [PluginService] public static IPluginLog Logger { get; private set; } = null!;

        public Configuration Configuration { get; init; }

        /// <summary>Shared across every XIV Hub plugin; see XivHubPluginKit/UI/THEME.md.</summary>
        public static HubThemeConfigService ThemeConfig { get; private set; } = null!;

        public WindowSystem WindowSystem = new("BlackjackAdvisor");
        private readonly MainWindow mainWindow;

        public Plugin()
        {
            ECommonsMain.Init(PluginInterface, this);

            Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            Configuration.Initialize(PluginInterface);

            ThemeConfig = new HubThemeConfigService(
                PluginInterface.GetPluginConfigDirectory(),
                (msg, ex) => Logger.Warning(ex, msg));
            HubStyle.Init(ThemeConfig);

            mainWindow = new MainWindow(Configuration);
            WindowSystem.AddWindow(mainWindow);

            CommandManager.AddHandler(commandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open the Blackjack Advisor. '/bj parse' force-reads the last chat line."
            });

            PluginInterface.UiBuilder.Draw += DrawUI;
            PluginInterface.UiBuilder.OpenMainUi += ToggleUi;
            PluginInterface.UiBuilder.OpenConfigUi += ToggleUi;
        }

        public void Dispose()
        {
            PluginInterface.UiBuilder.Draw -= DrawUI;
            PluginInterface.UiBuilder.OpenMainUi -= ToggleUi;
            PluginInterface.UiBuilder.OpenConfigUi -= ToggleUi;

            WindowSystem.RemoveAllWindows();
            mainWindow.Dispose();
            CommandManager.RemoveHandler(commandName);

            ECommonsMain.Dispose();
        }

        private void OnCommand(string command, string args)
        {
            if (args.Trim().Equals("parse", StringComparison.OrdinalIgnoreCase))
            {
                mainWindow.ForceParseLast();
                return;
            }
            mainWindow.Toggle();
        }

        private void ToggleUi() => mainWindow.Toggle();

        /// <summary>
        /// One wrap point for the whole plugin: no window class knows the theme
        /// exists, and the pop is guaranteed even if a window throws mid-draw —
        /// ImGui's style stack is global, so an unbalanced push corrupts every
        /// plugin drawing after this one.
        /// </summary>
        private void DrawUI()
        {
            HubStyle.Push();
            try { WindowSystem.Draw(); }
            finally { HubStyle.Pop(); }
        }
    }
}
