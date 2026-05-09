using System;
using System.Collections.Generic;
using System.Globalization;

using BepInEx.Logging;

using UnityEngine;

namespace CcjRelay {
    public class RelayHud : MonoBehaviour {
        const int WINDOW_ID = 0xCCDA;
        const int MaxLogEntries = 5000;
        const float MinWindowWidth = 420f;
        const float MinWindowHeight = 280f;
        const float ResizeHandleSize = 16f;

        public static RelayHud Instance { get; private set; }

        public KeyCode ToggleKey = KeyCode.F8;

        bool _visible;
        bool _autoScroll = true;
        Vector2 _logScroll = Vector2.zero;
        Rect _windowRect = new Rect(20f, 20f, 720f, 640f);
        bool _resizing;

        readonly Queue<LogEntry> _log = new Queue<LogEntry>();
        readonly object _logLock = new object();
        long _logVersion;
        long _lastSnappedVersion;

        string _lastSaveResult;
        bool _lastSaveOk;

        void Awake() {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        void OnDestroy() {
            if (Instance == this) Instance = null;
        }

        void Update() {
            if (Input.GetKeyDown(ToggleKey)) {
                _visible = !_visible;
            }
        }

        void OnGUI() {
            if (!_visible) return;
            _windowRect = GUI.Window(WINDOW_ID, _windowRect, DrawWindow, $"{PluginInfo.Name}  v{PluginInfo.Version}");
        }

        void DrawWindow(int id) {
            DrawStatus();
            GUILayout.Space(6f);
            DrawLog();
            HandleResize();
            GUI.DragWindow(new Rect(0f, 0f,
                _windowRect.width - ResizeHandleSize - 4f, 20f));
        }

        void DrawStatus() {
            GUILayout.BeginVertical(GUI.skin.box);
            Row("Plugin enabled", CcjRelayPlugin.RelayEnabled.Value.ToString());
            Row("Verbose log",    CcjRelayPlugin.VerboseLogging.Value.ToString());
            GUILayout.Space(2f);

            Row("Role",           HostForwarder.Role.ToString(),
                                  RoleColor(HostForwarder.Role));
            Row("Relay endpoint", HostForwarder.RelayEndpointDisplay);
            if (HostForwarder.Role == HostForwarder.ForwarderRole.Server) {
                Row("UNet local port", HostForwarder.UnetLocalPort.ToString(CultureInfo.InvariantCulture));
                Row("Active clients",  HostForwarder.ActiveClientCount.ToString(CultureInfo.InvariantCulture));
            }
            GUILayout.Space(2f);

            if (HostForwarder.Role == HostForwarder.ForwarderRole.Server) {
                Row("Out (us → relay)", $"{HostForwarder.PacketsRelayOutbound} pkts  /  {FmtBytes(HostForwarder.BytesRelayOutbound)}");
                Row("In  (relay → us)", $"{HostForwarder.PacketsRelayInbound} pkts  /  {FmtBytes(HostForwarder.BytesRelayInbound)}");
                GUILayout.Space(2f);
            }
            else if (HostForwarder.Role == HostForwarder.ForwarderRole.Client) {
                var prev = GUI.color;
                GUI.color = new Color(0.7f, 0.7f, 0.7f);
                GUILayout.Label("Joiner mode — vanilla UNet runs the transport.\nRelay traffic counters are host-only.");
                GUI.color = prev;
                GUILayout.Space(2f);
            }

            var lastErr = HostForwarder.LastError;
            if (string.IsNullOrEmpty(lastErr)) {
                Row("Last error", "(none)");
            }
            else {
                var ageStr = HostForwarder.LastErrorAt is { } at
                    ? $"  ({(int)(DateTime.Now - at).TotalSeconds}s ago)"
                    : "";
                Row("Last error", lastErr + ageStr, Color.red);
            }
            GUILayout.EndVertical();
        }

        void DrawLog() {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Log", GUILayout.Width(40f));
            if (!string.IsNullOrEmpty(_lastSaveResult)) {
                var col = GUI.color;
                GUI.color = _lastSaveOk ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 0.5f, 0.5f);
                GUILayout.Label(_lastSaveResult);
                GUI.color = col;
            }
            GUILayout.FlexibleSpace();
            _autoScroll = GUILayout.Toggle(_autoScroll, "auto-scroll", GUILayout.Width(95f));
            if (GUILayout.Button("save", GUILayout.Width(50f))) SaveLogToFile();
            if (GUILayout.Button("clear", GUILayout.Width(50f))) {
                lock (_logLock) { _log.Clear(); _logVersion++; }
            }
            GUILayout.EndHorizontal();

            LogEntry[] snapshot;
            long version;
            lock (_logLock) { snapshot = _log.ToArray(); version = _logVersion; }

            if (_autoScroll && version != _lastSnappedVersion) {
                _logScroll.y = float.MaxValue;
                _lastSnappedVersion = version;
            }

            _logScroll = GUILayout.BeginScrollView(_logScroll, GUI.skin.box,
                GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
            var prevColor = GUI.color;
            foreach (var e in snapshot) {
                GUI.color = LevelColor(e.Level);
                GUILayout.Label($"[{e.Time:HH:mm:ss.fff}] [{e.Level,-7}] {e.Text}");
            }
            GUI.color = prevColor;
            GUILayout.EndScrollView();
        }

        void HandleResize() {
            var rect = new Rect(
                _windowRect.width - ResizeHandleSize - 2f,
                _windowRect.height - ResizeHandleSize - 2f,
                ResizeHandleSize, ResizeHandleSize);
            GUI.Box(rect, "//");

            var ev = Event.current;
            if (ev.type == EventType.MouseDown && rect.Contains(ev.mousePosition)) {
                _resizing = true;
                ev.Use();
            }
            if (_resizing && ev.type == EventType.MouseDrag) {
                _windowRect.width  = Mathf.Max(MinWindowWidth,  _windowRect.width  + ev.delta.x);
                _windowRect.height = Mathf.Max(MinWindowHeight, _windowRect.height + ev.delta.y);
                ev.Use();
            }
            if (ev.type == EventType.MouseUp) {
                _resizing = false;
            }
        }

        void SaveLogToFile() {
            LogEntry[] snapshot;
            lock (_logLock) { snapshot = _log.ToArray(); }

            try {
                var dir = System.IO.Path.Combine(BepInEx.Paths.GameRootPath, "BepInEx", "ccj-relay-logs");
                System.IO.Directory.CreateDirectory(dir);
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                var role = HostForwarder.Role.ToString().ToLowerInvariant();
                var path = System.IO.Path.Combine(dir, $"hud_{role}_{stamp}.log");

                using (var sw = new System.IO.StreamWriter(path, append: false, encoding: System.Text.Encoding.UTF8)) {
                    sw.WriteLine($"# CCJ Relay HUD log — saved {DateTime.Now:O}");
                    sw.WriteLine($"# plugin v{PluginInfo.Version}, role={HostForwarder.Role}, endpoint={HostForwarder.RelayEndpointDisplay}");
                    sw.WriteLine($"# stats: out={HostForwarder.PacketsRelayOutbound}p/{HostForwarder.BytesRelayOutbound}b  in={HostForwarder.PacketsRelayInbound}p/{HostForwarder.BytesRelayInbound}b  clients={HostForwarder.ActiveClientCount}");
                    sw.WriteLine($"# entries: {snapshot.Length}");
                    sw.WriteLine();
                    foreach (var e in snapshot) {
                        sw.WriteLine($"[{e.Time:HH:mm:ss.fff}] [{e.Level,-7}] {e.Text}");
                    }
                }

                _lastSaveOk = true;
                _lastSaveResult = $"saved → {System.IO.Path.GetFileName(path)}";
                CcjRelayPlugin.L.LogInfo($"HUD log saved to {path}");
            }
            catch (Exception e) {
                _lastSaveOk = false;
                _lastSaveResult = $"save failed: {e.Message}";
                CcjRelayPlugin.L.LogError($"HUD log save failed: {e}");
            }
        }

        public void Append(LogLevel level, string text) {
            lock (_logLock) {
                _log.Enqueue(new LogEntry { Time = DateTime.Now, Level = level, Text = text });
                while (_log.Count > MaxLogEntries) _log.Dequeue();
                _logVersion++;
            }
        }

        static void Row(string key, string value) => Row(key, value, GUI.color);

        static void Row(string key, string value, Color valueColor) {
            GUILayout.BeginHorizontal();
            GUILayout.Label(key, GUILayout.Width(160f));
            var prev = GUI.color;
            GUI.color = valueColor;
            GUILayout.Label(value);
            GUI.color = prev;
            GUILayout.EndHorizontal();
        }

        static Color RoleColor(HostForwarder.ForwarderRole r) => r switch {
            HostForwarder.ForwarderRole.Server => new Color(0.4f, 1f, 0.4f),
            HostForwarder.ForwarderRole.Client => new Color(0.4f, 0.7f, 1f),
            _ => new Color(0.7f, 0.7f, 0.7f),
        };

        static Color LevelColor(LogLevel l) {
            if ((l & (LogLevel.Error | LogLevel.Fatal)) != 0)   return new Color(1f, 0.4f, 0.4f);
            if ((l & LogLevel.Warning) != 0)                    return new Color(1f, 0.85f, 0.3f);
            if ((l & LogLevel.Debug) != 0)                      return new Color(0.6f, 0.6f, 0.6f);
            return new Color(0.92f, 0.92f, 0.92f);
        }

        static string FmtBytes(long b) {
            if (b < 1024L)               return b + " B";
            if (b < 1024L * 1024L)       return (b / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " KB";
            if (b < 1024L * 1024L * 1024L) return (b / (1024.0 * 1024.0)).ToString("F2", CultureInfo.InvariantCulture) + " MB";
            return (b / (1024.0 * 1024.0 * 1024.0)).ToString("F2", CultureInfo.InvariantCulture) + " GB";
        }

        struct LogEntry {
            public DateTime Time;
            public LogLevel Level;
            public string Text;
        }
    }
}
