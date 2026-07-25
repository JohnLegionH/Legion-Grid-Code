using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Mono.Addins;

[assembly: AssemblyTitle("OpenSim.Region.PhysicsModule.LegionJolt")]
[assembly: AssemblyDescription("Legion Grid Jolt physics region module")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("Legion Grid")]
[assembly: AssemblyProduct("OpenSim")]
[assembly: AssemblyCopyright("Legion Grid developers")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]

[assembly: AssemblyVersion(OpenSim.VersionInfo.AssemblyVersionNumber)]

// Mono.Addins discovery: this assembly IS an addin, dependent on the region framework. The
// [Extension] attribute on LegionJoltScene registers it as a RegionModule; it self-selects on
// [Startup] physics = Jolt.
[assembly: Addin("OpenSim.Region.PhysicsModule.LegionJolt", OpenSim.VersionInfo.AssemblyVersionNumber)]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.AssemblyVersionNumber)]
