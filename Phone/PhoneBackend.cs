using System;
using System.Collections.Generic;
using System.Globalization;
using Sideload.Api;

namespace PropHunt.Phone
{
    /// <summary>
    /// The mod half of the PropHunt phone app: one snapshot out, ten commands in.
    ///
    /// Deliberately free of Unity and IL2CPP so the shipped <c>app.js</c> can be run against these very handlers
    /// in a headless test (see <c>Workspace/Tests/PropHunt.Tests</c>). Everything game-shaped arrives through
    /// <see cref="IPhoneHost"/>.
    ///
    /// One snapshot instead of forty calls, because a call crosses the bridge as a string and the page needs the
    /// whole picture on every render anyway. Countdowns are NOT in it as seconds - it carries absolute host-time
    /// deadlines and the host's clock, and the page subtracts. A synced countdown would be wrong by the transport
    /// delay and by whatever two Windows machines disagree about; a deadline plus an offset is right for as long
    /// as it is on screen.
    /// </summary>
    internal static class PhoneBackend
    {
        private static IPhoneHost _host;
        private static AppHandle _app;
        private static string _pushed;

        internal static void Install(AppHandle app, IPhoneHost host)
        {
            _app = app;
            _host = host;

            foreach (KeyValuePair<string, Func<string, string>> route in Handlers(host)) app.OnCall(route.Key, route.Value);
        }

        /// <summary>
        /// Every call the page may make, as data.
        ///
        /// Install walks this and so does the headless harness, which is the point: the reference pattern has the
        /// test re-declare the call names in a switch of its own, and a name added on one side only comes back as
        /// an empty string with no warning - the page renders an empty state and the test passes on it. Sharing the
        /// table makes that particular silent pass impossible.
        /// </summary>
        internal static Dictionary<string, Func<string, string>> Handlers(IPhoneHost host) =>
            new Dictionary<string, Func<string, string>>(StringComparer.Ordinal)
            {
                ["ph.snapshot"] = _ => Snapshot(host),
                ["ph.begin"] = _ => host.BeginMatch(),
                ["ph.next"] = _ => host.BeginNextRound(),
                ["ph.endround"] = _ => host.EndRound(),
                ["ph.hub"] = _ => host.ReturnToHub(),
                ["ph.map"] = arg => host.SwitchSafehouse(ParseInt(arg, 1)),
                ["ph.set"] = arg => Set(host, arg),
                ["ph.preset"] = name => host.ApplyPreset(name),
                ["ph.kick"] = arg => host.Kick(ParseId(arg)),
                ["ph.prop.roll"] = _ => host.RollProp(),
                ["ph.prop.clear"] = _ => host.ClearProp(),
            };

        /// <summary>
        /// Call once per frame from the mod's update loop. Emits only when something the page draws actually
        /// changed - never from inside a handler, which would re-enter the script engine while it is still on
        /// the stack.
        /// </summary>
        internal static void Tick()
        {
            if (_app == null || _host == null) return;

            string now = Fingerprint();
            if (now == _pushed) return;

            _pushed = now;
            _app.Emit("ph.changed");
        }

