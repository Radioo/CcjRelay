using System;
using System.Diagnostics;
using System.Reflection;

using HarmonyLib;

namespace CcjRelay {

    static class DiagLog {
        public static void Info(string msg)  => CcjRelayPlugin.L.LogInfo(msg);
        public static void Warn(string msg)  => CcjRelayPlugin.L.LogWarning(msg);
        public static void Error(string msg) => CcjRelayPlugin.L.LogError(msg);

        public static string Caller(int skipFrames = 2, int maxFrames = 4) {
            try {
                var st = new StackTrace(skipFrames, fNeedFileInfo: false);
                var frames = st.GetFrames();
                if (frames == null) return "<no frames>";
                var sb = new System.Text.StringBuilder();
                int n = Math.Min(maxFrames, frames.Length);
                for (int i = 0; i < n; i++) {
                    var m = frames[i].GetMethod();
                    if (m == null) continue;
                    if (i > 0) sb.Append(" ← ");
                    sb.Append(m.DeclaringType?.Name).Append('.').Append(m.Name);
                }
                return sb.ToString();
            }
            catch { return "<stack unavailable>"; }
        }

        public static string FieldStr(object inst, string name) {
            try { return AccessTools.Field(inst.GetType(), name).GetValue(inst)?.ToString() ?? "<null>"; }
            catch (Exception e) { return $"<err: {e.GetType().Name}>"; }
        }
    }

    [HarmonyPatch]
    static class P_UNet_Init {
        static MethodBase TargetMethod() => GameTypes.Method(GameTypes.UNetTransport, "Init");
        [HarmonyPrefix]
        static bool Prefix(object __instance) {
            DiagLog.Info(
                $"[mlapi] UNetTransport.Init  ConnectAddress={DiagLog.FieldStr(__instance, "ConnectAddress")} " +
                $"ConnectPort={DiagLog.FieldStr(__instance, "ConnectPort")} " +
                $"ServerListenPort={DiagLog.FieldStr(__instance, "ServerListenPort")} " +
                $"UseMLAPIRelay={DiagLog.FieldStr(__instance, "UseMLAPIRelay")} " +
                $"MLAPIRelayAddress={DiagLog.FieldStr(__instance, "MLAPIRelayAddress")}:{DiagLog.FieldStr(__instance, "MLAPIRelayPort")}");
            return true;
        }
        [HarmonyFinalizer]
        static Exception Finalizer(Exception __exception) {
            if (__exception != null) DiagLog.Error($"[mlapi] UNetTransport.Init THREW: {__exception}");
            else                     DiagLog.Info("[mlapi] UNetTransport.Init returned");
            return __exception;
        }
    }

    [HarmonyPatch]
    static class P_UNet_StartServer {
        static MethodBase TargetMethod() => GameTypes.Method(GameTypes.UNetTransport, "StartServer");
        [HarmonyPrefix]
        static bool Prefix(object __instance) {
            DiagLog.Info(
                $"[mlapi] UNetTransport.StartServer  ConnectAddress={DiagLog.FieldStr(__instance, "ConnectAddress")} " +
                $"ConnectPort={DiagLog.FieldStr(__instance, "ConnectPort")} " +
                $"ServerListenPort={DiagLog.FieldStr(__instance, "ServerListenPort")} " +
                $"UseMLAPIRelay={DiagLog.FieldStr(__instance, "UseMLAPIRelay")} caller=({DiagLog.Caller()})");
            return true;
        }
        [HarmonyFinalizer]
        static Exception Finalizer(Exception __exception) {
            if (__exception != null) DiagLog.Error($"[mlapi] UNetTransport.StartServer THREW: {__exception}");
            else                     DiagLog.Info("[mlapi] UNetTransport.StartServer returned");
            return __exception;
        }
    }

    [HarmonyPatch]
    static class P_UNet_StartClient {
        static MethodBase TargetMethod() => GameTypes.Method(GameTypes.UNetTransport, "StartClient");
        [HarmonyPrefix]
        static bool Prefix(object __instance) {
            DiagLog.Info(
                $"[mlapi] UNetTransport.StartClient  ConnectAddress={DiagLog.FieldStr(__instance, "ConnectAddress")} " +
                $"ConnectPort={DiagLog.FieldStr(__instance, "ConnectPort")} " +
                $"UseMLAPIRelay={DiagLog.FieldStr(__instance, "UseMLAPIRelay")} caller=({DiagLog.Caller()})");
            return true;
        }
        [HarmonyFinalizer]
        static Exception Finalizer(Exception __exception) {
            if (__exception != null) DiagLog.Error($"[mlapi] UNetTransport.StartClient THREW: {__exception}");
            else                     DiagLog.Info("[mlapi] UNetTransport.StartClient returned");
            return __exception;
        }
    }

