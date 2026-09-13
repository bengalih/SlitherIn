using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;
using SlitherIn.Core.Config;
using SlitherIn.Input;

namespace SlitherIn.Tests
{
    /// <summary>Stage 9: the VIIPER HID backend. A local mock TCP server speaks the
    /// old VIIPER protocol (null-terminated one-shot requests; the device report
    /// stream is a long-lived connection whose first frame is the bus/devId
    /// handshake), so the engine's bus/create + keyboard-add + handshake + report
    /// framing is exercised end-to-end without hardware. HidKeyMap (the pure
    /// token→usage/mod table) is asserted directly too.</summary>
    [TestFixture]
    public class ViiperTests
    {
        private MockViiperServer _server;
        private ViiperEngine _engine;

        [SetUp]
        public void SetUp()
        {
            _server = new MockViiperServer();
            _server.Start();
            _engine = new ViiperEngine("127.0.0.1", _server.Port, allowServerLaunch: false);
        }

        [TearDown]
        public void TearDown()
        {
            _engine?.Dispose();
            _server?.Stop();
            _engine = null;
            _server = null;
        }

        // ==== HidKeyMap (pure table) ==============================================

        [Test]
        public void Usages_MatchTheStandardHidKeyboardTable()
        {
            Assert.That(HidKeyMap.UsageOf("A"), Is.EqualTo(0x04));
            Assert.That(HidKeyMap.UsageOf("K"), Is.EqualTo(0x0E));
            Assert.That(HidKeyMap.UsageOf("Z"), Is.EqualTo(0x1D));
            Assert.That(HidKeyMap.UsageOf("1"), Is.EqualTo(0x1E), "top-row 1");
            Assert.That(HidKeyMap.UsageOf("9"), Is.EqualTo(0x26));
            Assert.That(HidKeyMap.UsageOf("0"), Is.EqualTo(0x27), "top-row 0");
            Assert.That(HidKeyMap.UsageOf("F1"), Is.EqualTo(0x3A));
            Assert.That(HidKeyMap.UsageOf("F12"), Is.EqualTo(0x45));
            Assert.That(HidKeyMap.UsageOf("F13"), Is.EqualTo(0x68), "F13 starts the second usage page");
            Assert.That(HidKeyMap.UsageOf("F24"), Is.EqualTo(0x73));
            Assert.That(HidKeyMap.UsageOf("Space"), Is.EqualTo(0x2C));
            Assert.That(HidKeyMap.UsageOf("Enter"), Is.EqualTo(0x28));
            Assert.That(HidKeyMap.UsageOf("Up"), Is.EqualTo(0x52));
            Assert.That(HidKeyMap.UsageOf("Numpad1"), Is.EqualTo(0x59), "keypad usages are NOT top-row VKs");
            Assert.That(HidKeyMap.UsageOf("Numpad0"), Is.EqualTo(0x62));
            Assert.That(HidKeyMap.UsageOf("NumpadAdd"), Is.EqualTo(0x57));
        }

        [Test]
        public void MouseTokens_HaveNoKeyboardUsage()
        {
            Assert.That(HidKeyMap.UsageOf("LButton"), Is.EqualTo(0));
            Assert.That(HidKeyMap.UsageOf("RButton"), Is.EqualTo(0));
            Assert.That(HidKeyMap.UsageOf("XButton2"), Is.EqualTo(0));
            Assert.That(HidKeyMap.UsageOf(null), Is.EqualTo(0));
        }

        [Test]
        public void ModifierByte_FollowsTheHidOrdering_NotTheFlagOrdering()
        {
            Assert.That(HidKeyMap.Modifiers(ModifierFlags.Ctrl), Is.EqualTo(0x01));
            Assert.That(HidKeyMap.Modifiers(ModifierFlags.Shift), Is.EqualTo(0x02), "HID shift is bit 1");
            Assert.That(HidKeyMap.Modifiers(ModifierFlags.Alt), Is.EqualTo(0x04), "HID alt is bit 2");
            Assert.That(HidKeyMap.Modifiers(ModifierFlags.Win), Is.EqualTo(0x08));
            Assert.That(HidKeyMap.Modifiers(ModifierFlags.Shift | ModifierFlags.Alt), Is.EqualTo(0x06), "shift+alt combine");
            Assert.That(HidKeyMap.Modifiers(ModifierFlags.None), Is.EqualTo(0x00));
        }

        // ==== Protocol ============================================================

