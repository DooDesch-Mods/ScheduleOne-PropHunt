using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSteamworks;

namespace PropHunt.Phone
{
    /// <summary>
    /// A lobby member's Steam avatar as PNG bytes, for the roster.
    ///
    /// Two things make this less obvious than the Steamworks docs suggest:
    ///
    ///  - The destination buffer MUST be an <c>Il2CppStructArray</c>. A managed <c>byte[]</c> is copied at the
    ///    interop boundary, so Steam fills a throwaway and the array you hold stays zeroed - the same trap
    ///    WhatsDab hit on GetLobbyChatEntry (Lobby/ChatTransport.cs:69).
    ///  - Steam hands back RGBA with the TOP row first; Unity's raw texture data starts at the bottom. Skipping the
    ///    flip gives an upside-down face, which reads as "the avatar code works" right up until someone looks.
    ///
    /// A handle of 0 means Steam has not cached that user's picture yet and is fetching it, which is normal for a
    /// non-friend who just joined. That is a "not yet", so this returns null and <see cref="PhoneImages"/> asks
    /// again a few frames later; the roster shows initials until it lands, and keeps showing them if it never does.
    /// </summary>
    internal static class SteamAvatar
    {
        /// <summary>Refuse anything absurd rather than allocating it - a Steam avatar is 32, 64 or 184 square.</summary>
        private const uint MaxSide = 512;

        /// <summary>Players whose Steam picture came back empty. Asked once; after that they keep the initial.</summary>
        private static readonly HashSet<ulong> Blank = new HashSet<ulong>();

        internal static byte[] Png(ulong steamId)
        {
            if (Blank.Contains(steamId)) return null;
            try
            {
                var id = new CSteamID(steamId);

                // Medium (64px) rather than large: the roster draws it at 32 css px, and the large one is a
                // four-times-bigger readback for a picture nobody sees at that size.
                int handle = SteamFriends.GetMediumFriendAvatar(id);
                if (handle <= 0)
                {
                    // Not cached yet. Asking Steam for the persona is what makes it fetch one for a non-friend.
                    try { SteamFriends.RequestUserInformation(id, false); } catch { }
                    return null;
                }

                if (!SteamUtils.GetImageSize(handle, out uint w, out uint h)) return null;
                if (w == 0 || h == 0 || w > MaxSide || h > MaxSide) return null;

                int bytes = (int)(w * h * 4);
                var buf = new Il2CppStructArray<byte>(bytes);
                if (!SteamUtils.GetImageRGBA(handle, buf, bytes)) return null;

                byte[] flipped = FlipRows(buf, (int)w, (int)h);
                if (flipped == null) return null;

                string blank = BlankReason(flipped);
                if (blank != null)
                {
                    if (Blank.Add(steamId))
                        Core.Log.Msg($"steam avatar for {steamId} is blank ({w}x{h}, {blank}); the roster shows the initial.");
                    return null;
                }

                var tex = new Texture2D((int)w, (int)h, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };

                tex.LoadRawTextureData(flipped);
                tex.Apply(false, false);

                byte[] png = ImageConversion.EncodeToPNG(tex);
                UnityEngine.Object.Destroy(tex);

                return png != null && png.Length > 0 ? png : null;
            }
            catch (Exception e)
            {
                Core.LogDebug("steam avatar failed for " + steamId + ": " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// Why a picture carries no face, or null when it does. A player without a Steam picture (and every seat of a
        /// Steam emulator without one configured) still gets a valid handle, filled with one flat colour or nothing
        /// opaque at all; drawn on the roster that is an empty dark square, where the initial says who it is.
        /// </summary>
        private static string BlankReason(byte[] rgba)
        {
            int opaque = 0;
            int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
            for (int i = 0; i + 3 < rgba.Length; i += 4)
            {
                if (rgba[i + 3] < 16) continue;
                opaque++;
                int r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
                if (r < minR) minR = r; if (r > maxR) maxR = r;
                if (g < minG) minG = g; if (g > maxG) maxG = g;
                if (b < minB) minB = b; if (b > maxB) maxB = b;
            }

            if (opaque == 0) return "fully transparent";
            if (maxR - minR <= 8 && maxG - minG <= 8 && maxB - minB <= 8)
                return $"one flat colour #{minR:x2}{minG:x2}{minB:x2}";
            Core.LogDebug($"steam avatar: {opaque} opaque px, colour range #{minR:x2}{minG:x2}{minB:x2}-#{maxR:x2}{maxG:x2}{maxB:x2}");
            return null;
        }

        /// <summary>
        /// Copy the interop buffer into a managed array, last row first. The element-wise read is the price of the
        /// interop boundary and it is paid once per player per session, so it does not need to be clever.
        /// </summary>
        private static byte[] FlipRows(Il2CppStructArray<byte> src, int width, int height)
        {
            int stride = width * 4;
            int total = stride * height;
            if (src == null || src.Length < total) return null;

            var dst = new byte[total];
            for (int row = 0; row < height; row++)
            {
                int from = (height - 1 - row) * stride;
                int to = row * stride;
                for (int i = 0; i < stride; i++) dst[to + i] = src[from + i];
            }

            return dst;
        }
    }
}