    [HarmonyPatch]
    static class P_UNet_Shutdown_Diag {
        static MethodBase TargetMethod() => GameTypes.Method(GameTypes.UNetTransport, "Shutdown");
        [HarmonyPrefix]
        static bool Prefix() {
            DiagLog.Info($"[mlapi] UNetTransport.Shutdown caller=({DiagLog.Caller()})");
            return true;
        }
    }

    [HarmonyPatch]
    static class P_UNet_DisconnectRemote {
        static MethodBase TargetMethod() => GameTypes.Method(GameTypes.UNetTransport, "DisconnectRemoteClient");
        [HarmonyPrefix]
        static bool Prefix(ulong clientId) {
            DiagLog.Warn($"[mlapi] UNetTransport.DisconnectRemoteClient(connId={clientId}) caller=({DiagLog.Caller()})");
            return true;
        }
    }

    [HarmonyPatch]
    static class P_UNet_DisconnectLocal {
        static MethodBase TargetMethod() => GameTypes.Method(GameTypes.UNetTransport, "DisconnectLocalClient");
        [HarmonyPrefix]
        static bool Prefix() {
            DiagLog.Warn($"[mlapi] UNetTransport.DisconnectLocalClient caller=({DiagLog.Caller()})");
            return true;
        }
    }

    [HarmonyPatch]
    static class P_UNet_Send {
        static MethodBase TargetMethod() => GameTypes.Method(GameTypes.UNetTransport, "Send");
        [HarmonyPrefix]
        static bool Prefix(ulong clientId, object networkChannel) {
            if (CcjRelayPlugin.VerboseLogging.Value) {
                CcjRelayPlugin.L.LogDebug(
                    $"[mlapi] UNetTransport.Send connId={clientId} chan={networkChannel}");
            }
            return true;
        }
    }

    [HarmonyPatch]
    static class P_UNet_PollEvent {
        static MethodBase TargetMethod() => GameTypes.Method(GameTypes.UNetTransport, "PollEvent");
        [HarmonyPostfix]
        static void Postfix(object __result, ulong clientId, object networkChannel) {
            string ev = __result?.ToString() ?? "<null>";
            if (ev == "Nothing") return;
            if (ev == "Data") {
                if (CcjRelayPlugin.VerboseLogging.Value) {
                    CcjRelayPlugin.L.LogDebug(
                        $"[mlapi] UNetTransport.PollEvent → Data connId={clientId} chan={networkChannel}");
                }
                return;
            }
            DiagLog.Info(
                $"[mlapi] UNetTransport.PollEvent → {ev} connId={clientId} chan={networkChannel}");
        }
    }

    [HarmonyPatch]
    static class P_UNet_SendQueued {
        static MethodBase TargetMethod() => GameTypes.Method(GameTypes.UNetTransport, "SendQueued");
        [HarmonyPrefix]
        static bool Prefix(ulong clientId) {
            if (CcjRelayPlugin.VerboseLogging.Value) {
                CcjRelayPlugin.L.LogDebug($"[mlapi] UNetTransport.SendQueued connId={clientId}");
            }
            return true;
        }
    }

    [HarmonyPatch]
    static class P_UNet_GetCurrentRtt {
        static MethodBase TargetMethod() => GameTypes.Method(GameTypes.UNetTransport, "GetCurrentRtt");
        [HarmonyFinalizer]
        static Exception Finalizer(ulong clientId, Exception __exception) {
            if (__exception != null) {
                DiagLog.Error($"[mlapi] UNetTransport.GetCurrentRtt(connId={clientId}) THREW: {__exception.Message}");
            }
            return __exception;
        }
    }

    [HarmonyPatch]
    static class P_NM_StartHost {
        static MethodBase TargetMethod() => GameTypes.Method(GameTypes.NetworkManager, "StartHost");
        [HarmonyPrefix]
        static bool Prefix() {
            DiagLog.Info($"[mlapi] NetworkManager.StartHost  caller=({DiagLog.Caller()})");
            return true;
        }
        [HarmonyFinalizer]
        static Exception Finalizer(Exception __exception) {
            if (__exception != null) DiagLog.Error($"[mlapi] NetworkManager.StartHost THREW: {__exception}");
            else                     DiagLog.Info("[mlapi] NetworkManager.StartHost returned");
            return __exception;
        }
    }