        [Test]
        public void Initialize_RegistersAKeyboardDevice_AvailableTrue()
        {
            _engine.Initialize();

            Assert.That(WaitUntil(() => ServerSeen("bus/create")), Is.True, "bus/create was never requested");
            Assert.That(WaitUntil(() => ServerSeen("bus/0/add {\"type\":\"keyboard\"}")), Is.True, "keyboard device never added");
            Assert.That(ServerSeen("bus/0/kbd1"), Is.True, "device handshake stream never opened");
            Assert.That(_engine.Available, Is.True);
        }

        [Test]
        public void SetState_SendsOneFullHidReport_PerCall()
        {
            _engine.Initialize();
            Assert.That(_engine.Available, Is.True);

            _engine.SetState(new[] { new Chord(ModifierFlags.Ctrl, "K") });
            Assert.That(WaitUntil(() => _server.ReportCount >= 1), Is.True, "no report sent");
            Assert.That(_server.LastReport, Is.EqualTo(new byte[] { 0x01, 0x01, 0x0E }), "Ctrl+K → ctrl mod, usage K");

            _engine.SetState(new[] { new Chord(ModifierFlags.None, "1"), new Chord(ModifierFlags.Shift, "A") });
            Assert.That(WaitUntil(() => _server.ReportCount >= 2), Is.True);
            Assert.That(_server.LastReport, Is.EqualTo(new byte[] { 0x02, 0x02, 0x1E, 0x04 }), "shift mod, usages 1 then A");
        }

        [Test]
        public void SetState_EmptyList_ClearsTheReport()
        {
            _engine.Initialize();
            _engine.SetState(new[] { new Chord(ModifierFlags.Ctrl, "K") });
            Assert.That(WaitUntil(() => _server.ReportCount >= 1), Is.True);

            _engine.SetState(new List<Chord>());
            Assert.That(WaitUntil(() => _server.ReportCount >= 2), Is.True);
            Assert.That(_server.LastReport, Is.EqualTo(new byte[] { 0x00, 0x00 }));
        }

        [Test]
        public void SetState_DropsMouseChordsWhole()
        {
            _engine.Initialize();
            _engine.SetState(new[] { new Chord(ModifierFlags.Shift, "RButton") });
            Assert.That(WaitUntil(() => _server.ReportCount >= 1), Is.True);
            Assert.That(_server.LastReport, Is.EqualTo(new byte[] { 0x00, 0x00 }),
                "a mouse chord cannot ride a keyboard report — dropped with its modifiers");
        }

        [Test]
        public void ReleaseAll_SendsEmptyReport()
        {
            _engine.Initialize();
            _engine.SetState(new[] { new Chord(ModifierFlags.None, "A") });
            Assert.That(WaitUntil(() => _server.ReportCount >= 1), Is.True);

            _engine.ReleaseAll();
            Assert.That(WaitUntil(() => _server.ReportCount >= 2), Is.True);
            Assert.That(_server.LastReport, Is.EqualTo(new byte[] { 0x00, 0x00 }));
        }

        [Test]
        public void Dispose_RemovesTheBus()
        {
            _engine.Initialize();
            Assert.That(_engine.Available, Is.True);

            _engine.Dispose();
            Assert.That(WaitUntil(() => ServerSeen("bus/remove 0")), Is.True, "bus not removed on dispose");
        }

        // ==== Hard-fail + self-heal ===============================================

        [Test]
        public void Available_IsFalse_WhenNothingListens()
        {
            var dead = new ViiperEngine("127.0.0.1", DeadPort(), allowServerLaunch: false);
            dead.Initialize();
            Assert.That(dead.Available, Is.False, "no server → engine not available (the hard-fail surface)");
            dead.Dispose();
        }

        [Test]
        public void StreamLoss_IsDetected_AndAvailable_SelfHeals()
        {
            _engine.Initialize();
            Assert.That(_engine.Available, Is.True);

            _server.KillDeviceStreams();

            Assert.That(WaitUntil(() => !_engine.Available, 5000), Is.True, "loss must be detected");
            Assert.That(WaitUntil(() => _engine.Available, 5000), Is.True,
                "pingable server + rebuilt keyboard must self-heal on the next Available");
        }

        // ==== Live verification (needs the real VIIPER server on 127.0.0.1:3242)
        // ========================================================================

        /// <summary>Full live round-trip against the installed viiper.exe: real
        /// bus/create + keyboard add + handshake + HID report + bus/remove. Kept
        /// [Explicit] so normal `dotnet test` stays hermetic — run it with
        /// `--filter FullyQualifiedName~LiveServer_Smoke`. A stray F13 tap is
        /// queued briefly and released; harmless in almost any focused app.</summary>
        [Test, Explicit("Live check — requires the real VIIPER server on 127.0.0.1:3242.")]
        public void LiveServer_Smoke()
        {
            var live = new ViiperEngine();
            live.Initialize();
            Assert.That(live.Available, Is.True, "server answered ping but device setup failed");
            live.SetState(new[] { new Chord(ModifierFlags.None, "F13") });
            Thread.Sleep(150);
            live.ReleaseAll();
            Thread.Sleep(150);
            live.Dispose();
        }

