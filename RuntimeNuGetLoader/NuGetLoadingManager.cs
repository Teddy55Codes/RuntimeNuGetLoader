using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using NuGet.Frameworks;

namespace RuntimeNuGetLoader
{
    public class NuGetLoadingManager
    {
        /// <summary>
        /// An <see cref="AssemblyTree"/> which has all directly requested packages in its <see cref="AssemblyTree.DependencyAssemblies"/>
        /// </summary>
        public AssemblyTree AssemblyTree = new AssemblyTree();

#if NET7_0
        public static NuGetLoadingManager Instance => _nuGetLoadingManager ??= new NuGetLoadingManager();
#else
        public static NuGetLoadingManager Instance => _nuGetLoadingManager ?? (_nuGetLoadingManager = new NuGetLoadingManager());
#endif
        internal List<ManagedNuGetPackage> AvailableNuGets = new List<ManagedNuGetPackage>();
#if LANG_V11
        private static NuGetLoadingManager? _nuGetLoadingManager;
#else
        private static NuGetLoadingManager _nuGetLoadingManager;
#endif

        private NuGetLoadingManager() => _nuGetLoadingManager = this;

        /// <summary>
        /// Adds a new <see cref="ManagedNuGetPackage"/> from a local .nupkg file to the AvailableNuGets.
        /// </summary>
        /// <param name="NugetPath">The path to the local .nupkg file.</param>
        public void AddNugetFromFile(string NugetPath) => AvailableNuGets.Add(new ManagedNuGetPackage(NugetPath, this));

        /// <summary>
        /// Adds multiple new <see cref="ManagedNuGetPackage"/> from a path to the AvailableNuGets.
        /// </summary>
        /// <param name="nugetPath">The path where the .nupkg files are stored.</param>
        public void AddNuGetsFromPath(string nugetPath)
        {
            Directory.GetFiles(nugetPath, "*.nupkg", SearchOption.AllDirectories).ToList().ForEach(AddNugetFromFile);
        }

        /// <summary>
        /// Get a Nuget package from AvailableNuGets with a given id.
        /// </summary>
        /// <param name="id">the NuGet package Id <a href="https://learn.microsoft.com/en-us/nuget/reference/nuspec#id">Id in nuspec (microsoft docs)</a> </param>
        /// <returns>instance of <see cref="ManagedNuGetPackage"/> or null if package was not found.</returns>
#if LANG_V11
        public ManagedNuGetPackage? GetPackageById(string id) => AvailableNuGets.FirstOrDefault(npkgi => npkgi.Id == id);
#else
        public ManagedNuGetPackage GetPackageById(string id) => AvailableNuGets.FirstOrDefault(npkgi => npkgi.Id == id);
#endif

        /// <summary>
        /// Does the same thing as <see cref="RequestPackage"/> but for a <see cref="List{T}"/> of <see cref="ManagedNuGetPackage"/>.
        /// </summary>
        /// <param name="requestedPackages">an <see cref="IEnumerable{T}"/> of <see cref="ManagedNuGetPackage"/> that should be loaded.</param>
        /// <param name="dependenciesFolderPath">The folder that is used to store dependencies.</param>
        /// <param name="downloadMissing">Weather or not to download missing dependencies from <a href="https://www.nuget.org">nuget.org</a></param>
        /// <returns>Instance of <see cref="AssemblyTree"/> containing an <see cref="AssemblyTree"/> in its <see cref="AssemblyTree.DependencyAssemblies"/> for each requested package.</returns>
        /// <exception cref="Exception">A multitude of Exceptions that can arise during resolution and loading of NuGet packages. (currently no custom exceptions have been created)</exception>
#if LANG_V11
        public AssemblyTree RequestPackages(IEnumerable<ManagedNuGetPackage> requestedPackages, bool downloadMissing, string?  dependenciesFolderPath = null)
#else
        public AssemblyTree RequestPackages(IEnumerable<ManagedNuGetPackage> requestedPackages, bool downloadMissing, string  dependenciesFolderPath = null)
#endif
        {
            var targetFramework = GetRunningFramework();
            var assemblyTree = new AssemblyTree { IsManagedByNuGetManager = true };
            foreach (var requestedPackage in requestedPackages)
            {
                if (requestedPackage.AssemblyTree != null) throw new Exception($"{requestedPackage.Id} has already been loaded.");
                var asmTree = requestedPackage.GetAssemblies(targetFramework, downloadMissing, dependenciesFolderPath);
                AssemblyTree.DependencyAssemblies.Add(asmTree);
                assemblyTree.DependencyAssemblies.Add(asmTree);
            }

            return assemblyTree;
        }

        /// <summary>
        /// Loads the assemblies and its dependencies for a <see cref="ManagedNuGetPackage"/>.
        /// </summary>
        /// <param name="requestedPackage">The <see cref="ManagedNuGetPackage"/> that should be loaded</param>
        /// <param name="dependenciesFolderPath">The folder that is used to store dependencies.</param>
        /// <param name="downloadMissing">Weather or not to download missing dependencies from <a href="https://www.nuget.org">nuget.org</a></param>
        /// <returns>Instance of <see cref="AssemblyTree"/> for the requested package</returns>
        /// <exception cref="Exception">A multitude of Exceptions that can arise during resolution and loading of NuGet packages. (currently no custom exceptions have been created)</exception>
#if LANG_V11
        public AssemblyTree RequestPackage(ManagedNuGetPackage requestedPackage, bool downloadMissing, string? dependenciesFolderPath = null)
#else
        public AssemblyTree RequestPackage(ManagedNuGetPackage requestedPackage, bool downloadMissing, string dependenciesFolderPath = null)
#endif
        {
            var targetFramework = GetRunningFramework();
            if (requestedPackage.AssemblyTree != null) throw new Exception($"{requestedPackage.Id} has already been loaded.");
            var asmTree = requestedPackage.GetAssemblies(targetFramework, downloadMissing, dependenciesFolderPath);
            AssemblyTree.DependencyAssemblies.Add(asmTree);
            return asmTree;
        }

        /// <summary>
        /// Translates the <see cref="AppDomainSetup.TargetFrameworkName"/> of the current <see cref="AppDomain"/> to a <see cref="NuGetFramework"/>
        /// </summary>
        /// <returns>instance of <see cref="NuGetFramework"/></returns>
        public static NuGetFramework GetRunningFramework()
        {
            
#if  NETFRAMEWORK || NET
            var targetFramework = NuGetFramework.Parse(AppDomain.CurrentDomain.SetupInformation.TargetFrameworkName ?? "netstandard2.0");
#elif NETSTANDARD
            var targetFramework = NuGetFramework.Parse(RuntimeInformation.FrameworkDescription ?? "netstandard2.0");
#endif
#if WINDOWS
            // add windows platform to differentiate between for example net7.0 and net7.0-windows7.0
            targetFramework = new NuGetFramework(targetFramework.Framework, targetFramework.Version, "Windows", targetFramework.Version);
#endif
            return targetFramework;
        }
    }
}