        /// <summary>
        /// What a rebuild depends on. Countdowns are absent on purpose - the page owns those - and so is the
        /// settings blob WHILE THE LOCAL PLAYER IS THE HOST: their own edit comes back as a change one frame
        /// later and would rebuild the row under their finger. A client has no such problem and wants it.
        /// </summary>
        private static string Fingerprint()
        {
            if (!_host.Available) return "none";

            var sb = new System.Text.StringBuilder(160);
            sb.Append(PhoneImages.Revision).Append('|')   // a batch of pictures landed - blank tiles can fill in
              .Append(_host.Phase).Append('|')
              .Append(_host.RoundNumber).Append('|')
              .Append(_host.Winner).Append('|')
              .Append(_host.IsHost ? 'H' : 'c').Append('|')
              .Append(_host.AliveHiders).Append('|')
              .Append(_host.LobbyMembers).Append('|')
              .Append(_host.Safehouse.Code).Append('|')
              .Append(_host.ActivePreset).Append('|')
              .Append(_host.Safehouse.Ready ? 1 : 0).Append('|');

            LocalView me = _host.Me;
            sb.Append(me.Role).Append(me.Eliminated ? 'x' : '-')
              .Append(me.Hp).Append('/').Append(me.MaxHp).Append('|')
              .Append(me.PropId).Append(me.Locked ? 'L' : '-').Append('|')
              .Append(me.DecoysUsed).Append(':').Append(me.ConcussUsed).Append(':').Append(me.Changes).Append('|')
              .Append(me.Downed ? 'D' : '-').Append(me.Outside ? 'O' : '-').Append('|');

            foreach (RosterEntry p in _host.Roster)
                sb.Append(p.Id).Append(p.Role[0]).Append(p.Eliminated ? 'x' : '-').Append(p.Score).Append(',');

            if (!_host.IsHost)
            {
                sb.Append('|');
                foreach (SettingView s in _host.Settings) sb.Append(s.Key).Append('=').Append(s.Value).Append(';');
            }

            return sb.ToString();
        }

        private static string Set(IPhoneHost host, string arg)
        {
            if (string.IsNullOrEmpty(arg)) return "error";

            int split = arg.IndexOf('\n');
            if (split <= 0) return "error";

            return host.SetSetting(arg.Substring(0, split), arg.Substring(split + 1));
        }

        // ---- snapshot ----

        internal static string Snapshot() => Snapshot(_host);

        internal static string Snapshot(IPhoneHost host)
        {
            if (host == null || !host.Available) return Json.Object().Add("ok", false).Close();

            LocalView me = host.Me;
            SafehouseView house = host.Safehouse;

            return Json.Object()
                .Add("ok", true)
                .Add("host", host.IsHost)
                .Add("phase", host.Phase)
                .Add("round", host.RoundNumber)
                .Add("winner", host.Winner)
                .Add("now", host.Now)
                .Add("ends", host.PhaseEndsAt)
                .Add("phaseLen", host.PhaseLength)
                .Add("nextRound", host.SecondsUntilNextRound)
                .Add("whistle", host.SecondsToWhistle)
                .Add("rotation", host.SecondsToPropRotation)
                .Add("hidersAlive", host.AliveHiders)
                .Add("lobby", host.LobbyMembers)
                .Add("becomable", host.BecomablePropCount)
                .Add("me", Local(host, me))
                .Add("players", Roster(host))
                .Add("settings", SettingsArray(host))
                .Add("presets", Strings(host.Presets))
                .Add("activePreset", host.ActivePreset ?? "")
                .Add("baselinePreset", host.BaselinePreset ?? "")
                .Add("safehouse", Json.Object()
                    .Add("name", house.Name)
                    .Add("code", house.Code)
                    .Add("options", house.OptionCount)
                    .Add("ready", house.Ready))
                .Add("awards", Awards(host))
                .Close();
        }

        private static Json Local(IPhoneHost host, LocalView me)
        {
            var j = Json.Object()
                .Add("id", Id(me.Id))
                .Add("role", me.Role)
                .Add("eliminated", me.Eliminated)
                .Add("spectating", me.Spectating)
                .Add("hp", me.Hp)
                .Add("maxHp", me.MaxHp)
                .Add("hunterHp", me.HunterHp)
                .Add("hunterMaxHp", me.HunterMaxHp)
                .Add("prop", me.PropId)
                .Add("propName", me.PropName ?? "")
                .Add("locked", me.Locked)
                .Add("changes", me.Changes)
                .Add("maxChanges", me.MaxChanges)
                .Add("freeChanges", me.FreeChangesNow)
                .Add("decoys", me.DecoysUsed)
                .Add("maxDecoys", me.MaxDecoys)
                .Add("conc", me.ConcussUsed)
                .Add("maxConc", me.MaxConcuss)
                .Add("downed", me.Downed)
                .Add("downedLeft", me.DownedSecondsLeft)
                .Add("outside", me.Outside)
                .Add("water", me.InWater)
                .Add("grace", me.OobGrace);

            string image = me.PropId >= 0 ? host.PropImage(me.PropId) : null;
            if (image != null) j.Add("propImage", image);

            return j;
        }

