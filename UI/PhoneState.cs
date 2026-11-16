using System;

namespace PropHunt.UI
{
    /// <summary>
    /// Whether the vanilla phone is up. The PropHunt HUD is a screen-space overlay above the phone, so every world
    /// prompt and popup that sits where the phone draws has to step aside while it is open.
    /// </summary>
    internal static class PhoneState
    {
        private static bool _warned;

        internal static bool IsOpen
        {
            get
            {
                try
                {
                    var phone = PlayerSingleton<Il2CppScheduleOne.UI.Phone.Phone>.Instance;
                    return phone != null && phone.IsOpen;
                }
                catch (Exception e)
                {
                    if (!_warned)
                    {
                        _warned = true;
                        Core.Log.Warning("could not read whether the phone is open (" + e.Message + "); the HUD treats it as closed.");
                    }
                    return false;
                }
            }
        }
    }
}
