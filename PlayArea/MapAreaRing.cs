using UnityEngine;
using UnityEngine.UI;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.UI.Phone.Map;
using PropHunt.Game;

namespace PropHunt.PlayArea
{
    /// <summary>
    /// Draws the round's play area as a ring on the phone map, so a player can see where the boundary runs before
    /// walking into it instead of only meeting the wall in the world.
    ///
    /// It piggybacks on the machinery vanilla already uses for its own markers rather than reinventing it:
    ///   - <see cref="MapPositionUtility.GetMapPosition"/> is the game's own world -> map converter (it drops Y and
    ///     scales x/z by a factor derived from two authored reference points). Calling it twice and taking the
    ///     difference gives the radius in map units without needing to know that factor.
    ///   - The ring is parented under <see cref="MapApp.PoIContainer"/>, the same RectTransform every POI marker
    ///     lives in, so zoom and panning (applied as scale/position on the parent) carry it along for free, and the
    ///     app opening and closing already shows and hides it.
    ///
    /// Client-local UI only - nothing here is networked; the centre and radius ride the normal game state.
    /// </summary>
    internal sealed class MapAreaRing
    {
        private const int TextureSize = 256;
        private const float RingThickness = 0.055f;   // fraction of the radius; thin enough to read as a boundary
        private const float OutsideAlpha = 0.28f;     // the forbidden wash - readable without hiding the streets under it
        private const int OutsideTexture = 512;       // the out-of-bounds mask; its own edge is covered by the crisp band

        private readonly GameModeController _ctl;
        private GameObject _go;          // the boundary band, sized to the area
        private RectTransform _rect;
        private GameObject _outGo;       // the out-of-bounds wash, stretched over the whole map
        private RectTransform _outRect;
        private Image _outImg;
        private Sprite _outSprite;       // the wash sprite this session made
        /// <summary>The band's sprite. Per SESSION, deliberately not static: a Sprite belongs to the scene that made
        /// it, and hosting a new lobby loads a new world. The static cache kept handing out the destroyed sprite of
        /// the previous session - a reference that is not null but draws nothing, which is why the red zone went
        /// missing at random after re-hosting. Building it costs one texture upload.</summary>
        private Sprite _sprite;

        // last drawn values, so the ring is only touched when the area actually changes
        private float _cx, _cz, _radius;

        internal MapAreaRing(GameModeController ctl) { _ctl = ctl; }

        internal void Tick()
        {
            try
            {
                var s = _ctl.State;
                bool want = s != null && s.AreaRadius > 0f && _ctl.RoundActive;
                if (!want) { Hide(); return; }
                if (!EnsureCreated()) return;

                if (Mathf.Approximately(_cx, s.AreaX) && Mathf.Approximately(_cz, s.AreaZ) &&
                    Mathf.Approximately(_radius, s.AreaRadius))
                {
                    if (!_go.activeSelf) _go.SetActive(true);
                    if (_outGo != null && !_outGo.activeSelf) _outGo.SetActive(true);
                    return;
                }

                var mpu = Singleton<MapPositionUtility>.Instance;
                if (mpu == null) return;

                // The difference of two conversions cancels the origin offset, so this is the radius in map units
                // whatever the authored scale happens to be - never a hardcoded metres-per-pixel.
                Vector2 centre = mpu.GetMapPosition(new Vector3(s.AreaX, 0f, s.AreaZ));
                Vector2 edge = mpu.GetMapPosition(new Vector3(s.AreaX + s.AreaRadius, 0f, s.AreaZ));
                float rMap = (edge - centre).magnitude;
                if (rMap <= 0f) return;

                // The band is drawn OUTSIDE the boundary, not on top of it. The sprite's ring sits at 0.89-1.0 of its own
                // radius, so sizing the element to the area radius would lay the line INSIDE the play area and eat into
                // the ground a player is allowed to stand on. Scaling it up by the band's own width instead puts the
                // line's inner edge exactly on the boundary: clear ground right up to the wall, then the line, then red.
                float rOuter = rMap / (1f - RingThickness * 2f);
                _rect.anchoredPosition = centre;
                _rect.sizeDelta = new Vector2(rOuter * 2f, rOuter * 2f);
                _go.SetActive(true);

                // Only remember the area as drawn once BOTH layers actually took it. The band is a transform write and
                // cannot really fail; the wash rebuilds a texture and can, and committing regardless left the previous
                // round's hole on screen for good - the ring had moved to the new safehouse and the red had not, so the
                // clear area sat next to its own boundary. A failed paint now simply repeats next frame.
                if (!PaintOutside(centre, rMap, rOuter)) return;

                _cx = s.AreaX; _cz = s.AreaZ; _radius = s.AreaRadius;
                Core.LogDebug($"map ring: area ({s.AreaX:F0},{s.AreaZ:F0}) r={s.AreaRadius:F0}m -> " +
                              $"map centre ({centre.x:F0},{centre.y:F0}) band {rMap:F0}..{rOuter:F0}");
            }
            catch (System.Exception e) { Core.LogDebug("map ring tick failed: " + e.Message); }
        }

