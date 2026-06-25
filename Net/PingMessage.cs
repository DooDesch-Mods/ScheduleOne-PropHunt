using System;
using System.Text;
using SteamNetworkLib.Models;

namespace PropHunt.Net
{
    /// <summary>
    /// Phase 0 spike message: a trivial ping used only to prove that SteamNetworkLib round-trips a custom
    /// P2P message between clients over the game's own Steam lobby. Not part of the real gamemode protocol.
    /// </summary>
    public class PingMessage : P2PMessage
    {
        public override string MessageType => "PHUNT_PING";

        public int Counter { get; set; }
        public string Note { get; set; } = string.Empty;

        public override byte[] Serialize()
            => Encoding.UTF8.GetBytes(Counter.ToString() + "|" + (Note ?? string.Empty));

        public override void Deserialize(byte[] data)
        {
            string s = Encoding.UTF8.GetString(data ?? Array.Empty<byte>());
            int bar = s.IndexOf('|');
            if (bar < 0) { Counter = 0; Note = s; return; }
            int.TryParse(s.Substring(0, bar), out int c);
            Counter = c;
            Note = s.Substring(bar + 1);
        }
    }
}
