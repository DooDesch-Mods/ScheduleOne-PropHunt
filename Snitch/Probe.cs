#if SNITCH
using Snitch.Api;
using PropHunt.Net;               // PropHuntNet

namespace PropHunt.Profiling
{
    /// <summary>
    /// DEBUG-only Snitch instrumentation for PropHunt: exposes the lobby member count as a profiler counter
    /// (alongside the NetTick section in Core). No-op when the Snitch host is absent. Compiled only when
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