        /// <summary>
        /// Build the ring by CLONING a live POI marker and repurposing it, rather than assembling a GameObject.
        ///
        /// A hand-built RectTransform + Image looked correct in every value that can be read back - active, sized,
        /// sprited, opaque, geometrically inside the map window - and still drew nothing. Everything a marker needs
        /// beyond those values lives on the POI prefab, not in code: the canvas context, the graphic material the
        /// stencil mask rewrites, the FontSetter, whatever a game update adds next. Cloning something the game itself
        /// draws inherits all of it, and there is no list to keep up to date.
        ///
        /// The clone's own children are switched off and the ring is drawn on the root, so nothing of the marker's
        /// appearance survives - only its wiring.
        /// </summary>
        private bool EnsureCreated()
        {
            if (_rect != null) return true;
            var map = PlayerSingleton<MapApp>.Instance;
            if (map == null || map.PoIContainer == null) return false;   // the map app is not built yet

            var probe = ProbeMarker();
            if (probe == null) return false;   // no marker to copy yet - the POIs build themselves on map open

            _go = UnityEngine.Object.Instantiate(probe.gameObject, map.PoIContainer);
            _go.name = "ph_area_ring";
            _rect = _go.GetComponent<RectTransform>();
            if (_rect == null) { UnityEngine.Object.Destroy(_go); _go = null; return false; }

            // The marker's icon and label go away; only its place in the hierarchy is wanted.
            for (int i = _rect.childCount - 1; i >= 0; i--)
            {
                var ch = _rect.GetChild(i);
                if (ch != null) ch.gameObject.SetActive(false);
            }

            // Nothing on it should react to the mouse - the map is clickable and a disc this size would swallow a
            // whole corner of it, including the markers underneath.
            try { var btn = _go.GetComponent<Button>(); if (btn != null) btn.enabled = false; } catch { }
            try { var ev = _go.GetComponent<UnityEngine.EventSystems.EventTrigger>(); if (ev != null) ev.enabled = false; } catch { }

            var img = _go.GetComponent<Image>();
            if (img == null) img = _go.AddComponent<Image>();
            img.sprite = RingSprite();
            img.type = Image.Type.Simple;
            img.preserveAspect = false;
            img.color = new Color(1f, 0.35f, 0.25f, 0.85f);
            img.raycastTarget = false;
            img.enabled = true;

            _rect.localScale = Vector3.one;
            _rect.localRotation = Quaternion.identity;

            // Collapse any stretch to a single anchor point. Tick() sizes the ring through sizeDelta, and sizeDelta only
            // MEANS a size while the two anchors coincide - stretched, it is an inset from the parent and the ring comes
            // out parent-sized plus the diameter. Collapsing to the midpoint keeps where the anchor was.
            if (_rect.anchorMin != _rect.anchorMax)
            {
                Vector2 mid = (_rect.anchorMin + _rect.anchorMax) * 0.5f;
                _rect.anchorMin = _rect.anchorMax = mid;
            }

            SortBehindMarkers(map);
            BuildOutsideLayer(map, probe);

            Core.LogDebug($"map ring: cloned '{probe.name}' -> anchors {_rect.anchorMin}/{_rect.anchorMax} " +
                          $"pivot {_rect.pivot}, sibling {_rect.GetSiblingIndex()} of {map.PoIContainer.childCount}, " +
                          $"image={(img != null)}, outside={(_outRect != null)}");
            _go.SetActive(false);
            return true;
        }

