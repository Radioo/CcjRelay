using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CcjRelay {

    public static class HostForwarder {
        static readonly byte[] RegistrationMagic = Encoding.ASCII.GetBytes("CCJRELAY");

        const int KeepaliveIntervalSec = 25;
        const int ClientIdHeaderLen = 2;
        const int SIO_UDP_CONNRESET = -1744830452;

        static readonly object _stateLock = new();
        static UdpClient _relaySocket;
        static IPEndPoint _relayEndpoint;
        static int _unetLocalPort;
        static CancellationTokenSource _cts;

        static readonly ConcurrentDictionary<ushort, ProxyEntry> _byClientId = new();

        public enum ForwarderRole { Idle, Server, Client }

        public static ForwarderRole Role { get; private set; } = ForwarderRole.Idle;
        public static string RelayEndpointDisplay { get; private set; } = "(not started)";
        public static int UnetLocalPort => _unetLocalPort;
        public static int ActiveClientCount => _byClientId.Count;

        public static long PacketsRelayInbound;
        public static long PacketsRelayOutbound;
        public static long BytesRelayInbound;
        public static long BytesRelayOutbound;
        public static string LastError { get; private set; }
        public static DateTime? LastErrorAt { get; private set; }

        sealed class ProxyEntry {
            public ushort    clientId;
            public UdpClient proxySocket;
            public int       proxyLocalPort;
        }

        public static void Start(string relayHost, int relayPort, int unetLocalPort) {
            lock (_stateLock) {
                StopLocked();

                if (!ResolveEndpoint(relayHost, relayPort, out _relayEndpoint)) {
                    RecordError($"HostForwarder.Start: bad relay endpoint '{relayHost}':{relayPort}");
                    return;
                }
                _unetLocalPort = unetLocalPort;

                try {
                    _relaySocket = new UdpClient(0, AddressFamily.InterNetwork);
                    DisableUdpConnReset(_relaySocket);
                    _cts = new CancellationTokenSource();

                    Task.Run(() => RelayRecvLoop(_relaySocket, _relayEndpoint, _cts.Token));
                    Task.Run(() => KeepaliveLoop(_cts.Token));

                    SendRegistrationLocked();

                    Role = ForwarderRole.Server;
                    RelayEndpointDisplay = _relayEndpoint.ToString();
                    CcjRelayPlugin.L.LogInfo(
                        $"HostForwarder started: relay={_relayEndpoint}, " +
                        $"local UNet port={_unetLocalPort}");
                }
                catch (Exception e) {
                    RecordError($"HostForwarder.Start failed: {e.Message}");
                    StopLocked();
                }
            }
        }

        public static void Stop() {
            lock (_stateLock) { StopLocked(); }
        }

        public static void NoteClientMode(string relayHost, int relayPort) {
            lock (_stateLock) {
                if (Role == ForwarderRole.Server) return;
                Role = ForwarderRole.Client;
                RelayEndpointDisplay = $"{relayHost}:{relayPort}  (vanilla UNet path)";
                CcjRelayPlugin.L.LogInfo(
                    $"HostForwarder: client mode noted — vanilla UNet → {relayHost}:{relayPort}");
            }
        }

        static void RecordError(string msg) {
            LastError = msg;
            LastErrorAt = DateTime.Now;
            CcjRelayPlugin.L.LogError(msg);
        }

        static void StopLocked() {
            try { _cts?.Cancel(); } catch { }
            try { _relaySocket?.Close(); } catch { }
            foreach (var entry in _byClientId.Values) {
                try { entry.proxySocket?.Close(); } catch { }
            }
            _byClientId.Clear();
            _relaySocket = null;
            _cts = null;
            Role = ForwarderRole.Idle;
            RelayEndpointDisplay = "(not started)";
        }

        static void SendRegistrationLocked() {
            try {
                _relaySocket.Send(RegistrationMagic, RegistrationMagic.Length, _relayEndpoint);
                Interlocked.Increment(ref PacketsRelayOutbound);
                Interlocked.Add(ref BytesRelayOutbound, RegistrationMagic.Length);
                CcjRelayPlugin.L.LogInfo(
                    $"HostForwarder: sent registration ({RegistrationMagic.Length}B) to {_relayEndpoint}");
            }
            catch (Exception e) {
                RecordError($"HostForwarder registration send failed: {e.Message}");
            }
        }

        static async Task KeepaliveLoop(CancellationToken token) {
            try {
                while (!token.IsCancellationRequested) {
                    await Task.Delay(TimeSpan.FromSeconds(KeepaliveIntervalSec), token).ConfigureAwait(false);
                    UdpClient sock;
                    IPEndPoint ep;
                    lock (_stateLock) { sock = _relaySocket; ep = _relayEndpoint; }
                    if (sock == null || ep == null) return;
                    try {
                        sock.Send(RegistrationMagic, RegistrationMagic.Length, ep);
                        Interlocked.Increment(ref PacketsRelayOutbound);
                        Interlocked.Add(ref BytesRelayOutbound, RegistrationMagic.Length);
                        if (CcjRelayPlugin.VerboseLogging.Value)
                            CcjRelayPlugin.L.LogDebug("HostForwarder: keepalive sent");
                    }
                    catch (Exception e) {
                        CcjRelayPlugin.L.LogWarning($"HostForwarder keepalive failed: {e.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        static async Task RelayRecvLoop(UdpClient sock, IPEndPoint expectedRelay, CancellationToken token) {
            int connResetCount = 0;
            while (!token.IsCancellationRequested) {
                try {
                    var result = await sock.ReceiveAsync().ConfigureAwait(false);
                    if (token.IsCancellationRequested) break;

                    var src = result.RemoteEndPoint;
                    if (!src.Address.Equals(expectedRelay.Address)) {
                        if (CcjRelayPlugin.VerboseLogging.Value)
                            CcjRelayPlugin.L.LogDebug($"HostForwarder: dropped packet from unexpected {src}");
                        continue;
                    }
                    Interlocked.Increment(ref PacketsRelayInbound);
                    Interlocked.Add(ref BytesRelayInbound, result.Buffer.Length);

                    HandleRelayInbound(result.Buffer);
                }
                catch (ObjectDisposedException) { return; }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.OperationAborted) { return; }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset) {
                    connResetCount++;
                    if (connResetCount == 1) {
                        RecordError("HostForwarder: relay sent ICMP unreachable. Continuing recv loop.");
                    }
                }
                catch (Exception e) {
                    RecordError($"HostForwarder relay recv loop: {e.Message}");
                    return;
                }
            }
        }

        static void HandleRelayInbound(byte[] framed) {
            if (framed.Length < ClientIdHeaderLen) {
                if (CcjRelayPlugin.VerboseLogging.Value)
                    CcjRelayPlugin.L.LogDebug(
                        $"HostForwarder: relay packet too short ({framed.Length}B) — dropping");
                return;
            }
            ushort clientId = (ushort)((framed[0] << 8) | framed[1]);
            int payloadLen = framed.Length - ClientIdHeaderLen;

            var entry = _byClientId.GetOrAdd(clientId, CreateProxyEntry);
            if (entry == null) return;

            try {
                var payload = new byte[payloadLen];
                if (payloadLen > 0) {
                    Buffer.BlockCopy(framed, ClientIdHeaderLen, payload, 0, payloadLen);
                }
                entry.proxySocket.Send(
                    payload, payloadLen,
                    new IPEndPoint(IPAddress.Loopback, _unetLocalPort));
                if (CcjRelayPlugin.VerboseLogging.Value)
                    CcjRelayPlugin.L.LogDebug(
                        $"HostForwarder: relay→UNet  {payloadLen}B  " +
                        $"clientId={clientId} proxyPort={entry.proxyLocalPort}");
            }
            catch (Exception e) {
                CcjRelayPlugin.L.LogWarning(
                    $"HostForwarder: forward to UNet failed (clientId={clientId}): {e.Message}");
            }
        }

        static ProxyEntry CreateProxyEntry(ushort clientId) {
            try {
                var sock = new UdpClient(0, AddressFamily.InterNetwork);
                DisableUdpConnReset(sock);
                int localPort = ((IPEndPoint)sock.Client.LocalEndPoint).Port;
                var entry = new ProxyEntry {
                    clientId       = clientId,
                    proxySocket    = sock,
                    proxyLocalPort = localPort,
                };
                _ = Task.Run(() => ProxyRecvLoop(entry, _cts.Token));
                CcjRelayPlugin.L.LogInfo(
                    $"HostForwarder: new proxy slot clientId={clientId} ↔ " +
                    $"127.0.0.1:{localPort} ↔ UNet:{_unetLocalPort}");
                return entry;
            }
            catch (Exception e) {
                RecordError($"HostForwarder: proxy socket open failed for clientId={clientId}: {e.Message}");
                return null;
            }
        }

        static async Task ProxyRecvLoop(ProxyEntry entry, CancellationToken token) {
            int connResetCount = 0;
            while (!token.IsCancellationRequested) {
                try {
                    var result = await entry.proxySocket.ReceiveAsync().ConfigureAwait(false);
                    if (token.IsCancellationRequested) break;

                    if (!result.RemoteEndPoint.Address.Equals(IPAddress.Loopback)) {
                        if (CcjRelayPlugin.VerboseLogging.Value)
                            CcjRelayPlugin.L.LogDebug(
                                $"HostForwarder: proxy dropped non-loopback {result.RemoteEndPoint}");
                        continue;
                    }

                    UdpClient relaySock;
                    IPEndPoint relayEp;
                    lock (_stateLock) { relaySock = _relaySocket; relayEp = _relayEndpoint; }
                    if (relaySock == null || relayEp == null) return;

                    int payloadLen = result.Buffer.Length;
                    var framed = new byte[ClientIdHeaderLen + payloadLen];
                    framed[0] = (byte)(entry.clientId >> 8);
                    framed[1] = (byte)(entry.clientId & 0xFF);
                    if (payloadLen > 0) {
                        Buffer.BlockCopy(result.Buffer, 0, framed, ClientIdHeaderLen, payloadLen);
                    }

                    relaySock.Send(framed, framed.Length, relayEp);
                    Interlocked.Increment(ref PacketsRelayOutbound);
                    Interlocked.Add(ref BytesRelayOutbound, framed.Length);
                    if (CcjRelayPlugin.VerboseLogging.Value)
                        CcjRelayPlugin.L.LogDebug(
                            $"HostForwarder: UNet→relay  {payloadLen}B (framed {framed.Length}B) " +
                            $"clientId={entry.clientId} proxyPort={entry.proxyLocalPort}");
                }
                catch (ObjectDisposedException) { return; }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.OperationAborted) { return; }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset) {
                    connResetCount++;
                    if (connResetCount == 1) {
                        CcjRelayPlugin.L.LogWarning(
                            $"HostForwarder: UNet on 127.0.0.1:{_unetLocalPort} returned ICMP " +
                            $"unreachable for proxy port {entry.proxyLocalPort}. Continuing.");
                    }
                }
                catch (Exception e) {
                    CcjRelayPlugin.L.LogWarning(
                        $"HostForwarder: proxy recv loop (proxyPort={entry.proxyLocalPort}): {e.Message}");
                    return;
                }
            }
        }

        static bool ResolveEndpoint(string host, int port, out IPEndPoint ep) {
            ep = null;
            if (string.IsNullOrEmpty(host) || port <= 0 || port > 65535) {
                CcjRelayPlugin.L.LogError($"Bad relay endpoint: '{host}':{port}");
                return false;
            }
            try {
                if (IPAddress.TryParse(host, out var ip)) {
                    ep = new IPEndPoint(ip, port);
                    return true;
                }
                var entry = Dns.GetHostEntry(host);
                foreach (var addr in entry.AddressList) {
                    if (addr.AddressFamily == AddressFamily.InterNetwork) {
                        ep = new IPEndPoint(addr, port);
                        return true;
                    }
                }
                CcjRelayPlugin.L.LogError($"DNS for '{host}' returned no IPv4 address");
                return false;
            }
            catch (Exception e) {
                CcjRelayPlugin.L.LogError($"DNS lookup '{host}' failed: {e.Message}");
                return false;
            }
        }

        static void DisableUdpConnReset(UdpClient client) {
            try {
                client.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch { }
        }
    }
}
