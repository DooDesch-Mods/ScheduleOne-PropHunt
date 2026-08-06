using System;
using System.Collections.Generic;
using Il2CppScheduleOne.DevUtilities;
using PropHunt.Game;
using UnityEngine;
using UnityEngine.UI;

namespace PropHunt.Patches
{
    /// <summary>
    /// Hides the phone apps a PropHunt round has no use for: Messages, Products, Contacts, Dealers, Deliveries. They
    /// belong to the business the round is not about, and a hider tapping through them is a hider not hiding. The map,
    /// the gamemode app and WhatsDab stay.
    ///
    /// Driven from the home screen's own icon container rather than from the App singletons: an icon is a prefab clone
    /// carrying its label in a "Label" child (HomeScreen.GenerateAppIcon), and the label is the only thing that
    /// identifies which app it belongs to without walking the whole App&lt;T&gt; generic zoo.
    ///
    /// Every icon we hide is remembered, so leaving the round puts back exactly what we took away - and nothing else.
    /// The container is rebuilt when the phone is reopened, so this runs per frame and compares before touching.
    /// </summary>
    internal static class PhoneAppVisibility
    {
        private static readonly HashSet<string> Unwanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Messages", "Products", "Contacts", "Dealers", "Deliveries",
        };

        private static readonly List<GameObject> _hidden = new List<GameObject>();
        private static bool _applied;

        /// <summary>Pumped every frame from the controller. Cheap: it walks at most a dozen icons and only calls
        /// SetActive when the state actually differs.</summary>
        internal static void Tick(bool roundActive)
        {
            if (roundActive) Hide();
            else if (_applied) Restore();
        }

        private static void Hide()
        {
            try
            {
                var home = PlayerSingleton<Il2CppScheduleOne.UI.Phone.HomeScreen>.Instance;
                var container = home != null ? home.appIconContainer : null;
                if (container == null) return;

                int n = container.childCount;
                for (int i = 0; i < n; i++)
                {
                    var icon = container.GetChild(i);
                    if (icon == null) continue;
                    var go = icon.gameObject;
                    if (!go.activeSelf) continue;                 // already hidden (by us or by the game)
                    if (!Unwanted.Contains(LabelOf(icon))) continue;
                    go.SetActive(false);
                    _hidden.Add(go);
                    _applied = true;
                }
            }
            catch (Exception e) { Core.LogDebug("[PropHunt] hiding phone apps failed: " + e.Message); }
        }

        private static void Restore()
        {
            for (int i = 0; i < _hidden.Count; i++)
            {
                try { if (_hidden[i] != null) _hidden[i].SetActive(true); }
                catch (Exception e) { Core.LogDebug("[PropHunt] restoring a phone app failed: " + e.Message); }
            }
            _hidden.Clear();
            _applied = false;
        }

        /// <summary>The icon's visible caption. Null when the clone has no Label child - a shape change in the prefab
        /// then hides nothing instead of hiding the wrong app.</summary>
        private static string LabelOf(Transform icon)
        {
            try
            {
                var label = icon.Find("Label");
                var text = label != null ? label.GetComponent<Text>() : null;
                return text != null ? text.text : null;
            }
            catch { return null; }
        }
    }
}