        /// <summary>The out-of-bounds layer: a second clone of the same marker, stretched to fill the map image, sitting
        /// directly under the boundary band so the band's crisp edge lands on top of the wash's soft one.</summary>
        private void BuildOutsideLayer(MapApp map, RectTransform probe)
        {
            try
            {
                _outGo = UnityEngine.Object.Instantiate(probe.gameObject, map.PoIContainer);
                _outGo.name = "ph_area_outside";
                _outRect = _outGo.GetComponent<RectTransform>();
                if (_outRect == null) { UnityEngine.Object.Destroy(_outGo); _outGo = null; return; }

                for (int i = _outRect.childCount - 1; i >= 0; i--)
                {
                    var ch = _outRect.GetChild(i);
                    if (ch != null) ch.gameObject.SetActive(false);
                }
                try { var b = _outGo.GetComponent<Button>(); if (b != null) b.enabled = false; } catch { }
                try { var ev = _outGo.GetComponent<UnityEngine.EventSystems.EventTrigger>(); if (ev != null) ev.enabled = false; } catch { }

                _outImg = _outGo.GetComponent<Image>();
                if (_outImg == null) _outImg = _outGo.AddComponent<Image>();
                _outImg.type = Image.Type.Simple;
                _outImg.preserveAspect = false;
                _outImg.color = new Color(1f, 0.25f, 0.2f, 1f);   // alpha lives in the texture, so the hole stays a hole
                _outImg.raycastTarget = false;
                _outImg.enabled = true;

                // Fill the map image exactly, whatever size it is, so "outside" really is everywhere else.
                _outRect.localScale = Vector3.one;
                _outRect.localRotation = Quaternion.identity;
                _outRect.anchorMin = Vector2.zero;
                _outRect.anchorMax = Vector2.one;
                _outRect.pivot = new Vector2(0.5f, 0.5f);
                _outRect.offsetMin = Vector2.zero;
                _outRect.offsetMax = Vector2.zero;

                _outRect.SetSiblingIndex(Mathf.Max(0, _rect.GetSiblingIndex()));   // just under the band
                _outGo.SetActive(false);
            }
            catch (System.Exception e)
            {
                Core.LogDebug("out-of-bounds layer failed: " + e.Message);
                _outGo = null; _outRect = null; _outImg = null;
            }
        }

