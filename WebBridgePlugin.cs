// WebBridgePlugin.cs — AOSharp entrypoint: loud banner + /wb command
using System;
using AOSharp.Core;
using AOSharp.Core.UI;

namespace WebBridge
{
    public class WebBridgePlugin : AOPluginEntry
    {
        [Obsolete] // AOPluginEntry.Run is marked obsolete in newer SDKs
        public override void Run(string args)
        {
            Chat.WriteLine("====================================");
            Chat.WriteLine(" WebBridgePlugin.Run() CALLED");
            Chat.WriteLine(" args: " + (args ?? "(null)"));
            Chat.WriteLine("====================================");

            Core.Start(args, line => Chat.WriteLine(line));

            // /wb ping | /wb status
            Chat.RegisterCommand("wb", (string cmd, string[] argv, ChatWindow win) =>
            {
                var sub = (argv != null && argv.Length > 0) ? argv[0].ToLowerInvariant() : "";
                switch (sub)
                {
                    case "ping":
                        Chat.WriteLine("[/wb] pong " + DateTime.Now.ToString("HH:mm:ss"));
                        break;

                    case "status":
                        Chat.WriteLine("[/wb] port=" + Core.Port +
                                       " token=" + Core.Token.Substring(0, 8) + "… temp=" + Core.TempLogPath);
                        break;

                    default:
                        Chat.WriteLine("[/wb] commands: ping | status");
                        break;
                }
            });

            Chat.WriteLine("[WebBridge] Registered /wb command");
            Chat.WriteLine("[WebBridge] Open http://127.0.0.1:" + Core.Port + "/");
        }

        public override void Teardown()
        {
            Chat.WriteLine("[WebBridge] Teardown called");
            Core.Stop(line => Chat.WriteLine(line));
        }
    }
}
