#if DEBUG
using System;
using System.Collections.Generic;
using UnityEngine;
using PropHunt.Disguise;

namespace PropHunt.Game
{
    /// <summary>
    /// Console-driven self-tests that exercise the PLAYER-facing paths without a keyboard or a second machine.
    ///
    /// The point is crash coverage. Everything below runs the same code a real round runs - the disguise build,
    /// the phone tabs, the map ring - and reports what happened, so a null reference or an interop throw shows up
    /// here instead of in front of an audience. It cannot judge whether anything LOOKS right; that still needs eyes.
    ///
    /// Debug-only and console-only, per the project rule that dev tooling is never a hotkey.
    /// </summary>
    internal static class SoloHarness
    {
        /// <summary>phsolo: run a match with one real player. A stand-in lobby member takes the hunter slot so the
        /// real player is the hider, which is the side that actually has a disguise, a whistle and a rotation.</summary>
        internal static void StartSolo()
        {
            var s = Core.Session;
            if (s == null) { Core.Log.Warning("phsolo: no active session - run phhost first."); return; }
            GameModeController.DebugSoloMode = true;
            Core.Log.Msg("phsolo: solo mode on (a stand-in member fills the hunter slot). Beginning match...");
            s.BeginMatch();
        }

        /// <summary>phbecome &lt;text&gt;: force the local player into the first prop whose name or key contains the
        /// text (empty = a random one). This is the whole disguise pipeline - clone build, strip, hitbox, collision
        /// height - on demand, instead of having to aim at something.</summary>
        internal static void Become(string filter)
        {
            var s = Core.Session;
            if (s == null) { Core.Log.Warning("phbecome: no active session."); return; }

            int id = -1;
            string label = filter;
            if (string.IsNullOrEmpty(filter))
            {
                id = PropCatalog.RandomId(s.LocalPropId);
                label = "(random)";
            }
            else
            {
                foreach (var e in PropCatalog.Entries())
                {
                    if (e == null) continue;
                    if ((e.Name != null && e.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (e.Key != null && e.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
                    { id = e.Id; label = e.Key; break; }
                }
            }

            if (id < 0) { Core.Log.Warning($"phbecome: nothing matches \"{filter}\"."); return; }
            Core.Log.Msg($"phbecome: requesting prop {id} ({label}), size {PropCatalog.SizeOf(id):F2}m.");
            s.RequestSelectProp(id);
        }

        /// <summary>phbecomeall: build EVERY prop in the catalog as a real clone, one after another, and report the
        /// ones that throw or come out empty. The disguise build is the most interop-heavy path in the mod and the
        /// one most likely to meet a prop nobody has ever worn; this walks all of them in a few seconds.</summary>
        internal static void BuildAll()
        {
            var entries = PropCatalog.Entries();
            int ok = 0, failed = 0, empty = 0;
            var broken = new List<string>();

            foreach (var e in entries)
            {
                if (e == null) continue;
                GameObject go = null;
                try
                {
                    go = PropClone.Build(e, "ph_buildall");
                    if (go == null) { failed++; broken.Add(e.Key + " (null)"); continue; }
                    if (!PropClone.TryGetPropLocalBounds(go, out var b) || b.size.sqrMagnitude <= 0.0001f)
                    { empty++; broken.Add(e.Key + " (no visible mesh)"); continue; }
                    ok++;
                }
                catch (Exception ex) { failed++; broken.Add(e.Key + " (threw: " + ex.Message + ")"); }
                finally { if (go != null) { try { UnityEngine.Object.DestroyImmediate(go); } catch { } } }
            }

            Core.Log.Msg($"phbecomeall: {ok} built, {failed} failed, {empty} with nothing to show (of {entries.Count}).");
            foreach (var b in broken) Core.Log.Warning("phbecomeall   " + b);
        }

        /// <summary>phphone: build every tab of the phone app into a throwaway container. A tab that throws would
        /// otherwise only show up when a player opens it mid-round, which is the worst time to find out.</summary>
        internal static void PhoneSelfTest()
        {
            var ctl = Core.Session;
            if (ctl == null) { Core.Log.Warning("phphone: no active session."); return; }

            for (int tab = 0; tab < PropHunt.Phone.PhoneScreens.TabLabels.Length; tab++)
            {
                GameObject host = null;
                try
                {
                    host = new GameObject("ph_phonetest_" + tab);
                    host.AddComponent<RectTransform>();
                    PropHunt.Phone.PhoneScreens.Build(host.transform, ctl, tab, ctl.IsHost, host.transform);
                    int rows = host.transform.childCount;
                    int deep = CountDescendants(host.transform);
                    Core.Log.Msg($"phphone: tab {tab} \"{PropHunt.Phone.PhoneScreens.TabLabels[tab]}\" built ok " +
                                 $"({rows} root child(ren), {deep} object(s) total).");
                }
                catch (Exception e)
                {
                    Core.Log.Error($"phphone: tab {tab} \"{PropHunt.Phone.PhoneScreens.TabLabels[tab]}\" THREW - {e}");
                }
                finally { if (host != null) { try { UnityEngine.Object.DestroyImmediate(host); } catch { } } }
            }
        }

        private static int CountDescendants(Transform t)
        {
            int n = 0;
            for (int i = 0; i < t.childCount; i++) { n += 1 + CountDescendants(t.GetChild(i)); }
            return n;
        }

        /// <summary>phmapring: report whether the play-area ring exists on the phone map and where it thinks it is.
        /// Numbers, not looks - but a ring at the wrong coordinates or a missing MapApp shows up immediately.</summary>
        internal static void MapRingReport()
        {
            var ctl = Core.Session;
            if (ctl == null) { Core.Log.Warning("phmapring: no active session."); return; }
            try
            {
                var map = PlayerSingleton<Il2CppScheduleOne.UI.Phone.Map.MapApp>.Instance;
                if (map == null) { Core.Log.Warning("phmapring: MapApp not built yet."); return; }
                Core.Log.Msg($"phmapring: MapApp ok, PoIContainer={(map.PoIContainer == null ? "NULL" : "ok")}.");

                var mpu = Singleton<Il2CppScheduleOne.Map.MapPositionUtility>.Instance;
                if (mpu == null) { Core.Log.Warning("phmapring: MapPositionUtility missing."); return; }

                var st = ctl.State;
                Vector2 c = mpu.GetMapPosition(new Vector3(st.AreaX, 0f, st.AreaZ));
                Vector2 edge = mpu.GetMapPosition(new Vector3(st.AreaX + Mathf.Max(1f, st.AreaRadius), 0f, st.AreaZ));
                Core.Log.Msg($"phmapring: area=({st.AreaX:F0},{st.AreaZ:F0}) r={st.AreaRadius:F0}m -> " +
                             $"map centre=({c.x:F1},{c.y:F1}) radius={(edge - c).magnitude:F1} map units.");

                // Walk the container's children instead of GameObject.Find: the phone's map app is closed unless
                // someone is looking at it, so the ring sits in an INACTIVE hierarchy and Find - which only ever
                // returns active objects - reports it missing even when it is there and correct.
                Transform ring = null;
                for (int i = 0; i < map.PoIContainer.childCount; i++)
                {
                    var ch = map.PoIContainer.GetChild(i);
                    if (ch != null && ch.name == "ph_area_ring") { ring = ch; break; }
                }
                if (ring == null)
                {
                    Core.Log.Msg($"phmapring: no ring among the {map.PoIContainer.childCount} POI-container child(ren) " +
                                 "- it is created on the first round tick after the map app exists.");
                    return;
                }
                var rt = ring.GetComponent<RectTransform>();
                var img = ring.GetComponent<UnityEngine.UI.Image>();
                Core.Log.Msg($"phmapring: ring activeSelf={ring.gameObject.activeSelf} " +
                             $"pos=({rt.anchoredPosition.x:F1},{rt.anchoredPosition.y:F1}) " +
                             $"size=({rt.sizeDelta.x:F1}x{rt.sizeDelta.y:F1}) " +
                             $"sprite={(img == null || img.sprite == null ? "MISSING" : "ok")} " +
                             $"sibling={ring.GetSiblingIndex()} of {map.PoIContainer.childCount}.");

                // Everything above can read "correct" while nothing is on screen, which is exactly the case that
                // needed diagnosing. These are the reasons a correct ring stays invisible:
                //  - the Image is disabled, transparent, or the alpha got lost,
                //  - the container is scaled/offset by zoom so the ring's band falls outside the visible rect,
                //  - a Mask/RectMask2D above it clips it away.
                // Does the generated texture actually contain the ring? SetPixels32 takes an array across the IL2CPP
                // boundary, and an upload that quietly does nothing leaves a plausible-looking sprite that draws
                // nothing at all. Sampling it is the only way to tell that apart from a geometry problem.
                try
                {
                    var tex = img != null && img.sprite != null ? img.sprite.texture : null;
                    if (tex != null)
                    {
                        int n = tex.width, mid = n / 2;
                        int bandX = (int)((n - 1) * 0.5f * 0.965f) + mid;   // inside the band
                        Core.Log.Msg($"phmapring: sprite {n}x{n} alpha centre={tex.GetPixel(mid, mid).a:F2} " +
                                     $"band={tex.GetPixel(Mathf.Min(bandX, n - 1), mid).a:F2} " +
                                     $"outside={tex.GetPixel(n - 1, n - 1).a:F2} (band must be ~1, outside 0)");
                    }
                }
                catch (Exception te) { Core.Log.Msg("phmapring: sprite not sampleable - " + te.Message); }

                // sizeDelta is what the ring is sized through, but it only equals the drawn size while the anchors
                // coincide. Reporting the RESOLVED rect alongside it is what catches an inherited stretch, which
                // silently multiplies the ring by the container's size.
                Core.Log.Msg($"phmapring: resolved rect=({rt.rect.width:F0}x{rt.rect.height:F0}) " +
                             $"vs sizeDelta=({rt.sizeDelta.x:F0}x{rt.sizeDelta.y:F0}) " +
                             $"anchors {rt.anchorMin}/{rt.anchorMax} -> " +
                             $"{(Mathf.Abs(rt.rect.width - rt.sizeDelta.x) < 1f ? "sizeDelta is the size" : "STRETCHED - the ring is parent-sized, not prop-sized")}");

                Core.Log.Msg($"phmapring: image enabled={(img == null ? "?" : img.enabled.ToString())} " +
                             $"colour={(img == null ? "?" : $"rgba({img.color.r:F2},{img.color.g:F2},{img.color.b:F2},{img.color.a:F2})")} " +
                             $"raycast={(img == null ? "?" : img.raycastTarget.ToString())} " +
                             $"localScale=({rt.localScale.x:F2},{rt.localScale.y:F2}).");

                // Draw order against the map's own background. A canvas draws the FIRST sibling first, so anything
                // ordered before the opaque map image is painted over - which is invisible while every readable
                // property still says the overlay is fine. That is exactly how this went unexplained for three runs.
                try
                {
                    var bg = map.BackgroundImage;
                    if (bg == null) Core.Log.Msg("phmapring: MapApp.BackgroundImage is null.");
                    else
                    {
                        bool sibling = bg.rectTransform != null && bg.rectTransform.parent == ring.parent;
                        int bgIdx = sibling ? bg.rectTransform.GetSiblingIndex() : -1;
                        int myIdx = ring.GetSiblingIndex();
                        Core.Log.Msg($"phmapring: background '{bg.name}' sameParent={sibling} " +
                                     $"bgSibling={bgIdx} ringSibling={myIdx} bgColour={bg.color} bgEnabled={bg.enabled} " +
                                     $"-> {(sibling && myIdx < bgIdx ? "RING IS BEHIND THE MAP - it cannot be seen" : "ring draws after the map")}");
                    }
                }
                catch (Exception be) { Core.Log.Msg("phmapring: background check failed - " + be.Message); }

                // The out-of-bounds wash: a separate layer that must span the whole map and carry a hole where the play
                // area is. If it is missing, the boundary is a hairline again - and a hairline circle wider than the map
                // window is invisible from the middle of one's own area, which is when it matters most.
                Transform wash = null;
                for (int i = 0; i < map.PoIContainer.childCount; i++)
                {
                    var ch = map.PoIContainer.GetChild(i);
                    if (ch != null && ch.name == "ph_area_outside") { wash = ch; break; }
                }
                if (wash == null) Core.Log.Msg("phmapring: no out-of-bounds wash (ph_area_outside) - boundary only.");
                else
                {
                    var wrt = wash.GetComponent<RectTransform>();
                    var wimg = wash.GetComponent<UnityEngine.UI.Image>();
                    var wtex = wimg != null && wimg.sprite != null ? wimg.sprite.texture : null;
                    string holes = "?";
                    if (wtex != null)
                    {
                        // SCAN for the two states rather than sampling named spots. The hole sits wherever the play
                        // area is, which is not the middle of the map - sampling the texture's centre reported a
                        // uniform wash for a texture that was in fact correct.
                        int n = wtex.width, step = Mathf.Max(1, n / 48);
                        float lo = 1f, hi = 0f;
                        int clearCount = 0, total = 0;
                        for (int y = step / 2; y < n; y += step)
                            for (int x = step / 2; x < n; x += step)
                            {
                                float a2 = wtex.GetPixel(x, y).a;
                                if (a2 < lo) lo = a2;
                                if (a2 > hi) hi = a2;
                                if (a2 < 0.1f) clearCount++;
                                total++;
                            }
                        holes = $"min={lo:F2} max={hi:F2} clear={clearCount}/{total}";
                    }
                    Core.Log.Msg($"phmapring: wash active={wash.gameObject.activeSelf} " +
                                 $"rect=({wrt.rect.width:F0}x{wrt.rect.height:F0}) sibling={wash.GetSiblingIndex()} " +
                                 $"alpha {holes} (a hole needs min LOW and max HIGH)");
                }

                var poi = map.PoIContainer;
                Core.Log.Msg($"phmapring: container rect=({poi.rect.width:F0}x{poi.rect.height:F0}) " +
                             $"scale=({poi.localScale.x:F2},{poi.localScale.y:F2}) " +
                             $"anchoredPos=({poi.anchoredPosition.x:F1},{poi.anchoredPosition.y:F1}).");

                // Is any of it inside the visible window? Compared in the CLIPPING RECT'S OWN space, never in world
                // space: the phone's canvas is a plane standing in the world, so world x/y drops one of the screen's
                // two axes and every rect collapses to a line - which is what a first attempt at this reported.
                var p = ring.parent;
                int depth = 0;
                while (p != null && depth++ < 8)
                {
                    var mask2d = p.GetComponent<UnityEngine.UI.RectMask2D>();
                    var mask = p.GetComponent<UnityEngine.UI.Mask>();
                    if (mask2d == null && mask == null) { p = p.parent; continue; }

                    var prt = p.GetComponent<RectTransform>();
                    if (prt == null) { p = p.parent; continue; }
                    Rect clip = prt.rect;
                    Rect mine = RectIn(prt, rt);
                    bool overlaps = mine.Overlaps(clip);
                    // A ring is a thin band, so an overlapping BOX still shows nothing when the window sits in the
                    // hole: that happens when the window is entirely nearer the centre than the band.
                    Vector2 ringCentre = mine.center;
                    float rOuter = mine.width * 0.5f;
                    float rInner = rOuter * 0.89f;   // matches the sprite's band
                    float dxMax = Mathf.Max(Mathf.Abs(clip.xMin - ringCentre.x), Mathf.Abs(clip.xMax - ringCentre.x));
                    float dyMax = Mathf.Max(Mathf.Abs(clip.yMin - ringCentre.y), Mathf.Abs(clip.yMax - ringCentre.y));
                    float furthest = Mathf.Sqrt(dxMax * dxMax + dyMax * dyMax);
                    bool bandReachable = furthest >= rInner && overlaps;

                    Core.Log.Msg($"phmapring: window '{p.name}' ({(mask2d != null ? "RectMask2D" : "Mask")}) " +
                                 $"rect=({clip.width:F0}x{clip.height:F0}) x=[{clip.xMin:F0},{clip.xMax:F0}] y=[{clip.yMin:F0},{clip.yMax:F0}]");
                    Core.Log.Msg($"phmapring: ring in that space x=[{mine.xMin:F0},{mine.xMax:F0}] " +
                                 $"y=[{mine.yMin:F0},{mine.yMax:F0}] bandRadius={rInner:F0}..{rOuter:F0} " +
                                 $"furthestCorner={furthest:F0} -> {(bandReachable ? "BAND IS REACHABLE" : overlaps ? "window sits INSIDE the ring - nothing to see" : "ring is FULLY OUTSIDE the window")}.");
                    p = p.parent;
                }
            }
            catch (Exception e) { Core.Log.Error("phmapring THREW - " + e); }
        }

        /// <summary>
        /// <paramref name="what"/>'s rect expressed in <paramref name="space"/>'s local coordinates, so two rects at
        /// different depths of a UI hierarchy can be compared directly.
        ///
        /// The corners are transformed one at a time on purpose. GetWorldCorners fills a caller-supplied array, and an
        /// array handed across the IL2CPP boundary comes back untouched - it reports a zero-sized rect at the origin
        /// and every comparison built on it silently agrees with itself.
        /// </summary>
        private static Rect RectIn(RectTransform space, RectTransform what)
        {
            var r = what.rect;
            Vector3 a = space.InverseTransformPoint(what.TransformPoint(new Vector3(r.xMin, r.yMin, 0f)));
            Vector3 b = space.InverseTransformPoint(what.TransformPoint(new Vector3(r.xMax, r.yMax, 0f)));
            return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        }
    }
}
#endif