    [HarmonyPatch]
    static class P_NM_StartServer {
        static MethodBase TargetMethod() => GameTypes.Method(GameTypes.NetworkManager, "StartServer");
        [HarmonyPrefix]
        static bool Prefix() {
            DiagLog.Info($"[mlapi] NetworkManager.StartServer caller=({DiagLog.Caller()})");
            return true;
        }
        [HarmonyFinalizer]
        static Exception Finalizer(Exception __exception) {
            if (__exception != null) DiagLog.Error($"[mlapi] NetworkManager.StartServer THREW: {__exception}");
            else                     DiagLog.Info("[mlapi] NetworkManager.StartServer returned");
            return __exception;
        }
    }

    [HarmonyPatch]
    static class P_NM_StartClient {
        static MethodBase TargetMethod() => GameTypes.Method(GameTypes.NetworkManager, "StartClient");
        [HarmonyPrefix]
        static bool Prefix() {
            DiagLog.Info($"[mlapi] NetworkManager.StartClient caller=({DiagLog.Caller()})");
            return true;
        }
        [HarmonyFinalizer]
        static Exception Finalizer(Exception __exception) {
            if (__exception != null) DiagLog.Error($"[mlapi] NetworkManager.StartClient THREW: {__exception}");
            else                     DiagLog.Info("[mlapi] NetworkManager.StartClient returned");
            return __exception;
        }
    }

    [HarmonyPatch]
    static class P_NM_StopHost {
        static MethodBase TargetMethod() => GameTypes.Method(GameTypes.NetworkManager, "StopHost");
        [HarmonyPrefix]
        static bool Prefix() {
            DiagLog.Info($"[mlapi] NetworkManager.StopHost  caller=({DiagLog.Caller()})");
            return true;
        }
    }

    [HarmonyPatch]
    static class P_NM_StopServer {
        static MethodBase TargetMethod() => GameTypes.Method(GameTypes.NetworkManager, "StopServer");
        [HarmonyPrefix]
        static bool Prefix() {
            DiagLog.Info($"[mlapi] NetworkManager.StopServer caller=({DiagLog.Caller()})");
            return true;
        }
    }

    [HarmonyPatch]
    static class P_NM_StopClient {
        static MethodBase TargetMethod() => GameTypes.Method(GameTypes.NetworkManager, "StopClient");
        [HarmonyPrefix]
        static bool Prefix() {
            DiagLog.Info($"[mlapi] NetworkManager.StopClient caller=({DiagLog.Caller()})");
            return true;
        }
    }

    [HarmonyPatch]
    static class P_NMC_SetUseCase {
        static MethodBase TargetMethod() => GameTypes.Method(GameTypes.NetworkingModulesControler, "SetUseCase");
        [HarmonyPrefix]
        static bool Prefix() {
            DiagLog.Info($"[ccj]   NetworkingModulesControler.SetUseCase  caller=({DiagLog.Caller()})");
            return true;
        }
    }

    [HarmonyPatch]
    static class P_NMUC_StartHost {
        static MethodBase TargetMethod() => GameTypes.Method(GameTypes.NetworkingModulesUseCase, "StartHost");
        [HarmonyPrefix]
        static bool Prefix(int matchCount, int playerCount, DateTime deadlineTime, int port) {
            DiagLog.Info(
                $"[ccj]   NetworkingModulesUseCase.StartHost  matchCount={matchCount} " +
                $"playerCount={playerCount} deadlineTime={deadlineTime:HH:mm:ss} port={port}");
            return true;
        }
        [HarmonyFinalizer]
        static Exception Finalizer(Exception __exception) {
            if (__exception != null) DiagLog.Error($"[ccj]   NetworkingModulesUseCase.StartHost THREW: {__exception}");
            return __exception;
        }
    }

    [HarmonyPatch]
    static class P_NMUC_StartClient {
        static MethodBase TargetMethod() => GameTypes.Method(GameTypes.NetworkingModulesUseCase, "StartClient");
        [HarmonyPrefix]
        static bool Prefix(int matchCount, int playerCount, DateTime deadlineTime, int port) {
            DiagLog.Info(
                $"[ccj]   NetworkingModulesUseCase.StartClient  matchCount={matchCount} " +
                $"playerCount={playerCount} deadlineTime={deadlineTime:HH:mm:ss} port={port}");
            return true;
        }
        [HarmonyFinalizer]
        static Exception Finalizer(Exception __exception) {
            if (__exception != null) DiagLog.Error($"[ccj]   NetworkingModulesUseCase.StartClient THREW: {__exception}");
            return __exception;
        }
    }