        // ==== Helpers =============================================================

        private bool ServerSeen(string request)
        {
            lock (_server.Requests)
                return _server.Requests.Contains(request);
        }

        private static bool WaitUntil(Func<bool> condition, int timeoutMs = 3000)
        {
            long start = Environment.TickCount;
            while (Environment.TickCount - start < timeoutMs)
            {
                if (condition()) return true;
                Thread.Sleep(10);
            }
            return condition();
        }

        private static int DeadPort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
    }

    /// <summary>A tiny VIIPER server speaking the old protocol, hermetic for tests:
    /// ping/bus/create/bus-add return canned responses on one-shot connections; a
    /// connection whose first line is a `bus/N/devId` handshake becomes the device's
    /// report stream and records every [mod][count][usages...] packet. KillDeviceStreams
    /// closes those report streams to simulate an unplug.</summary>
    internal sealed class MockViiperServer
    {
        public readonly List<string> Requests = new();
        public byte[] LastReport = new byte[0];
        public int ReportCount;

        private TcpListener _listener;
        private volatile bool _running;
        private Thread _acceptor;
        private readonly List<NetworkStream> _deviceStreams = new();

        public int Port { get; private set; }

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _running = true;
            _acceptor = new Thread(AcceptLoop) { IsBackground = true };
            _acceptor.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
        }

        public void KillDeviceStreams()
        {
            lock (Requests)
            {
                foreach (NetworkStream ns in _deviceStreams)
                    try { ns.Close(); } catch { }
                _deviceStreams.Clear();
            }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch { return; }
                var t = new Thread(() => Handle(client)) { IsBackground = true };
                t.Start();
            }
        }

        private void Handle(TcpClient client)
        {
            try
            {
                using (client)
                using (NetworkStream ns = client.GetStream())
                {
                    string line = ReadLine(ns);

                    lock (Requests)
                        if (line != null) Requests.Add(line);

                    if (line == "ping")
                        WriteAndClose(ns, "VIIPER ok");
                    else if (line == "bus/create")
                        WriteAndClose(ns, "{\"busId\":0}");
                    else if (line != null && line.StartsWith("bus/") && line.Contains("/add "))
                        WriteAndClose(ns, "{\"devId\":\"kbd1\"}");
                    else if (line != null && line.StartsWith("bus/remove"))
                        WriteAndClose(ns, "{\"ok\":true}");
                    else if (line != null && line.StartsWith("bus/")
                        && line.IndexOf('/', "bus/".Length) > 0)
                    {
                        lock (Requests) _deviceStreams.Add(ns);
                        ReadReports(ns);
                        lock (Requests) _deviceStreams.Remove(ns);
                    }
                    else
                        WriteAndClose(ns, "?");
                }
            }
            catch { }
        }

        /// <summary>First frame of any connection: the null-terminated request.</summary>
        private static string ReadLine(NetworkStream ns)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < 512; i++)
            {
                int b = ns.ReadByte();
                if (b < 0) break;
                if (b == 0) return sb.ToString();
                sb.Append((char)b);
            }
            return sb.ToString();
        }

        private static void WriteAndClose(NetworkStream ns, string response)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(response);
            ns.Write(bytes, 0, bytes.Length);
            ns.Flush();
        }

        /// <summary>Device stream mode: parse a byte stream of [mod][count][usage…]
        /// reports (tolerating partial frame delivery), keep only the last one.</summary>
        private void ReadReports(NetworkStream ns)
        {
            byte[] pending = new byte[128];
            int count = 0;
            while (_running)
            {
                int n;
                try { n = ns.Read(pending, count, pending.Length - count); }
                catch { break; }
                if (n <= 0) break;
                count += n;

                int pos = 0;
                while (count - pos >= 2)
                {
                    byte len = pending[pos + 1];
                    if (count - pos - 2 < len) break;
                    var report = new byte[2 + len];
                    Array.Copy(pending, pos, report, 0, report.Length);
                    pos += report.Length;
                    lock (Requests)
                    {
                        LastReport = report;
                        ReportCount++;
                    }
                }
                if (pos > 0)
                {
                    Array.Copy(pending, pos, pending, 0, count - pos);
                    count -= pos;
                }
            }
        }
    }
}