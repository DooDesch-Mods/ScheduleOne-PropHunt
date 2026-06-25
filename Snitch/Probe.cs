#if SNITCH
using Snitch.Api;
using PropHunt.Net;               // PropHuntNet

namespace PropHunt.Profiling
{
    /// <summary>
    /// DEBUG-only Snitch instrumentation for PropHunt. Phase 0 only has the Steam net pump, so the profiler
    /// value is the lobby member count (and the NetTick section in Core). The role/prop/disguise state
    /// distribution lands with the Phase 1 gamemode. No-op when the Snitch host is absent. Compiled only when
    /// SNITCH is defined (Debug + EnableSnitch); excluded from Release. See Workspace/build/Snitch.props.
    /// </summary>
    internal static class SnitchProbe
    {
        public static void Register()
        {
            Profiler.RegisterCounter("PropHunt.Members", () => PropHuntNet.MemberCount(), "players");
        }
    }
}
#endif
