// WebBridgeCore.cs — Local Web UI + hardened in‑game command injector (SendInput)
// - No System.Web
// - TcpListener (no URLACL)
// - Attractive single‑page UI
// - Sends /commands using SetForegroundWindow + SendInput (UNICODE typing)
// - Writes verbose logs to %TEMP%\AO_WebBridge_log.txt and optionally to AO chat

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WebBridge
{
    internal static class Core
    {
        private static readonly CancellationTokenSource Cts = new CancellationTokenSource();
        private static readonly ConcurrentQueue<string> LogQ = new ConcurrentQueue<string>();
        private static readonly Dictionary<string, Action<Dictionary<string, string>>> Actions =
            new Dictionary<string, Action<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);

        private static bool _started;
        private static readonly string _token = Guid.NewGuid().ToString("N");
        private static readonly string _logPath = Path.Combine(Path.GetTempPath(), "AO_WebBridge_log.txt");
        private static int _port = 8787;
        private static TcpListener _listener;

        public static string TempLogPath => _logPath;
        public static int Port => _port;
        public static string Token => _token;

        public static void Start(string args = null, Action<string> chat = null)
        {
            if (_started) return;
            _started = true;

            // Optional arg: "port=8788" or "8788"
            if (!string.IsNullOrWhiteSpace(args))
            {
                var s = args.Trim();
                if (s.StartsWith("port=", StringComparison.OrdinalIgnoreCase)) s = s.Substring(5);
                if (int.TryParse(s, out var p) && p > 0 && p < 65536) _port = p;
            }

            SafeLog("=== WebBridge boot ===", chat);
            SafeLog("Temp log: " + _logPath, chat);

            // ---- Debug actions
            Register("test", _ => SafeLog("TEST action clicked", chat));
            Register("status", _ => SafeLog($"STATUS: port={_port} token={_token.Substring(0, 8)}… pid={Process.GetCurrentProcess().Id}", chat));
            Register("ping", _ => SafeLog("PONG " + DateTime.Now.ToString("HH:mm:ss"), chat));

            // ---- Diagnostics for command path
            Register("diag_say", _ => Cmd.Send("/say WebBridge says hi!"));
            Register("diag_raw", q => { var t = q.TryGetValue("t", out var x) ? x : "raw"; InputPath.TypeUnicodeAndEnter(t); SafeLog("diag_raw sent: " + t, chat); });
            Register("diag_focus", _ => { var ok = AoWindow.Focus(AoWindow.Find()); SafeLog("diag_focus: " + ok, chat); });

            // ---- Generic helpers
            Register("cmd", q => {
                if (!q.TryGetValue("text", out var t) || string.IsNullOrWhiteSpace(t)) { SafeLog("cmd requires text=/something", chat); return; }
                SafeLog("[cmd] " + t, chat);
                Cmd.Send(t);
            });

            Register("keys", q => {
                if (!q.TryGetValue("combo", out var c) || string.IsNullOrWhiteSpace(c)) { SafeLog("keys requires combo=CTRL+ALT+S", chat); return; }
                SafeLog("[keys] " + c, chat);
                KeyCombo.Send(c);
            });

            // ---- Stacker (adjust if your commands differ)
            Register("stack_now", _ => Cmd.Send("/stacker stack"));
            Register("stack_toggle", _ => Cmd.Send("/stacker toggle"));
            Register("stack_meter", _ => Cmd.Send("/stacker meter"));
            Register("stack_perf", _ => Cmd.Send("/stacker perf"));
            Register("stack_delay", q => { var ms = q.TryGetValue("ms", out var v) ? v : "600"; Cmd.Send("/stacker delay " + ms); });

            // ---- TradeSkill Leveler (TSL)
            Register("tsl_ui", _ => Cmd.Send("/tsl ui"));
            Register("tsl_go", _ => Cmd.Send("/tsl go"));
            Register("tsl_stop", _ => Cmd.Send("/tsl stop"));
            Register("tsl_recipe", q => { if (!q.TryGetValue("name", out var r) || string.IsNullOrWhiteSpace(r)) { SafeLog("tsl_recipe requires name", chat); return; } Cmd.Send("/tsl recipe " + r); });

            // ---- SmokeBag
            Register("smokebag_open", _ => Cmd.Send("/smokebag"));
            Register("smokebag_auto", q => Cmd.Send("/smokebag " + (q.TryGetValue("mode", out var m) ? m : "auto")));

            // ---- BagForce (examples)
            Register("bf_pair_go", _ => Cmd.Send("/bf pair go"));
            Register("bf_pair_stop", _ => Cmd.Send("/bf pair stop"));
            Register("bf_status", _ => Cmd.Send("/bf status"));

            // ---- Server
            Task.Run(() => RunTcpServer("127.0.0.1", _port, Cts.Token, chat));

            Banner("====================================", chat);
            Banner(" WebBridge running", chat);
            Banner(" URL: http://127.0.0.1:" + _port + "/", chat);
            Banner(" Token: " + _token, chat);
            Banner(" Actions: stack_*, tsl_*, smokebag_*, bf_*, cmd, keys, diag_*", chat);
            Banner("====================================", chat);
        }

        public static void Stop(Action<string> chat = null)
        {
            try { Cts.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            SafeLog("WebBridge stopped.", chat);
        }

        public static void Register(string name, Action<Dictionary<string, string>> handler)
        {
            if (string.IsNullOrWhiteSpace(name) || handler == null) return;
            Actions[name.Trim()] = handler;
        }

        // ---------------- TCP server ----------------
        private static async Task RunTcpServer(string host, int port, CancellationToken ct, Action<string> chat)
        {
            try
            {
                var ip = IPAddress.Parse(host);
                _listener = new TcpListener(ip, port);
                _listener.Start();
                SafeLog("TcpListener started on " + host + ":" + port, chat);

                while (!ct.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleClient(client), ct);
                }
            }
            catch (Exception ex)
            {
                SafeLog("Server fatal: " + ex, chat);
                Banner("[WebBridge] FATAL: " + ex.Message, chat);
            }
        }

        private static void HandleClient(TcpClient client)
        {
            using (client)
            using (var ns = client.GetStream())
            {
                ns.ReadTimeout = 10000;
                ns.WriteTimeout = 10000;

                // read headers only
                var buf = new byte[8192];
                var sb = new StringBuilder();
                try
                {
                    int n;
                    while ((n = ns.Read(buf, 0, buf.Length)) > 0)
                    {
                        sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                        if (sb.ToString().Contains("\r\n\r\n")) break;
                        if (sb.Length > 65536) break;
                    }
                }
                catch { }
                var request = sb.ToString();
                if (string.IsNullOrEmpty(request)) return;

                var lineEnd = request.IndexOf("\r\n", StringComparison.Ordinal);
                var line = lineEnd > 0 ? request.Substring(0, lineEnd) : request;
                var parts = line.Split(' ');
                string target = parts.Length > 1 ? parts[1] : "/";
                var path = target;
                var query = "";
                var qIdx = target.IndexOf('?');
                if (qIdx >= 0) { path = target.Substring(0, qIdx); query = target.Substring(qIdx + 1); }

                var q = ParseQuery(query);

                if (path == "/")
                {
                    WriteHttp(ns, 200, "text/html; charset=utf-8", HtmlIndex());
                    return;
                }
                if (path == "/api/logs")
                {
                    if (!CheckToken(q)) { WriteHttp(ns, 403, "text/plain", "forbidden"); return; }
                    WriteHttp(ns, 200, "text/plain; charset=utf-8", ReadAllLogs());
                    return;
                }
                if (path == "/api/do")
                {
                    if (!CheckToken(q)) { WriteHttp(ns, 403, "text/plain", "forbidden"); return; }
                    if (!q.TryGetValue("a", out var act) || string.IsNullOrWhiteSpace(act)) { WriteHttp(ns, 400, "text/plain", "missing a"); return; }

                    if (Actions.TryGetValue(act, out var h))
                    {
                        try { h(q); WriteHttp(ns, 200, "text/plain", "OK"); }
                        catch (Exception ex) { SafeLog("Action ERR " + act + ": " + ex.Message); WriteHttp(ns, 500, "text/plain", ex.Message); }
                    }
                    else WriteHttp(ns, 404, "text/plain", "unknown action");
                    return;
                }

                WriteHttp(ns, 404, "text/plain", "not found");
            }
        }

        // ---------------- HTML ----------------
        private static string HtmlIndex()
        {
            // NOTE: old-C# friendly (no string interpolation / backticks)
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"/>")
              .Append("<title>AOSharp Control – WebBridge</title>")
              .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"/>")
              .Append("<style>")
              .Append(":root{--bg:#0b0f14;--fg:#e6f1ff;--muted:#8aa0bf;--card:#0f172a;--line:#1f2a3a;--accent:#49b6ff;--good:#39d98a;--warn:#ffb020}")
              .Append("body{margin:0;background:var(--bg);color:var(--fg);font:16px/1.4 system-ui,sans-serif}")
              .Append("header{padding:14px 18px;border-bottom:1px solid var(--line);background:#0c1220}")
              .Append("h1{margin:0;font-size:18px;letter-spacing:.5px}")
              .Append(".wrap{max-width:1100px;margin:0 auto;padding:18px}")
              .Append(".grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(260px,1fr));gap:14px}")
              .Append(".card{background:var(--card);border:1px solid var(--line);border-radius:16px;padding:14px;box-shadow:0 6px 18px rgba(0,0,0,.25)}")
              .Append(".btn{background:var(--accent);color:#001018;border:none;padding:10px 12px;border-radius:10px;cursor:pointer;font-weight:700;margin:4px 6px}")
              .Append(".btn.secondary{background:#1e293b;color:var(--fg)}")
              .Append("label,input{display:block}")
              .Append("input{width:100%;background:#0b1220;color:var(--fg);border:1px solid var(--line);border-radius:8px;padding:8px 10px;margin:6px 0}")
              .Append("#logs{white-space:pre-wrap;font-family:Consolas,monospace;max-height:260px;overflow:auto;color:#cbd5e1}")
              .Append("small{color:var(--muted)}")
              .Append("</style></head><body>")
              .Append("<header><h1>AOSharp Control – WebBridge</h1>")
              .Append("<div><small>http://127.0.0.1:").Append(_port).Append(" • token …").Append(Html(_token.Substring(_token.Length - 6))).Append("</small></div>")
              .Append("</header><div class=\"wrap\">")
              .Append("<div class=\"grid\">")

              // STACKER
              .Append("<div class=\"card\"><h3>Stacker</h3>")
              .Append(btn("Stack now", "stack_now"))
              .Append(btn("Toggle auto", "stack_toggle"))
              .Append(btn("Meter", "stack_meter"))
              .Append(btn("Perf", "stack_perf"))
              .Append("<label>Delay (ms)</label><input id=\"delayms\" placeholder=\"600\"/>")
              .Append(btnKV("Set delay", "stack_delay", "ms", "delayms"))
              .Append("</div>")

              // TSL
              .Append("<div class=\"card\"><h3>TradeSkill Leveler</h3>")
              .Append(btn("Open UI", "tsl_ui"))
              .Append(btn("Go", "tsl_go"))
              .Append(btn("Stop", "tsl_stop"))
              .Append("<label>Recipe name</label><input id=\"tslrec\" placeholder=\"e.g. Carbonum\"/>")
              .Append(btnKV("Set recipe", "tsl_recipe", "name", "tslrec"))
              .Append("</div>")

              // SmokeBag
              .Append("<div class=\"card\"><h3>SmokeBag</h3>")
              .Append(btn("Open", "smokebag_open"))
              .Append("<label>Mode</label><input id=\"sbmode\" placeholder=\"auto|fast|safe\"/>")
              .Append(btnKV("Set mode", "smokebag_auto", "mode", "sbmode"))
              .Append("</div>")

              // BagForce
              .Append("<div class=\"card\"><h3>BagForce</h3>")
              .Append(btn("Pair Go", "bf_pair_go"))
              .Append(btn("Pair Stop", "bf_pair_stop"))
              .Append(btn("Status", "bf_status"))
              .Append("</div>")

              // Generic / Diagnostics
              .Append("<div class=\"card\"><h3>Generic</h3>")
              .Append("<label>Slash command</label><input id=\"cmdtxt\" placeholder=\"/stacker stack\"/>")
              .Append(btnKV("Send command", "cmd", "text", "cmdtxt"))
              .Append("<label>Key combo (CTRL+ALT+S)</label><input id=\"combo\" placeholder=\"CTRL+ALT+S\"/>")
              .Append(btnKV("Send keys", "keys", "combo", "combo"))
              .Append(btn("Ping", "ping")).Append(btn("Status", "status")).Append(btn("Test", "test"))
              .Append("<hr/><small>Diagnostics</small>")
              .Append(btn("Say hello", "diag_say"))
              .Append(btnKV("Raw type+enter", "diag_raw", "t", "cmdtxt"))
              .Append(btn("Focus AO", "diag_focus"))
              .Append("</div>")

              // Logs
              .Append("<div class=\"card\" style=\"grid-column:1/-1\"><h3>Logs</h3><div id=\"logs\"></div></div>")

              .Append("</div></div>")
              // JS (XHR, no template literals)
              .Append("<script>")
              .Append("var T='").Append(Html(_token)).Append("';var B=location.protocol+'//'+location.host;")
              .Append("function req(u,m,b,cb){var x=new XMLHttpRequest();x.open(m,u,true);if(m==='POST')x.setRequestHeader('Content-Type','application/x-www-form-urlencoded');x.onreadystatechange=function(){if(x.readyState===4&&cb)cb(x)};x.send(b||null);}")
              .Append("function doA(a){req(B+'/api/do?a='+encodeURIComponent(a)+'&t='+encodeURIComponent(T),'POST',null,function(){refresh()});}")
              .Append("function doKV(a,k,el){var v=document.getElementById(el).value.trim();var body='a='+encodeURIComponent(a)+'&t='+encodeURIComponent(T)+'&'+encodeURIComponent(k)+'='+encodeURIComponent(v);req(B+'/api/do','POST',body,function(){refresh()});}")
              .Append("function refresh(){req(B+'/api/logs?t='+encodeURIComponent(T),'GET',null,function(x){if(x.status===200){var el=document.getElementById('logs');el.textContent=x.responseText;el.scrollTop=el.scrollHeight;}})}")
              .Append("function init(){refresh();setInterval(refresh,1500);}init();")
              .Append("</script></body></html>");
            return sb.ToString();

            string btn(string label, string action)
            {
                return "<button class=\"btn\" onclick=\"doA('" + Html(action) + "')\">" + Html(label) + "</button>";
            }
            string btnKV(string label, string action, string key, string inputId)
            {
                return "<button class=\"btn secondary\" onclick=\"doKV('" + Html(action) + "','" + Html(key) + "','" + Html(inputId) + "')\">" + Html(label) + "</button>";
            }
        }

        private static Dictionary<string, string> ParseQuery(string qs)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(qs)) return dict;
            var parts = qs.Split('&');
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i])) continue;
                var eq = parts[i].IndexOf('=');
                if (eq < 0) dict[UrlDecode(parts[i])] = "";
                else dict[UrlDecode(parts[i].Substring(0, eq))] = UrlDecode(parts[i].Substring(eq + 1));
            }
            return dict;
        }

        private static string UrlDecode(string s)
        {
            try { return Uri.UnescapeDataString(s.Replace("+", " ")); } catch { return s; }
        }
        private static bool CheckToken(Dictionary<string, string> q) { return q.TryGetValue("t", out var t) && t == _token; }
        private static void WriteHttp(NetworkStream ns, int status, string type, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body ?? "");
            var header = "HTTP/1.1 " + status + " OK\r\nContent-Type: " + type + "\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n";
            ns.Write(Encoding.ASCII.GetBytes(header), 0, header.Length); ns.Write(bytes, 0, bytes.Length);
        }
        private static string ReadAllLogs() { try { return File.Exists(_logPath) ? File.ReadAllText(_logPath) : "(no log yet)"; } catch { return string.Join("\n", LogQ.ToArray()); } }
        private static void SafeLog(string msg, Action<string> chat = null) { var line = DateTime.Now.ToString("HH:mm:ss ") + msg; LogQ.Enqueue(line); try { File.AppendAllText(_logPath, line + Environment.NewLine); } catch { } try { chat?.Invoke(line); } catch { } }
        private static void Banner(string msg, Action<string> chat = null) => SafeLog(msg, chat);

        private static string Html(string s)
        {
            if (s == null) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
        }

        // ======================= Command / Input path =========================
        private static class Cmd
        {
            public static void Send(string text)
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                if (text[0] != '/') text = "/" + text;

                // 1) Bring AO to foreground
                var h = AoWindow.Find();
                if (h == IntPtr.Zero) { LogQ.Enqueue("ERR: AO window not found"); return; }
                if (!AoWindow.Focus(h)) LogQ.Enqueue("WARN: SetForegroundWindow failed");

                // 2) open chat (Enter)
                Thread.Sleep(50);
                InputPath.TapVk(VK.RETURN);

                // 3) type unicode + Enter
                Thread.Sleep(40);
                InputPath.TypeUnicode(text);
                Thread.Sleep(30);
                InputPath.TapVk(VK.RETURN);

                LogQ.Enqueue("Sent slash cmd: " + text);
            }
        }

        private static class KeyCombo
        {
            public static void Send(string combo)
            {
                if (string.IsNullOrWhiteSpace(combo)) return;
                var h = AoWindow.Find();
                if (h == IntPtr.Zero) { LogQ.Enqueue("ERR: AO window not found (keys)"); return; }
                if (!AoWindow.Focus(h)) LogQ.Enqueue("WARN: SetForegroundWindow failed");
                Thread.Sleep(40);

                // parse combo like CTRL+ALT+S
                var parts = combo.Split('+');
                var downs = new List<ushort>();
                foreach (var p in parts)
                {
                    var vk = VK.Parse(p.Trim());
                    if (vk == 0) continue;
                    InputPath.KeyDown(vk);
                    downs.Add(vk);
                    Thread.Sleep(15);
                }
                for (int i = downs.Count - 1; i >= 0; i--)
                {
                    InputPath.KeyUp(downs[i]);
                    Thread.Sleep(10);
                }
                LogQ.Enqueue("Sent keys: " + combo);
            }
        }

        private static class AoWindow
        {
            [DllImport("user32.dll", SetLastError = true)] static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
            [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);

            public static IntPtr Find()
            {
                var h = FindWindow(null, "Anarchy Online");
                if (h == IntPtr.Zero) h = FindWindow(null, "Anarchy Online - Shadowlands");
                return h;
            }
            public static bool Focus(IntPtr h) => SetForegroundWindow(h);
        }

        // -------------------- SendInput (global, reliable) -------------------
        private static class InputPath
        {
            [StructLayout(LayoutKind.Sequential)]
            struct INPUT { public uint type; public InputUnion U; }
            [StructLayout(LayoutKind.Explicit)]
            struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; }
            [StructLayout(LayoutKind.Sequential)]
            struct KEYBDINPUT
            {
                public ushort wVk;
                public ushort wScan;
                public uint dwFlags;
                public uint time;
                public IntPtr dwExtraInfo;
            }

            const uint INPUT_KEYBOARD = 1;
            const uint KEYEVENTF_KEYUP = 0x0002;
            const uint KEYEVENTF_UNICODE = 0x0004;
            const uint KEYEVENTF_SCANCODE = 0x0008;

            [DllImport("user32.dll", SetLastError = true)]
            static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

            public static void TapVk(ushort vk)
            {
                KeyDown(vk);
                Thread.Sleep(10);
                KeyUp(vk);
            }

            public static void KeyDown(ushort vk)
            {
                var input = new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new InputUnion
                    {
                        ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = 0, time = 0, dwExtraInfo = IntPtr.Zero }
                    }
                };
                SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
            }

            public static void KeyUp(ushort vk)
            {
                var input = new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new InputUnion
                    {
                        ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = KEYEVENTF_KEYUP, time = 0, dwExtraInfo = IntPtr.Zero }
                    }
                };
                SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
            }

            public static void TypeUnicode(string text)
            {
                if (string.IsNullOrEmpty(text)) return;
                var list = new List<INPUT>(text.Length * 2);
                for (int i = 0; i < text.Length; i++)
                {
                    ushort ch = text[i];
                    list.Add(new INPUT
                    {
                        type = INPUT_KEYBOARD,
                        U = new InputUnion
                        {
                            ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE, time = 0, dwExtraInfo = IntPtr.Zero }
                        }
                    });
                    list.Add(new INPUT
                    {
                        type = INPUT_KEYBOARD,
                        U = new InputUnion
                        {
                            ki = new KEYBDINPUT { wVk = 0, wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, time = 0, dwExtraInfo = IntPtr.Zero }
                        }
                    });
                }
                if (list.Count > 0) SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf(typeof(INPUT)));
            }

            public static void TypeUnicodeAndEnter(string text)
            {
                TypeUnicode(text);
                Thread.Sleep(20);
                TapVk(VK.RETURN);
            }
        }

        private static class VK
        {
            public const ushort RETURN = 0x0D;
            public const ushort CONTROL = 0x11;
            public const ushort MENU = 0x12;   // ALT
            public const ushort SHIFT = 0x10;
            public const ushort V = 0x56;

            public static ushort Parse(string token)
            {
                token = (token ?? "").Trim().ToUpperInvariant();
                if (token == "CTRL" || token == "CONTROL") return CONTROL;
                if (token == "ALT" || token == "MENU") return MENU;
                if (token == "SHIFT") return SHIFT;
                if (token == "V") return V;
                if (token.Length == 1) return (ushort)token[0];
                if (token.StartsWith("KEY_") && token.Length == 5) return (ushort)token[4];
                return 0;
            }
        }
        // =====================================================================
    }
}
