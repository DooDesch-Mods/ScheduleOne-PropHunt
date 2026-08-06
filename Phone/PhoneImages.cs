using System;
using System.Collections.Generic;
using Sideload.Api;

namespace PropHunt.Phone
{
    /// <summary>
    /// Hands the page real pictures - a photographed prop, a player's Steam avatar - a few per frame.
    ///
    /// The pacing is the whole reason this is a class. A readback costs a frame slice, and a roster of twenty plus
    /// the props they were caught as is far more work than one frame should do; asking for everything the moment
    /// the app opens is a stutter you can see. So a request only ever queues, the page gets text until the picture
    /// lands, and the queue drains at <see cref="PerTick"/> a frame.
    ///
    /// <see cref="Revision"/> is bumped ONCE per drained batch rather than once per picture. That distinction is
    /// borrowed from Reflash/Game/SpriteFeed.cs, where the naive version cost a visible quarter-second: a revision
    /// change rebuilds the whole page, and four pictures a frame meant six rebuilds of a long list.
    /// </summary>
    internal static class PhoneImages
    {
        /// <summary>Readbacks a single frame may pay for.</summary>
        private const int PerTick = 2;

        /// <summary>
        /// Tries before a key is written off. A Steam avatar is fetched asynchronously by Steam itself and a prop's
        /// meshes may not be loaded the instant the app opens, so an early miss is a "not yet" rather than a "never" -
        /// but something that will never arrive must not be retried for the whole session.
        /// </summary>
        private const int MaxAttempts = 20;

        private static AppHandle _app;

        private static readonly HashSet<string> Ready = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> Dead = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> Attempts = new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly List<string> Queue = new List<string>();

        private static bool _unannounced;

        /// <summary>Changes when a batch of pictures has finished arriving. Part of the snapshot fingerprint, so the
        /// page learns its blank tiles have filled in.</summary>
        internal static int Revision { get; private set; }

        internal static void UseHandle(AppHandle app) => _app = app;

        /// <summary>Forget everything. A new session may have a different prop catalog and a different roster.</summary>
        internal static void Reset()
        {
            Ready.Clear();
            Dead.Clear();
            Attempts.Clear();
            Queue.Clear();
            _unannounced = false;
        }

        /// <summary>The key to put in an <c>&lt;img src="s1://..."&gt;</c> for this prop, or null while none exists.</summary>
        internal static string Prop(int propId) => propId < 0 ? null : Want("prop/" + propId);

        /// <summary>The key for this player's portrait, or null while none exists.</summary>
        internal static string Player(ulong steamId) => steamId == 0 ? null : Want("face/" + steamId);

        /// <summary>
        /// Ask for a key: hand it back when it is already published, otherwise queue it and answer null. Answering
        /// null rather than a key that paints nothing is the point - an img with no picture behind it draws an
        /// empty box, and the page has a text row it can show instead.
        /// </summary>
        private static string Want(string key)
        {
            if (Ready.Contains(key)) return key;
            if (Dead.Contains(key)) return null;
            if (!Queue.Contains(key)) Queue.Add(key);
            return null;
        }

        /// <summary>Drain a little of the queue. Call once per frame from the mod's update loop.</summary>
        internal static void Tick()
        {
            if (_app == null || Queue.Count == 0)
            {
                if (_unannounced) { _unannounced = false; Revision++; }
                return;
            }

            int done = 0;
            int published = 0;

            while (Queue.Count > 0 && done < PerTick)
            {
                string key = Queue[0];
                Queue.RemoveAt(0);
                done++;

                byte[] png = Produce(key);
                if (png != null && png.Length > 0)
                {
                    Ready.Add(key);
                    Attempts.Remove(key);
                    _app.Image(key, png);
                    published++;
                    continue;
                }

                Attempts.TryGetValue(key, out int failures);
                Attempts[key] = ++failures;

                if (failures >= MaxAttempts) { Dead.Add(key); continue; }

                Queue.Add(key);   // back of the line, so one stubborn key cannot starve the rest
            }

            // Announce on PUBLISHES, not on work done: a key that keeps coming back as "not yet" would otherwise
            // keep the pass looking busy for ever and the page would never hear about the ones that did arrive.
            if (published > 0) { _unannounced = true; return; }
            if (_unannounced) { _unannounced = false; Revision++; }
        }

        private static byte[] Produce(string key)
        {
            try
            {
                if (key.StartsWith("prop/", StringComparison.Ordinal))
                    return int.TryParse(key.Substring(5), out int id) ? PropShot.Render(id) : null;

                if (key.StartsWith("face/", StringComparison.Ordinal))
                    return ulong.TryParse(key.Substring(5), out ulong id) ? SteamAvatar.Png(id) : null;
            }
            catch (Exception e) { Core.LogDebug("[PropHunt] image '" + key + "' failed: " + e.Message); }

            return null;
        }
    }
}
