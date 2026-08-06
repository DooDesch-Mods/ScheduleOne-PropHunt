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

            app.OnCall("ph.snapshot", _ => Snapshot())
               .OnCall("ph.begin", _ => _host.BeginMatch())
               .OnCall("ph.next", _ => _host.BeginNextRound())
               .OnCall("ph.endround", _ => _host.EndRound())
               .OnCall("ph.hub", _ => _host.ReturnToHub())
               .OnCall("ph.map", arg => _host.SwitchSafehouse(ParseInt(arg, 1)))
               .OnCall("ph.set", Set)
               .OnCall("ph.preset", name => _host.ApplyPreset(name))
               .OnCall("ph.kick", arg => _host.Kick(ParseId(arg)))
               .OnCall("ph.prop.roll", _ => _host.RollProp())
               .OnCall("ph.prop.clear", _ => _host.ClearProp());
        }

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

        private static string Set(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return "error";

            int split = arg.IndexOf('\n');
            if (split <= 0) return "error";

            return _host.SetSetting(arg.Substring(0, split), arg.Substring(split + 1));
        }

        // ---- snapshot ----

        internal static string Snapshot()
        {
            if (_host == null || !_host.Available) return Json.Object().Add("ok", false).Close();

            LocalView me = _host.Me;
            SafehouseView house = _host.Safehouse;

            return Json.Object()
                .Add("ok", true)
                .Add("host", _host.IsHost)
                .Add("phase", _host.Phase)
                .Add("round", _host.RoundNumber)
                .Add("winner", _host.Winner)
                .Add("now", _host.Now)
                .Add("ends", _host.PhaseEndsAt)
                .Add("phaseLen", _host.PhaseLength)
                .Add("nextRound", _host.SecondsUntilNextRound)
                .Add("whistle", _host.SecondsToWhistle)
                .Add("rotation", _host.SecondsToPropRotation)
                .Add("hidersAlive", _host.AliveHiders)
                .Add("lobby", _host.LobbyMembers)
                .Add("becomable", _host.BecomablePropCount)
                .Add("me", Local(me))
                .Add("players", Roster())
                .Add("settings", SettingsArray())
                .Add("presets", Strings(_host.Presets))
                .Add("safehouse", Json.Object()
                    .Add("name", house.Name)
                    .Add("code", house.Code)
                    .Add("options", house.OptionCount)
                    .Add("ready", house.Ready))
                .Add("awards", Awards())
                .Close();
        }

        private static Json Local(LocalView me)
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

            string image = me.PropId >= 0 ? _host.PropImage(me.PropId) : null;
            if (image != null) j.Add("propImage", image);

            return j;
        }

        private static Json Roster()
        {
            var arr = Json.Array();

            foreach (RosterEntry p in _host.Roster)
            {
                var j = Json.Object()
                    .Add("id", Id(p.Id))
                    .Add("name", p.Name)
                    .Add("role", p.Role)
                    .Add("eliminated", p.Eliminated)
                    .Add("self", p.Self)
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
                    string propImage = _host.PropImage(p.PropId);
                    if (propImage != null) j.Add("propImage", propImage);
                }

                if (p.Self) j.Add("hp", p.Hp).Add("maxHp", p.MaxHp);

                string face = _host.PlayerImage(p.Id);
                if (face != null) j.Add("face", face);

                arr.Item(j);
            }

            return arr;
        }

        private static Json SettingsArray()
        {
            var arr = Json.Array();

            foreach (SettingView s in _host.Settings)
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

        private static Json Awards()
        {
            var arr = Json.Array();
            foreach (AwardView a in _host.Awards)
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
