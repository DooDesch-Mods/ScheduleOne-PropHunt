using SteamNetworkLib.Models;

namespace PropHunt.Net
{
    /// <summary>Host -> all: lock (true) or open (false) the doors of the named safehouse property. The door
    /// access flag (PropertyDoorController.PlayerAccess) is a plain field with no FishNet SyncVar, so the host
    /// pushes it explicitly; clients apply it locally. Payload = "propertyCode;1|0" (code has no ';').</summary>
    public class SafehouseDoorLockMessage : P2PMessage
    {
        public override string MessageType => "PH_SH_DOOR";
        public string PropertyCode { get; set; }
        public bool Locked { get; set; }
        public override byte[] Serialize() => MsgCodec.Bytes((PropertyCode ?? "") + ";" + (Locked ? "1" : "0"));
        public override void Deserialize(byte[] data)
        {
            var p = MsgCodec.Str(data).Split(new[] { ';' }, 2);
            PropertyCode = p[0];
            Locked = p.Length >= 2 && p[1] == "1";
        }
    }
}
