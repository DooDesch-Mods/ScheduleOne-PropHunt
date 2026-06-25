// IL2CPP backend (net6.0) global usings.
//
// Strategy (same as the sibling RVRepairVan mod): import the Il2Cpp* game namespaces globally so the
// rest of the source uses UNQUALIFIED game type names that resolve identically under the Mono backend
// (which imports the plain ScheduleOne.* namespaces in Compat/GlobalUsings.Mono.cs). UnityEngine is NOT
// prefixed in il2cpp interop, so it is backend-agnostic.
//
// NOTE: because UnityEngine is imported here and System is imported implicitly, the bare identifier
// `Object` is ambiguous - always write `UnityEngine.Object`.

global using UnityEngine;
global using Il2CppScheduleOne.DevUtilities;     // PlayerSingleton, Singleton, NetworkSingleton
global using Il2CppScheduleOne.PlayerScripts;    // Player, PlayerCamera, PlayerMovement
global using Il2CppScheduleOne.UI;               // HUD, BlackOverlay
