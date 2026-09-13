using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using SlitherIn.Core.Config;

namespace SlitherIn.Input
{
    /// <summary>
    /// VIIPER HID engine — the opt-in escape hatch for games that block direct
    /// input. Port of the old SlitherIn.Viiper.cs (here it's a second IKeyEngine,
    /// not the only path).
    ///
    /// HARD-FAIL rule (locked): when a profile selects `viiper` and
    /// <see cref="Available"/> is false, the app does NOT function for that
    /// profile — alert icon + tray message + log detail. No fallback to SendInput.
    ///
    /// Transport (ported 1:1 from the old app): one-shot null-terminated HTTP-ish
    /// requests onto the server's TCP API (ping, bus/create, bus/N/add, bus/remove);
    /// the device's report stream is a separate long-lived connection whose first
    /// frame is the `bus/N/devId\0` handshake and every frame after that is a HID
    /// report [mod][count][usages...]. A background drain thread reads feedback and
    /// flags a dead device (the connection closing) so the next Available rebuilds.
    /// </summary>
    public sealed class ViiperEngine : IKeyEngine
    {
        private const string DefaultHost = "127.0.0.1";
        private const int DefaultPort = 3242;

        /// <summary>A HID boot-keyboard report carries at most 6 non-modifier
        /// usages; a byte can't hold more anyway. Anything beyond is dropped (the
        /// device would reject/overflow it).</summary>
        private const int MaxReportKeys = 6;
        private const int RebuildThrottleMs = 2000;
        private const int ServerWaitMs = 250;
        private const int ServerWaitAttempts = 40;

        private readonly string _host;
        private readonly int _port;
        private readonly bool _allowServerLaunch;

        private Process _serverProc;
        private int _busId = -1;
        private string _devId;
        private TcpClient _stream;
        private NetworkStream _streamNs;
        private bool _deviceAlive;
        private readonly object _stateLock = new();
        private long _lastRebuildAttempt;

        public ViiperEngine() : this(DefaultHost, DefaultPort, allowServerLaunch: true) { }

        /// <summary>Test seam: point at a mock port and forbid auto-launching the
        /// real viiper.exe. Production uses the default host/port + auto-launch.</summary>
        internal ViiperEngine(string host, int port, bool allowServerLaunch = false)
        {
            _host = host;
            _port = port;
            _allowServerLaunch = allowServerLaunch;
        }

        public string Name => "viiper";

        /// <summary>
        /// True when the device is connected RIGHT NOW. A dead-but-pingable server
        /// triggers a throttled rebuild (re-add the keyboard + reopen the report
        /// stream), so an unplug + replug self-heals on the next ApplyActiveState.
        /// Reconnect never relaunches the server process — that only happens on the
        /// explicit Initialize() (keeps this getter bounded for the UI thread).
        /// </summary>
        public bool Available
        {
            get
            {
                try
                {
                    lock (_stateLock)
                    {
                        if (_deviceAlive) return true;
                        if (Environment.TickCount - _lastRebuildAttempt < RebuildThrottleMs) return false;
                        if (!Ping()) return false;
                        _lastRebuildAttempt = Environment.TickCount;
                        return RebuildDeviceLocked();
                    }
                }
                catch { return false; }
            }
        }

        /// <summary>Find/ping the server (auto-launch viiper.exe if found), then
        /// open the device. Called once by the composition root when the engine is
        /// first selected; the hard-fail check reads <see cref="Available"/> after.</summary>
        public void Initialize()
        {
            if (!EnsureServer()) return;
            EnsureDevice();
        }

        /// <summary>
        /// Push the full set of currently-held chords as ONE HID report — VIIPER's
        /// SetState model (dispatch passes the union of held chords on every down/up
        /// transition, just like the old app). Mouse-button chords cannot ride a
        /// keyboard report and are dropped whole (their modifiers too); such a
        /// profile is rejected at validation, this is the belt-and-braces layer.
        /// </summary>
        public void SetState(IReadOnlyList<Chord> pressed)
        {
            if (pressed == null) throw new ArgumentNullException(nameof(pressed));

            byte mod = 0;
            var keys = new List<byte>(pressed.Count);
            foreach (Chord c in pressed)
            {
                byte usage = HidKeyMap.UsageOf(c.Key);
                if (usage == 0) continue;                       // mouse / unmappable
                mod |= HidKeyMap.Modifiers(c.Modifiers);
                keys.Add(usage);
            }
            SendReport(mod, keys.Count > MaxReportKeys ? keys.GetRange(0, MaxReportKeys).ToArray() : keys.ToArray());
        }

        public void ReleaseAll() => SendReport(0, Array.Empty<byte>());

        public void Dispose()
        {
            lock (_stateLock)
            {
                _deviceAlive = false;
                if (_busId >= 0)
                {
                    try { Request("bus/remove " + _busId); } catch { }
                    _busId = -1;
                    _devId = null;
                }
                CloseStreamLocked();
            }
            // The viiper.exe process is left running (it may be a shared/system
            // server) — disposing this engine only detaches its device. Matches the
            // old app.
        }

        // ==== TCP API ==============================================================

