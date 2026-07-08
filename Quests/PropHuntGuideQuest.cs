using System;
using System.Reflection;
using UnityEngine;
using S1API.Quests;

namespace PropHunt.Quests
{
    /// <summary>
    /// The PropHunt session guidance quest (header "PropHunt", on the gamemode quest-block allow-list). It is begun
    /// ONCE and never completed; <see cref="GuideQuest"/> adds one objective ENTRY per host action ("Start the match",
    /// "Start round N") and checks each off as it happens, so the journal reads as a progression of completed objectives.
    ///
    /// Two vanilla quest rules drive the design:
    ///  - A quest auto-ends only when ALL its entries are Completed. A never-completed ANCHOR entry (added here, left
    ///    Inactive) therefore keeps the quest alive forever - and an Inactive entry shows no HUD row, so it is invisible.
    ///  - An entry's visible checkmark animation fires only on an Active -> Completed transition. So a new objective
    ///    entry must be put ACTIVE (Begin) before it shows, and only then does Complete() draw the ✓. Adding an entry
    ///    and completing it straight from Inactive shows nothing.
    /// </summary>
    public class PropHuntGuideQuest : Quest
    {
        protected override string Title => GuideQuest.Title;   // "PropHunt" - allow-listed past the gamemode quest-block
        protected override string Description => "Run and follow the prop hunt from the PropHunt app on your phone.";
        protected override bool AutoBegin => true;

        // Provide our own icon so the base Quest ctor never falls back to ContactsApp.Instance.AppIcon (null in a
        // PropHunt scratch session -> throws).
        private static Sprite _icon;
        private static bool _iconAbsent;
        protected override Sprite QuestIcon
        {
            // Re-load if a scene unload (quit -> re-host) destroyed the cached sprite (Unity '== null' is true for a
            // destroyed object), so the journal icon survives re-hosting instead of falling back / NRE-ing.
            get
            {
                if (_icon != null) return _icon;
                if (_iconAbsent) return null;
                _icon = LoadIcon();
                if (_icon == null) _iconAbsent = true;
                return _icon;
            }
        }

        private static Sprite LoadIcon()
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("PropHunt.Assets.phone_icon.png"))
                {
                    if (s == null) return null;
                    var bytes = new byte[s.Length];
                    int read = 0;
                    while (read < bytes.Length) { int n = s.Read(bytes, read, bytes.Length - read); if (n <= 0) break; read += n; }
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
                    tex.hideFlags = HideFlags.DontUnloadUnusedAsset;   // survive scene unloads (re-host) so the icon persists
                    if (tex.LoadImage(bytes))
                    {
                        var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                        if (sp != null) sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
                        return sp;
                    }
                    return null;
                }
            }
            catch { return null; }
        }

        protected override void OnCreated()
        {
            base.OnCreated();
            try
            {
                // Anchor entry, left INACTIVE: invisible (no HUD row) but counts as "not Completed", so the quest never
                // auto-ends as objectives are checked off. Never begun, never completed.
                AddEntry("PropHunt session");
            }
            catch (Exception e) { Core.Log.Warning("[PropHunt] guide quest OnCreated failed: " + e.Message); }
        }

        /// <summary>Add a new objective entry, put it ACTIVE (so its row shows and a later Complete() animates the ✓),
        /// with a valid POI on the player to avoid the compass null-flicker. Returns it so the manager can complete it.</summary>
        internal QuestEntry AddObjective(string title)
        {
            var lp = Player.Local;
            QuestEntry e = lp != null ? AddEntry(title, lp.transform.position) : AddEntry(title);
            try { e.Begin(); } catch { }   // Inactive -> Active: the HUD row appears now; Complete() later draws the checkmark
            return e;
        }
    }
}
