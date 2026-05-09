using System;
using System.Reflection;

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

using UnityEngine;

namespace CcjRelay {
    [BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
    public class CcjRelayPlugin : BaseUnityPlugin {
        public static ManualLogSource L;
        public static ConfigEntry<bool>   RelayEnabled;
        public static ConfigEntry<bool>   VerboseLogging;
        public static ConfigEntry<bool>   HudEnabled;
        public static ConfigEntry<KeyCode> HudToggleKey;

        void Awake() {
            L = Logger;
            L.LogInfo($"{PluginInfo.Name} v{PluginInfo.Version}: Awake()");

            RelayEnabled = Config.Bind(
                "Relay", "Enabled", true,
                "When this game hosts a match, run a userspace UDP forwarder that " +
                "bridges UNet to the configured relay server. The relay endpoint is " +
                "taken from UNetTransport.ConnectAddress/ConnectPort, which the " +
                "matchmaker fills in via XRPC game.matchMake response " +
                "(globalip/globalport). Clients can join through the relay without " +
                "needing this plugin.");

            VerboseLogging = Config.Bind(
                "Diagnostics", "VerboseLogging", false,
                "Log every relay packet forward/return. Very chatty; only enable for debugging.");

            HudEnabled = Config.Bind(
                "HUD", "Enabled", true,
                "Spawn the in-game IMGUI debug HUD. Press the toggle key to show it.");

            HudToggleKey = Config.Bind(
                "HUD", "ToggleKey", KeyCode.F8,
                "Key that toggles the HUD overlay.");

            try {
                new Harmony(PluginInfo.Guid).PatchAll();
                L.LogInfo("Harmony patches applied.");
            }
            catch (Exception e) {
                L.LogError($"Harmony PatchAll threw: {e}");
            }

            if (HudEnabled.Value) {
                try {
                    var hudHost = new GameObject($"{PluginInfo.Name}.HUD");
                    DontDestroyOnLoad(hudHost);
                    var hud = hudHost.AddComponent<RelayHud>();
                    hud.ToggleKey = HudToggleKey.Value;
                    L.LogEvent += (_, ev) => {
                        var instance = RelayHud.Instance;
                        if (instance == null) return;
                        instance.Append(ev.Level, ev.Data?.ToString() ?? "");
                    };
                    L.LogInfo($"HUD spawned (toggle key: {HudToggleKey.Value})");
                }
                catch (Exception e) {
                    L.LogError($"HUD spawn failed: {e}");
                }
            }

            L.LogInfo($"{PluginInfo.Name} v{PluginInfo.Version}: ready  relay={RelayEnabled.Value} verbose={VerboseLogging.Value} hud={HudEnabled.Value}");
        }
    }

    static class GameTypes {
        public const string UNetTransport               = "MLAPI.Transports.UNET.UNetTransport";
        public const string NetworkManager              = "MLAPI.NetworkManager";
        public const string RelayTransport              = "MLAPI.Transports.UNET.RelayTransport";
        public const string HostTopology                = "UnityEngine.Networking.HostTopology";
        public const string NetworkingModulesControler  = "NetworkingModulesControler";
        public const string NetworkingModulesUseCase    = "NetworkingModulesUseCase";
        public const string NetworkingModulesDataAccess = "NetworkingModulesDataAccess";

        public static Type Resolve(string name) =>
            AccessTools.TypeByName(name)
                ?? throw new InvalidOperationException($"Type not loaded: {name}");

        public static MethodBase Method(string typeName, string methodName, Type[] paramTypes = null) =>
            AccessTools.Method(Resolve(typeName), methodName, paramTypes);
    }

    [HarmonyPatch]
    static class P_StartServer_Forwarder {
        static MethodBase TargetMethod() => GameTypes.Method(GameTypes.UNetTransport, "StartServer");
        [HarmonyPostfix]
        static void Postfix(object __instance) {
            if (!CcjRelayPlugin.RelayEnabled.Value) return;
            try {
                HostForwarder.Start(
                    relayHost:     UNetField.GetString(__instance, "ConnectAddress"),
                    relayPort:     UNetField.GetInt(__instance, "ConnectPort"),
                    unetLocalPort: UNetField.GetInt(__instance, "ServerListenPort"));
            }
            catch (Exception e) {
                CcjRelayPlugin.L.LogError($"P_StartServer postfix: {e}");
            }
        }
    }

    [HarmonyPatch]
    static class P_StartClient_Watcher {
        static MethodBase TargetMethod() => GameTypes.Method(GameTypes.UNetTransport, "StartClient");
        [HarmonyPostfix]
        static void Postfix(object __instance) {
            if (!CcjRelayPlugin.RelayEnabled.Value) return;
            try {
                HostForwarder.NoteClientMode(
                    UNetField.GetString(__instance, "ConnectAddress"),
                    UNetField.GetInt(__instance, "ConnectPort"));
            }
            catch (Exception e) {
                CcjRelayPlugin.L.LogError($"P_StartClient_Watcher postfix: {e}");
            }
        }
    }

    [HarmonyPatch]
    static class P_Shutdown_Forwarder {
        static MethodBase TargetMethod() => GameTypes.Method(GameTypes.UNetTransport, "Shutdown");
        [HarmonyPostfix]
        static void Postfix() {
            if (!CcjRelayPlugin.RelayEnabled.Value) return;
            try { HostForwarder.Stop(); }
            catch (Exception e) {
                CcjRelayPlugin.L.LogError($"P_Shutdown postfix: {e}");
            }
        }
    }

    static class UNetField {
        public static string GetString(object inst, string name) =>
            (string)AccessTools.Field(inst.GetType(), name).GetValue(inst);
        public static int GetInt(object inst, string name) =>
            (int)AccessTools.Field(inst.GetType(), name).GetValue(inst);
        public static bool GetBool(object inst, string name) =>
            (bool)AccessTools.Field(inst.GetType(), name).GetValue(inst);
    }
}