        /// <summary>One-shot request: write `path\0`, read until the server closes.
        /// Every API call is a fresh connection (the old protocol has no session).</summary>
        private string Request(string path)
        {
            using (var c = new TcpClient())
            {
                c.Connect(_host, _port);
                c.ReceiveTimeout = 5000;
                using (NetworkStream ns = c.GetStream())
                {
                    byte[] req = Encoding.UTF8.GetBytes(path + "\0");
                    ns.Write(req, 0, req.Length);
                    ns.Flush();
                    byte[] buf = new byte[4096];
                    using (var ms = new MemoryStream())
                    {
                        int n;
                        while ((n = ns.Read(buf, 0, buf.Length)) > 0)
                            ms.Write(buf, 0, n);
                        return Encoding.UTF8.GetString(ms.ToArray());
                    }
                }
            }
        }

        private bool Ping()
        {
            try { return Request("ping").IndexOf("VIIPER", StringComparison.OrdinalIgnoreCase) >= 0; }
            catch { return false; }
        }

        private static string FindServerPath()
        {
            string[] cands =
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "viiper.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VIIPER", "viiper.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VIIPER", "viiper.exe"),
            };
            foreach (string p in cands)
                if (File.Exists(p)) return p;
            return null;
        }

        private static string FindUsbipDir()
        {
            string[] cands =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "USBip"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "USBip"),
            };
            foreach (string p in cands)
                if (File.Exists(Path.Combine(p, "usbip.exe"))) return p;
            return null;
        }

        private bool EnsureServer()
        {
            if (Ping()) return true;
            if (!_allowServerLaunch) return false;

            string path = FindServerPath();
            if (path == null) return false;
            try
            {
                var psi = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
                string usbipDir = FindUsbipDir();
                if (usbipDir != null)
                {
                    string cur = psi.EnvironmentVariables["PATH"];
                    psi.EnvironmentVariables["PATH"] = (string.IsNullOrEmpty(cur) ? "" : cur + ";") + usbipDir;
                }
                _serverProc = Process.Start(psi);
            }
            catch { return false; }

            for (int i = 0; i < ServerWaitAttempts; i++)
            {
                if (Ping()) return true;
                Thread.Sleep(ServerWaitMs);
            }
            return false;
        }

        private bool EnsureDevice()
        {
            lock (_stateLock)
            {
                if (_deviceAlive) return true;
                if (Environment.TickCount - _lastRebuildAttempt < RebuildThrottleMs) return false;
                _lastRebuildAttempt = Environment.TickCount;
                return RebuildDeviceLocked();
            }
        }

        /// <summary>bus/create → bus/N/add keyboard → open the report stream and
        /// drain. Always called under <see cref="_stateLock"/>. On failure the bus
        /// is removed (the old code leaked it when the add step failed).</summary>
        private bool RebuildDeviceLocked()
        {
            try
            {
                if (_busId < 0)
                {
                    string r = Request("bus/create");
                    using (JsonDocument doc = ParseJson(r))
                    {
                        if (doc == null || !doc.RootElement.TryGetProperty("busId", out JsonElement id)
                            || id.ValueKind != JsonValueKind.Number)
                            return false;
                        _busId = id.GetInt32();
                    }
                }

                string dev = Request($"bus/{_busId}/add {{\"type\":\"keyboard\"}}");
                using (JsonDocument doc = ParseJson(dev))
                {
                    if (doc == null || !doc.RootElement.TryGetProperty("devId", out JsonElement dv)
                        || dv.ValueKind != JsonValueKind.String)
                    {
                        RemoveBusLocked();
                        return false;
                    }
                    _devId = dv.GetString();
                }

                var s = new TcpClient();
                s.Connect(_host, _port);
                NetworkStream ns = s.GetStream();
                byte[] hs = Encoding.UTF8.GetBytes($"bus/{_busId}/{_devId}\0");
                ns.Write(hs, 0, hs.Length);
                ns.Flush();

                CloseStreamLocked();
                _stream = s;
                _streamNs = ns;
                _deviceAlive = true;

                var t = new Thread(DrainLoop) { IsBackground = true };
                t.Start();
                return true;
            }
            catch
            {
                CloseStreamLocked();
                RemoveBusLocked();
                return false;
            }
        }

        private void RemoveBusLocked()
        {
            if (_busId < 0) return;
            try { Request("bus/remove " + _busId); } catch { }
            _busId = -1;
            _devId = null;
        }

        private void CloseStreamLocked()
        {
            if (_stream != null)
            {
                try { _stream.Close(); } catch { }
                _stream = null;
                _streamNs = null;
            }
        }

        private void DrainLoop()
        {
            NetworkStream mine = _streamNs;
            byte[] buf = new byte[256];
            try
            {
                while (true)
                {
                    int n = mine.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                }
            }
            catch { }
            lock (_stateLock)
            {
                if (ReferenceEquals(_streamNs, mine) || _stream == null)
                    _deviceAlive = false;
            }
        }

        private void SendReport(byte mod, byte[] keys)
        {
            byte[] pkt = new byte[2 + keys.Length];
            pkt[0] = mod;
            pkt[1] = (byte)keys.Length;
            Array.Copy(keys, 0, pkt, 2, keys.Length);

            lock (_stateLock)
            {
                if (!_deviceAlive) return;
                NetworkStream ns = _streamNs;
                if (ns == null) return;
                try { ns.Write(pkt, 0, pkt.Length); ns.Flush(); }
                catch { /* device lost — the drain loop flags it */ }
            }
        }

        private static JsonDocument ParseJson(string json)
        {
            try { return string.IsNullOrEmpty(json) ? null : JsonDocument.Parse(json); }
            catch { return null; }
        }
    }
}