    [HarmonyPatch]
    static class P_NMDA_StartHost {
        static MethodBase TargetMethod() => GameTypes.Method(GameTypes.NetworkingModulesDataAccess, "StartHost");
        [HarmonyPrefix]
        static bool Prefix(int matchCount, int playerCount, DateTime deadlineTime, int port) {
            DiagLog.Info(
                $"[ccj]   NetworkingModulesDataAccess.StartHost  matchCount={matchCount} " +
                $"playerCount={playerCount} deadlineTime={deadlineTime:HH:mm:ss} port={port}");
            return true;
        }
        [HarmonyFinalizer]
        static Exception Finalizer(Exception __exception) {
            if (__exception != null) DiagLog.Error($"[ccj]   NetworkingModulesDataAccess.StartHost THREW: {__exception}");
            return __exception;
        }
    }

    [HarmonyPatch]
    static class P_NMDA_StartClient {
        static MethodBase TargetMethod() => GameTypes.Method(GameTypes.NetworkingModulesDataAccess, "StartClient");
        [HarmonyPrefix]
        static bool Prefix(int matchCount, int playerCount, DateTime deadlineTime, int port) {
            DiagLog.Info(
                $"[ccj]   NetworkingModulesDataAccess.StartClient  matchCount={matchCount} " +
                $"playerCount={playerCount} deadlineTime={deadlineTime:HH:mm:ss} port={port}");
            return true;
        }
        [HarmonyFinalizer]
        static Exception Finalizer(Exception __exception) {
            if (__exception != null) DiagLog.Error($"[ccj]   NetworkingModulesDataAccess.StartClient THREW: {__exception}");
            return __exception;
        }
    }

    [HarmonyPatch]
    static class P_RT_AddHost_3 {
        static MethodBase TargetMethod() {
            var ht = GameTypes.Resolve(GameTypes.HostTopology);
            return GameTypes.Method(GameTypes.RelayTransport, "AddHost",
                new[] { ht, typeof(int), typeof(bool) });
        }
        [HarmonyPrefix]
        static bool Prefix(int port, bool createServer) {
            DiagLog.Info(
                $"[mlapi] RelayTransport.AddHost(port={port}, createServer={createServer}) " +
                $"caller=({DiagLog.Caller()})");
            return true;
        }
        [HarmonyFinalizer]
        static Exception Finalizer(int __result, Exception __exception) {
            if (__exception != null) DiagLog.Error($"[mlapi] RelayTransport.AddHost THREW: {__exception}");
            else                     DiagLog.Info($"[mlapi] RelayTransport.AddHost → hostId={__result}");
            return __exception;
        }
    }

    [HarmonyPatch]
    static class P_RT_AddHost_2 {
        static MethodBase TargetMethod() {
            var ht = GameTypes.Resolve(GameTypes.HostTopology);
            return GameTypes.Method(GameTypes.RelayTransport, "AddHost",
                new[] { ht, typeof(bool) });
        }
        [HarmonyPrefix]
        static bool Prefix(bool createServer) {
            DiagLog.Info(
                $"[mlapi] RelayTransport.AddHost(createServer={createServer}) caller=({DiagLog.Caller()})");
            return true;
        }
        [HarmonyFinalizer]
        static Exception Finalizer(int __result, Exception __exception) {
            if (__exception != null) DiagLog.Error($"[mlapi] RelayTransport.AddHost(2) THREW: {__exception}");
            else                     DiagLog.Info($"[mlapi] RelayTransport.AddHost(2) → hostId={__result}");
            return __exception;
        }
    }

    [HarmonyPatch]
    static class P_RT_Connect {
        static MethodBase TargetMethod() => GameTypes.Method(GameTypes.RelayTransport, "Connect");
        [HarmonyPrefix]
        static bool Prefix(int hostId, string serverAddress, int serverPort) {
            DiagLog.Info(
                $"[mlapi] RelayTransport.Connect(hostId={hostId}, server={serverAddress}:{serverPort})");
            return true;
        }
        [HarmonyFinalizer]
        static Exception Finalizer(int __result, Exception __exception) {
            if (__exception != null) DiagLog.Error($"[mlapi] RelayTransport.Connect THREW: {__exception}");
            else                     DiagLog.Info($"[mlapi] RelayTransport.Connect → connectionId={__result}");
            return __exception;
        }
    }
}
