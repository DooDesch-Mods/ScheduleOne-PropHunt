using System;
using System.Globalization;
using System.Text;
using SteamNetworkLib.Models;

namespace PropHunt.Net
{
    // The real PropHunt P2P protocol. Client -> host messages are INTENTS the host validates; host -> all
    // messages are transient EVENTS. Durable state (roles, props, eliminations, phase) rides the GameState
    // HostSyncVar instead - these messages are only for one-shot actions. Sender identity is the handler's
    // CSteamID arg, so intents don't carry it. Mirrors PingMessage's plain UTF8 codec (no JSON dependency).

    internal static class MsgCodec
    {
        internal static string Str(byte[] d) => Encoding.UTF8.GetString(d ?? Array.Empty<byte>());
        internal static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s ?? string.Empty);
        internal static int I(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        internal static ulong U(string s) => ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0UL;
        internal static string Of(int v) => v.ToString(CultureInfo.InvariantCulture);
        internal static string Of(ulong v) => v.ToString(CultureInfo.InvariantCulture);
        internal static float F(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
        internal static string Of(float v) => v.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Client -> host: a hider chose a prop catalog id (honoured only in the Hiding phase).</summary>
    public class SelectPropMessage : P2PMessage
    {
        public override string MessageType => "PH_SELECT";
        public int PropId { get; set; }
        public override byte[] Serialize() => MsgCodec.Bytes(MsgCodec.Of(PropId));
        public override void Deserialize(byte[] data) => PropId = MsgCodec.I(MsgCodec.Str(data));
    }

    /// <summary>Client -> host: a hider locks (true) or unlocks (false) the current prop.</summary>
    public class LockPropMessage : P2PMessage
    {
        public override string MessageType => "PH_LOCK";
        public bool Locked { get; set; }
        public override byte[] Serialize() => MsgCodec.Bytes(Locked ? "1" : "0");
        public override void Deserialize(byte[] data) => Locked = MsgCodec.Str(data) == "1";
    }

    /// <summary>Client -> host: set the manual facing (yaw, degrees) of the hider's current prop ([F]+mouse).</summary>
    public class RotatePropMessage : P2PMessage
    {
        public override string MessageType => "PH_ROT";
        public float Yaw { get; set; }
        public override byte[] Serialize() => MsgCodec.Bytes(MsgCodec.Of(Yaw));
        public override void Deserialize(byte[] data) => Yaw = MsgCodec.F(MsgCodec.Str(data));
    }

    // NOTE: messages MUST carry a non-empty payload - SteamNetworkLib does not deliver empty-body messages
    // (an empty Serialize() silently never reaches the host), so these carry the drop/origin coordinates.

    /// <summary>Client -> host: drop a decoy of the hider's current prop at the given world spot + facing ([Q]).</summary>
    public class DropDecoyMessage : P2PMessage
    {
        public override string MessageType => "PH_DECOY";
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Yaw { get; set; }
        public override byte[] Serialize() => MsgCodec.Bytes($"{MsgCodec.Of(X)};{MsgCodec.Of(Y)};{MsgCodec.Of(Z)};{MsgCodec.Of(Yaw)}");
        public override void Deserialize(byte[] data)
        {
            var p = MsgCodec.Str(data).Split(';');
            if (p.Length >= 4) { X = MsgCodec.F(p[0]); Y = MsgCodec.F(p[1]); Z = MsgCodec.F(p[2]); Yaw = MsgCodec.F(p[3]); }
        }
    }

    /// <summary>Client -> host: set off a concussion grenade at the hider's position ([G]). Host stuns nearby hunters.</summary>
    public class ConcussMessage : P2PMessage
    {
        public override string MessageType => "PH_CONC";
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public override byte[] Serialize() => MsgCodec.Bytes($"{MsgCodec.Of(X)};{MsgCodec.Of(Y)};{MsgCodec.Of(Z)}");
        public override void Deserialize(byte[] data)
        {
            var p = MsgCodec.Str(data).Split(';');
            if (p.Length >= 3) { X = MsgCodec.F(p[0]); Y = MsgCodec.F(p[1]); Z = MsgCodec.F(p[2]); }
        }
    }

    /// <summary>Client -> host: a hunter claims a catch on the given victim (host re-validates geometry).</summary>
    public class ClaimTagMessage : P2PMessage
    {
        public override string MessageType => "PH_TAG";
        public ulong VictimSteamId { get; set; }
        public override byte[] Serialize() => MsgCodec.Bytes(MsgCodec.Of(VictimSteamId));
        public override void Deserialize(byte[] data) => VictimSteamId = MsgCodec.U(MsgCodec.Str(data));
    }

    /// <summary>Client -> host: the sender's own local player has left the play area.</summary>
    public class OutOfBoundsMessage : P2PMessage
    {
        public override string MessageType => "PH_OOB";
        public override byte[] Serialize() => Array.Empty<byte>();
        public override void Deserialize(byte[] data) { }
    }

    /// <summary>Host -> all: force a reveal taunt on the given hider (transient one-shot effect).</summary>
    public class TauntMessage : P2PMessage
    {
        public override string MessageType => "PH_TAUNT";
        public ulong SteamId { get; set; }
        public override byte[] Serialize() => MsgCodec.Bytes(MsgCodec.Of(SteamId));
        public override void Deserialize(byte[] data) => SteamId = MsgCodec.U(MsgCodec.Str(data));
    }
}
