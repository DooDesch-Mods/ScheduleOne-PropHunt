using System;
using SteamNetworkLib;
#if IL2CPP
using Il2CppSteamworks;
#else
using Steamworks;
#endif

namespace PropHunt.Net
{
    /// <summary>
    /// Phase 0 networking spike. Wraps a single SteamNetworkLib client. SteamNetworkLib registers a global
    /// LobbyEnter_t Steam callback in its constructor, so once <see cref="Initialize"/> has run it
    /// AUTO-ATTACHES to whatever Steam lobby the game itself joins/creates - no explicit JoinLobby needed.
    /// We just initialize early and pump <see cref="Tick"/> every frame.
    /// </summary>
    internal static class PropHuntNet
    {
        private static SteamNetworkClient _client;
        private static bool _ready;
        private static int _pingCounter;
        private static int _idlePump;   // frame counter used to throttle the P2P pump while outside a lobby

        /// <summary>True once the SteamNetworkLib client has initialized (Steam available).</summary>
        internal static bool Ready => _ready;

        /// <summary>True while attached to a Steam lobby (the game's co-op lobby once joined).</summary>
        internal static bool InLobby { get { try { return _ready && _client.IsInLobby; } catch { return false; } } }

        /// <summary>True when the local player owns the current lobby (host-authoritative pivot).</summary>
        internal static bool IsHost { get { try { return _ready && _client.IsHost; } catch { return false; } } }

        internal static void Initialize()
        {
            if (_ready) return;
            try
            {
                _client = new SteamNetworkClient();
                if (_client.TryInitialize(out var err))
                {
                    _client.RegisterMessageHandler<PingMessage>(OnPing);
                    _ready = true;
                    Core.Log.Msg("[Net] SteamNetworkLib ready (auto-attaches to the game's Steam lobby).");
                }
                else
                {
                    Core.LogDebug("[Net] not ready yet: " + (err?.Message ?? "unknown") + " (will retry on next scene).");
                    _client = null;
                }
            }
            catch (Exception e)
            {
                Core.Log.Warning("[Net] init failed: " + e.Message);
                _client = null;
            }
        }

        /// <summary>Pumps Steam callbacks and processes incoming P2P each frame. No-op until ready.</summary>
        internal static void Tick()
        {
            if (!_ready) return;
            // Outside a co-op lobby (e.g. single-player) there is no PropHunt P2P to process - pumping the Steam P2P
            // socket every frame costs ~0.2ms/frame for nothing (found via the Snitch profiler: PropHunt.Net section).
            // While idle, pump only ~1 frame in 32 (still picks up a freshly joined lobby within a fraction of a
            // second); run every frame only once actually in a session. The && short-circuits so the counter advances
            // only while idle, and the lobby check stays cheap.
            if (!InLobby && (++_idlePump & 31) != 0) return;
            try { _client.ProcessIncomingMessages(); }
            catch (Exception e) { Core.LogDebug("[Net] tick error: " + e.Message); }
        }

        /// <summary>Phase 0 gate 1+2: broadcast a ping to all lobby members.</summary>
        internal static void SendPing()
        {
            if (!_ready) { Core.Log.Warning("[Net] not ready; ping skipped."); return; }
            if (!InLobby) { Core.Log.Warning("[Net] not in a co-op lobby; ping skipped (host or join a session first)."); return; }
            try
            {
                var msg = new PingMessage { Counter = ++_pingCounter, Note = "from " + _client.LocalPlayerId64 };
                _client.BroadcastMessage(msg);
                Core.Log.Msg($"[Net] -> broadcast PHUNT_PING #{msg.Counter} (members={MemberCount()}, IsHost={IsHost}).");
            }
            catch (Exception e) { Core.Log.Warning("[Net] ping send failed: " + e.Message); }
        }

        internal static int MemberCount() { try { return _client.GetLobbyMembers().Count; } catch { return -1; } }

        private static void OnPing(PingMessage msg, CSteamID sender)
        {
            Core.Log.Msg($"[Net] <- PHUNT_PING #{msg.Counter} from {sender.m_SteamID} ({msg.Note}). " +
                         "Seeing this on the OTHER machine means gates 1+2 (BiggerLobbies join + SteamNetworkLib round-trip) pass.");
        }
    }
}