        /// <summary>
        /// Tint everything OUTSIDE the area, leaving the play area itself clear.
        ///
        /// Red has to mean forbidden, and what is forbidden is the outside - a wash over the play area says the opposite
        /// of what it means. That flip cannot be done by recolouring the disc, because "outside" is the whole rest of
        /// the map: the overlay has to span the entire map image and carry a hole, so it is a second element stretched
        /// across the container rather than a circle sized to the area.
        ///
        /// <paramref name="rMap"/> is the boundary, <paramref name="rOuter"/> the outer edge of the drawn band - the
        /// hole goes between them, so a player standing against the wall sits on the ring's INNER edge with clear map
        /// behind them, rather than at the far side of a line drawn across ground they are allowed to walk on.
        ///
        /// The hole moves and resizes once per round, so the texture is rebuilt then - not per frame. It is deliberately
        /// low resolution: its own edge is covered by the crisp band drawn on top of it, so the only thing it has to get
        /// right is the wash, and 512 across a 2048-unit map is far more than that needs.
        /// </summary>
        /// <returns>Whether the wash now shows this area. False means try again - the caller must not record the area
        /// as drawn, or a layer that failed once stays wrong for the rest of the session.</returns>
        private bool PaintOutside(Vector2 centreMap, float rMap, float rOuter)
        {
            if (_outRect == null || _outImg == null) return false;
            try
            {
                // GetComponent, never `as`. A managed as-cast does not see the Il2Cpp type and hands back null, so this
                // returned false on every single tick: the band was already repositioned by the caller, the wash never
                // was, and the area was never recorded as drawn. Resizing or moving the play area moved the ring and
                // left the red exactly where it had been.
                var parent = _outRect.parent != null ? _outRect.parent.GetComponent<RectTransform>() : null;
                if (parent == null) return false;
                Rect pr = parent.rect;
                if (pr.width <= 1f || pr.height <= 1f) return false;

                // The band is placed with anchoredPosition, which is measured from its own anchor - so the same point
                // in the parent's rect has to be reconstructed the same way rather than assumed to be the centre.
                Vector2 anchor = _rect != null ? _rect.anchorMin : new Vector2(0.5f, 0.5f);
                Vector2 anchorLocal = new Vector2(pr.xMin + anchor.x * pr.width, pr.yMin + anchor.y * pr.height);
                Vector2 local = anchorLocal + centreMap;

                float cxN = (local.x - pr.xMin) / pr.width;
                float cyN = (local.y - pr.yMin) / pr.height;

                // The hole ends in the MIDDLE of the band, which runs from the boundary (rMap) outward to rOuter.
                // Ending it exactly on either edge leaves a visible sliver of untinted map: this texture is coarse -
                // one texel spans several map units, so its hard edge lands on a grid - and the band carries a soft
                // edge of its own. Half a band-width of overlap swallows both, and no edge a player can see moves.
                float rHole = (rMap + rOuter) * 0.5f;
                float rxN = rHole / pr.width;
                float ryN = rHole / pr.height;
                if (rxN <= 0f || ryN <= 0f) return false;

                int n = OutsideTexture;
                var px = new Color32[n * n];
                byte a = (byte)(Mathf.Clamp01(OutsideAlpha) * 255f);
                for (int y = 0; y < n; y++)
                {
                    float v = (y + 0.5f) / n;
                    float dy = (v - cyN) / ryN;
                    int row = y * n;
                    for (int x = 0; x < n; x++)
                    {
                        float u = (x + 0.5f) / n;
                        float dx = (u - cxN) / rxN;
                        // Inside the area: fully clear. Outside: the wash. An ellipse test rather than a circle one,
                        // so a map image that is not square cannot quietly turn the boundary into an oval.
                        px[row + x] = (dx * dx + dy * dy <= 1f) ? new Color32(255, 255, 255, 0) : new Color32(255, 255, 255, a);
                    }
                }

                var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
                tex.wrapMode = TextureWrapMode.Clamp;   // the edge texel must not repeat around the map
                tex.SetPixels32(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32>(px));
                tex.Apply(false, false);

                // Free only the sprite WE made last round. The clone arrived carrying the marker's own icon sprite,
                // which other markers share - destroying that would have taken their icons with it.
                var mine = _outSprite;
                _outSprite = Sprite.Create(tex, new Rect(0f, 0f, n, n), new Vector2(0.5f, 0.5f));
                _outImg.sprite = _outSprite;
                if (mine != null)
                {
                    try { if (mine.texture != null) UnityEngine.Object.Destroy(mine.texture); UnityEngine.Object.Destroy(mine); } catch { }
                }
                _outGo.SetActive(true);
                return true;
            }
            catch (System.Exception e) { Core.LogDebug("out-of-bounds wash failed: " + e.Message); return false; }
        }

        /// <summary>
        /// Put the ring just above the map image and below every marker.
        ///
        /// This is the whole bug that made the first version invisible. It called SetAsFirstSibling to stay out of the
        /// markers' way - and a UI canvas draws first sibling FIRST, so the ring went underneath the map's own opaque
        /// BackgroundImage, which lives in this same container. Every property read back correctly because nothing was
        /// wrong with the ring; it was simply painted over. Vanilla POIs never hit this: instantiating a child appends
        /// it, so they land after the background by default.
        ///
        /// So: directly after the background when it is a sibling, otherwise last, like a vanilla marker. Sitting under
        /// the markers is still worth having - the ring is a wide, mostly transparent overlay and must not bury them.
        /// </summary>
        private void SortBehindMarkers(MapApp map)
        {
            try
            {
                var bg = map.BackgroundImage;
                if (bg != null && bg.rectTransform != null && bg.rectTransform.parent == _rect.parent)
                {
                    _rect.SetSiblingIndex(bg.rectTransform.GetSiblingIndex() + 1);
                    return;
                }
            }
            catch { }
            _rect.SetAsLastSibling();   // never first: that is behind the map itself
        }