        private static Json Roster(IPhoneHost host)
        {
            var arr = Json.Array();

            foreach (RosterEntry p in host.Roster)
            {
                var j = Json.Object()
                    .Add("id", Id(p.Id))
                    .Add("name", p.Name)
                    .Add("role", p.Role)
                    .Add("eliminated", p.Eliminated)
                    .Add("self", p.Self)
                    .Add("friend", p.Friend)
                    .Add("catches", p.Catches)
                    .Add("survived", p.SurvivedSeconds)
                    .Add("score", p.Score)
                    .Add("hits", p.HitsDealt)
                    .Add("baits", p.DecoyBaits)
                    .Add("stuns", p.StunsLanded)
                    .Add("smashed", p.DecoysSmashed)
                    .Add("taunts", p.Taunts);

                // Only ever set for the local player or for someone already out - see IPhoneHost.
                if (p.PropId >= 0)
                {
                    j.Add("prop", p.PropId).Add("propName", p.PropName ?? "");
                    string propImage = host.PropImage(p.PropId);
                    if (propImage != null) j.Add("propImage", propImage);
                }

                if (p.Self) j.Add("hp", p.Hp).Add("maxHp", p.MaxHp);

                string face = host.PlayerImage(p.Id);
                if (face != null) j.Add("face", face);

                arr.Item(j);
            }

            return arr;
        }

        private static Json SettingsArray(IPhoneHost host)
        {
            var arr = Json.Array();

            foreach (SettingView s in host.Settings)
            {
                var j = Json.Object()
                    .Add("key", s.Key)
                    .Add("label", s.Label)
                    .Add("hint", s.Hint ?? "")
                    .Add("cat", s.Category)
                    .Add("type", s.Type)
                    .Add("unit", s.Unit ?? "")
                    .Add("value", s.Value ?? "")
                    .Add("def", s.Default ?? "")
                    .Add("min", Num(s.Min))
                    .Add("max", Num(s.Max))
                    .Add("step", Num(s.Step))
                    .Add("whole", s.WholeNumbers);

                if (s.Options.Length > 0) j.Add("options", Strings(s.Options)).Add("values", Strings(s.Values));

                arr.Item(j);
            }

            return arr;
        }

        private static Json Awards(IPhoneHost host)
        {
            var arr = Json.Array();
            foreach (AwardView a in host.Awards)
                arr.Item(Json.Object().Add("label", a.Label).Add("name", a.Name).Add("value", a.Value));
            return arr;
        }

        private static Json Strings(IReadOnlyList<string> values)
        {
            var arr = Json.Array();
            if (values != null) foreach (string v in values) arr.Item(v ?? "");
            return arr;
        }

        /// <summary>
        /// A Steam id as a decimal STRING, never a number: it needs 64 bits and JavaScript's number type carries
        /// 53, so <c>JSON.parse</c> would round the last digits off and the page would kick the wrong player.
        /// </summary>
        private static string Id(ulong id) => id.ToString(CultureInfo.InvariantCulture);

        /// <summary>Sliders carry fractional steps (0.1 on tag range), so these cross as strings and the page
        /// parses them. Invariant culture, or a German machine writes "0,1" and JSON.parse gives up.</summary>
        private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static int ParseInt(string s, int fallback) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

        private static ulong ParseId(string s) =>
            ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong v) ? v : 0UL;
    }
}
