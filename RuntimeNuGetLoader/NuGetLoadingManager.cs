using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using NuGet.Frameworks;
using NuGet.Versioning;

namespace RuntimeNuGetLoader
{
    public class NuGetLoadingManager
    {
        /// <summary>
        /// An <see cref="AssemblyTree"/> which has all directly requested packages in its <see cref="AssemblyTree.DependencyAssemblies"/>
        /// </summary>
        public AssemblyTree AssemblyTree { get; set; } = new AssemblyTree();

        internal List<ManagedNuGetPackage> AvailableNuGets { get; set; } = new List<ManagedNuGetPackage>();
        
        public static NuGetLoadingManager Instance => _nuGetLoadingManager ??= new NuGetLoadingManager();
        
        private static NuGetLoadingManager? _nuGetLoadingManager;

        private NuGetLoadingManager()
        {
            AppDomain.CurrentDomain.AssemblyResolve += NuGetLoadingManagerAssemblyResolver;
            _nuGetLoadingManager = this;
        } 
        
        private Assembly NuGetLoadingManagerAssemblyResolver(object sender, ResolveEventArgs args) => AssemblyTree.GetOwnAndDependentAssemblies().FirstOrDefault(asm => asm.FullName == args.Name);
        
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
        /// <param name="nuGetVersion">Specifies the version of the nuget to load. When loading locally this is optional but if you want to download the nuget this is required</param>
        /// <param name="savePath">The place where the nuget package should be downloaded to. (only applies if the nuget was not found locally)</param>
        /// <returns>instance of <see cref="ManagedNuGetPackage"/> or null if package was not found.</returns>
        public ManagedNuGetPackage? GetPackageById(string id, NuGetVersion? nuGetVersion = null, string? savePath = null)
        {
            // first check if nuget already exists
            var existingNuget = AvailableNuGets.FirstOrDefault(npkgi => npkgi.Id == id && (nuGetVersion == null || npkgi.Version == nuGetVersion));
            if (existingNuget != null) return existingNuget;
            
            // if nuget was not found locally and downloading parameters where supplied nuget is downloaded
            if (nuGetVersion == null || savePath == null) return null;
            AddNugetFromFile(DownloadNugetPackage(id, nuGetVersion, savePath));   
            return AvailableNuGets.FirstOrDefault(npkgi => npkgi.Id == id && npkgi.Version == nuGetVersion);
        }

        /// <summary>
        /// Does the same thing as <see cref="RequestPackage"/> but for a <see cref="List{T}"/> of <see cref="ManagedNuGetPackage"/>.
        /// </summary>
        /// <param name="requestedPackages">an <see cref="IEnumerable{T}"/> of <see cref="ManagedNuGetPackage"/> that should be loaded.</param>
        /// <param name="dependenciesFolderPath">The folder that is used to store dependencies.</param>
        /// <param name="downloadMissing">Weather or not to download missing dependencies from <a href="https://www.nuget.org">nuget.org</a></param>
        /// <returns>Instance of <see cref="AssemblyTree"/> containing an <see cref="AssemblyTree"/> in its <see cref="AssemblyTree.DependencyAssemblies"/> for each requested package.</returns>
        /// <exception cref="Exception">A multitude of Exceptions that can arise during resolution and loading of NuGet packages. (currently no custom exceptions have been created)</exception>
        public AssemblyTree RequestPackages(IEnumerable<ManagedNuGetPackage> requestedPackages, bool downloadMissing, string?  dependenciesFolderPath = null)
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
        public AssemblyTree RequestPackage(ManagedNuGetPackage requestedPackage, bool downloadMissing, string? dependenciesFolderPath = null)
        {
            var targetFramework = GetRunningFramework();
            if (requestedPackage.AssemblyTree != null) throw new Exception($"{requestedPackage.Id} has already been loaded.");
            var asmTree = requestedPackage.GetAssemblies(targetFramework, downloadMissing, dependenciesFolderPath);
            AssemblyTree.DependencyAssemblies.Add(asmTree);
            return asmTree;
        }
        
        /// <summary>
        /// Downloads a nuget package from <a href="https://www.nuget.org">nuget.org</a>.
        /// </summary>
        /// <param name="packageID">The <a href="https://learn.microsoft.com/en-us/nuget/reference/nuspec#id">Id</a> of the package to download.</param>
        /// <param name="packageVersion">The <a href="https://learn.microsoft.com/en-us/nuget/reference/nuspec#version">version</a> of the package to download.</param>
        /// <param name="savePath">The path where the nuget should be saved at.</param>
        /// <returns>The path that the nuget has been saved to.</returns>
        /// <exception cref="Exception">The download has failed for whatever reason.</exception>
        internal string DownloadNugetPackage(string packageID, NuGetVersion packageVersion, string savePath)
        {
            // official NuGet repository url
            var url = $"https://www.nuget.org/api/v2/package/{packageID}/{packageVersion}";
            try
            {
                HttpResponseMessage response = new HttpClient().GetAsync(url).Result;
                response.EnsureSuccessStatusCode();
                byte[] responseBody = response.Content.ReadAsByteArrayAsync().Result;

                // Write the package to disk
                string fileName = $"{packageID}.{packageVersion}.nupkg";
                var filePath = Path.Combine(savePath, fileName);
                File.WriteAllBytes(filePath, responseBody);
                return filePath;
            }
            catch (Exception ex)
            {
                throw new Exception($"Could not download package {packageID}.{packageVersion}", ex);
            }
        }

        /// <summary>
        /// Translates the <see cref="AppDomainSetup.TargetFrameworkName"/> of the current <see cref="AppDomain"/> to a <see cref="NuGetFramework"/>
        /// </summary>
        /// <returns>instance of <see cref="NuGetFramework"/></returns>
        internal static NuGetFramework GetRunningFramework()
        {
#if  NET
            var targetFramework = NuGetFramework.Parse(AppDomain.CurrentDomain.SetupInformation.TargetFrameworkName ?? "netstandard2.0");
#elif NETSTANDARD
            var targetFramework = NuGetFramework.Parse(AppContext.TargetFrameworkName ?? "netstandard2.0");
#endif
#if WINDOWS
            // add windows platform to differentiate between for example net8.0 and net8.0-windows8.0
            targetFramework = new NuGetFramework(targetFramework.Framework, targetFramework.Version, "Windows", targetFramework.Version);
#elif LINUX
            // add linux platform to differentiate between for example net8.0 and net8.0-linux8.0
            targetFramework = new NuGetFramework(targetFramework.Framework, targetFramework.Version, "Linux", targetFramework.Version);
#elif UNIX // this should handle MacOS and BSD
            // add unix platform to differentiate between for example net8.0 and net8.0-unix8.0
            targetFramework = new NuGetFramework(targetFramework.Framework, targetFramework.Version, "Linux", targetFramework.Version);
#endif
            return targetFramework;
        }
    }
}