        /// <summary>
        /// A live marker to clone - specifically one carrying a <c>UIMapItem</c>, which is what makes it a marker
        /// rather than just a child.
        ///
        /// "First child of the container" is NOT good enough, and picking it produced a ring nearly four times too
        /// large. The first child is the map's own Background, which is stretched to fill the container
        /// (anchors 0,0 - 1,1), and under stretch anchors sizeDelta is an inset from the PARENT's size rather than a
        /// size: 751 became 2048 + 751. A real marker is point-anchored, which is the whole reason to copy one.
        /// </summary>
        private static RectTransform ProbeMarker()
        {
            try
            {
                var map = PlayerSingleton<MapApp>.Instance;
                if (map == null || map.PoIContainer == null) return null;
                for (int i = 0; i < map.PoIContainer.childCount; i++)
                {
                    var ch = map.PoIContainer.GetChild(i);
                    if (ch == null || ch.name.StartsWith("ph_area_")) continue;   // never clone our own overlays
                    if (ch.GetComponent<Il2CppScheduleOne.UIMapItem>() == null) continue;   // background, labels, decoration
                    var rt = ch.GetComponent<RectTransform>();
                    if (rt != null) return rt;
                }
                // Nothing built yet - the player's own marker is the one that exists earliest.
                var lp = Player.Local;
                return lp != null && lp.PoI != null ? lp.PoI.UI : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// The boundary band alone - a hollow circle, generated once. Vanilla draws no circular overlay on the map at
        /// all, so there is nothing to borrow.
        ///
        /// Nothing fills the inside: the play area is where a player is ALLOWED to be, and tinting it says the
        /// opposite. What carries the colour is <see cref="PaintOutside"/>, over everything the wall shuts off. This
        /// band's only job is the exact line, drawn on top of that wash.
        /// </summary>
        private Sprite RingSprite()
        {
            if (_sprite != null) return _sprite;
            try
            {
                var tex = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false);
                float c = (TextureSize - 1) * 0.5f;
                float outer = c, inner = c * (1f - RingThickness * 2f);

                // Filled in one pass and uploaded with a SINGLE interop call. Per-pixel SetPixel would be 65k
                // calls across the IL2CPP boundary and visibly hitches the game the first time a player opens
                // the map - the one moment this must not stutter.
                var px = new Color32[TextureSize * TextureSize];
                for (int y = 0; y < TextureSize; y++)
                {
                    int row = y * TextureSize;
                    for (int x = 0; x < TextureSize; x++)
                    {
                        float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                        // one pixel of falloff on each edge, so the ring does not look like a staircase when zoomed
                        float band = Mathf.Clamp01(outer - d) * Mathf.Clamp01(d - inner);
                        byte alpha = (byte)(Mathf.Clamp01(band) * 255f);
                        px[row + x] = new Color32(255, 255, 255, alpha);
                    }
                }
                tex.SetPixels32(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32>(px));
                tex.Apply(false, false);
                _sprite = Sprite.Create(tex, new Rect(0f, 0f, TextureSize, TextureSize), new Vector2(0.5f, 0.5f));
            }
            catch (System.Exception e) { Core.LogDebug("ring sprite failed: " + e.Message); }
            return _sprite;
        }

        private void Hide()
        {
            if (_go != null && _go.activeSelf) _go.SetActive(false);
            if (_outGo != null && _outGo.activeSelf) _outGo.SetActive(false);
        }

        internal void Destroy()
        {
            // Both sprites belong to this session and to the scene it ran in, so both go with it. Leaving either
            // behind is what made the boundary invisible in the NEXT lobby.
            try
            {
                if (_outSprite != null)
                {
                    if (_outSprite.texture != null) UnityEngine.Object.Destroy(_outSprite.texture);
                    UnityEngine.Object.Destroy(_outSprite);
                }
            }
            catch (Exception e) { Core.LogDebug("map wash sprite teardown failed: " + e.Message); }
            _outSprite = null;
            try
            {
                if (_sprite != null)
                {
                    if (_sprite.texture != null) UnityEngine.Object.Destroy(_sprite.texture);
                    UnityEngine.Object.Destroy(_sprite);
                }
            }
            catch (Exception e) { Core.LogDebug("map ring sprite teardown failed: " + e.Message); }
            _sprite = null;
            try { if (_outGo != null) UnityEngine.Object.Destroy(_outGo); } catch { }
            try { if (_go != null) UnityEngine.Object.Destroy(_go); } catch { }
            _go = null; _rect = null;
            _outGo = null; _outRect = null; _outImg = null;
            _cx = _cz = _radius = 0f;
        }
    }
}
