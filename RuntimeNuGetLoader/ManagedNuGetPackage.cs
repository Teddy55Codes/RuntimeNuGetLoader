using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Versioning;

namespace RuntimeNuGetLoader
{
    public class ManagedNuGetPackage
    {
        /// <summary>
        /// <a href="https://learn.microsoft.com/en-us/nuget/reference/nuspec#id">Microsoft docs for Id in nuspec.</a>
        /// </summary>
        public string Id { get; private set; }

        /// <summary>
        /// <a href="https://learn.microsoft.com/en-us/nuget/reference/nuspec#version">Microsoft docs for Version in nuspec.</a>
        /// </summary>
        public NuGetVersion Version { get; private set; }

        /// <summary>
        /// An Instance of <see cref="AssemblyTree"/> that contains all the dependencies for this package.
        /// </summary>
        public AssemblyTree AssemblyTree { get; private set; }

        // true if NuGet package doesn't contain any folder under lib/ that has a dll file in it.
        private readonly bool _isLinkingNuGet;
        private readonly NuGetLoadingManager _managerParent;
        private readonly List<FrameworkSpecificGroup> _frameworkItems;
        private readonly string _nuGetPackagePath;
        private readonly List<PackageDependencyGroup> _dependencies;
        private string _dependencyPackagePath;


        // These are common namespaces that are used for NuGet packages that are used by the dotnet compiler (Roslyn). They are not needed at runtime.
        // All other NuGet packages that are also only used in the build process need to be analysed by the program.
        private static readonly string[] BuildNuGetNameSpaces = {
            "microsoft.netcore",
            "microsoft.build",
            "microsoft.codeanalysis.csharp.workspaces",
            "microsoft.codeanalysis.visualbasic.workspaces",
            "microsoft.net.sdk",
            "microsoft.net.test.sdk",
            "xunit.runner.visualstudio",
            "microsoft.testplatform.testhost",
            "microsoft.codeanalysis.fxcopanalyzers",
            "stylecop.analyzers",
            "sonaranalyzer.csharp"
        };

        /// <summary>
        /// Loads basic information about a NuGet package from a local file without loading any <see cref="Assembly">assemblies</see>.
        /// </summary>
        /// <param name="nuGetPackagePath">The path to the NuGet package file.</param>
        /// <param name="manager">instance of its <see cref="NuGetLoadingManager"/></param>
        /// <exception cref="Exception">Thrown if the .nuspec file has an invalid format.</exception>
        /// <exception cref="ArgumentException">Thrown if the package version in the .nuspec is invalid (to a valid <see cref="NuGetVersion"/>)</exception>
        public ManagedNuGetPackage(string nuGetPackagePath, NuGetLoadingManager manager)
        {
            _nuGetPackagePath = nuGetPackagePath;
            _managerParent = manager;
#if LANG_V11
            using ZipArchive archive = ZipFile.OpenRead(_nuGetPackagePath);
#else
            ZipArchive archive = ZipFile.OpenRead(_nuGetPackagePath);
#endif
            
            var entry = archive.Entries.First(ent => ent.FullName.EndsWith(".nuspec"));

            // Load nuspec file from stream and read id and version
            var nuspecDocument = XDocument.Load(entry.Open());
            var attribute = nuspecDocument.Root?.Attribute("xmlns");
            if (attribute == null) throw new Exception($"invalid .nuspec file found in {_nuGetPackagePath}");
            XNamespace xNamespace = attribute.Value;
            Id = nuspecDocument.Descendants(xNamespace + "id").FirstOrDefault()?.Value;
            try
            {
                var x = nuspecDocument.Descendants(xNamespace + "version").FirstOrDefault()?.Value;
                Version = NuGetVersion.Parse(x);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException($"Found invalid version format in package {Id} ({_nuGetPackagePath})", ex);
            }
#if LANG_V11
            using var packageArchiveReader = new PackageArchiveReader(_nuGetPackagePath);
#else
            var packageArchiveReader = new PackageArchiveReader(_nuGetPackagePath);
#endif
            _dependencies = packageArchiveReader.GetPackageDependencies().ToList();
            _frameworkItems = packageArchiveReader.GetLibItems().ToList();
            // check if NuGet package is only used to link to other dependencies
            _isLinkingNuGet = !_frameworkItems.Any(group => group.Items.Any(item => item.EndsWith(".dll")));
        }

        /// <summary>
        /// Loads the <see cref="Assembly">assemblies</see>  of the requested NuGet package and its dependencies.
        /// Also if the "downloadMissing" parameter is set to true it automatically downloads NuGet packages from <a href="https://www.nuget.org">nuget.org</a> and saves them to the set "dependencyFolderPath".
        /// </summary>
        /// <param name="dependencyFolderPath">if "downloadMissing" is set to true this is the path where the dependencies will be stored (optimally this would be in the area where packages are loaded by the <see cref="NuGetLoadingManager"/>)</param>
        /// <param name="currentTF">The <see cref="NuGetFramework"/> of the parent item. This is ether the representation of the <see cref="AppDomainSetup.TargetFrameworkName"/> of the current <see cref="AppDomain"/> if it is the directly requested package otherwise it is the selected <see cref="NuGetFramework"/> of the NuGet package that this one is a dependency of.</param>
        /// <param name="downloadMissing">Weather or not to download missing dependencies from <a href="https://www.nuget.org">nuget.org</a>.</param>
        /// <returns>The <see cref="AssemblyTree"/> for the loaded NuGet package.</returns>
        /// <exception cref="Exception">A multitude of Exceptions that can arise during resolution and loading of NuGet packages. (currently no custom exceptions have been created)</exception>
#if LANG_V11  
        public AssemblyTree GetAssemblies(NuGetFramework currentTF, bool downloadMissing, string? dependencyFolderPath = null)
#else
        public AssemblyTree GetAssemblies(NuGetFramework currentTF, bool downloadMissing, string dependencyFolderPath = null)
#endif
        {
            if (downloadMissing && dependencyFolderPath == null) throw new ArgumentNullException(dependencyFolderPath, $"A path for storing dependencies must be set. If {nameof(downloadMissing)} is set to true {nameof(dependencyFolderPath)} can't be null.");
            _dependencyPackagePath = dependencyFolderPath;
            var assemblyTree = new AssemblyTree { PackageId = Id, PackageVersion = Version.ToString(), IsManagedByNuGetManager = true };

            var mostCompatible = GetMostCompatibleFramework(currentTF);
            var dependenciesForMostCompatible = _dependencies.Single(d => d.TargetFramework == mostCompatible.Item1).Packages.ToList();

            // resolve dependencies
            for (int i = 0;i < dependenciesForMostCompatible.Count;i++)
            {
                var dependency = dependenciesForMostCompatible[i];
                // skip NuGets that are used by the compiler only (only from known namespaces the rest will need to be handled later)
                if (BuildNuGetNameSpaces.Any(ns => dependency.Id.ToLower().StartsWith(ns))) continue;

                // find dependency with same id
                var dependencyCandidates = _managerParent.AvailableNuGets.Where(nupkgi => nupkgi.Id == dependency.Id);

                // if assembly that was not found under the managed NuGets
                if (!dependencyCandidates.Any())
                {
                    // check if it is already loaded
#if LANG_V11
                    if (IsAssemblyLoaded(dependency.Id, dependency.VersionRange, out Assembly? alreadyLoadedAssembly))
#else
                    if (IsAssemblyLoaded(dependency.Id, dependency.VersionRange, out Assembly alreadyLoadedAssembly))
#endif
                    {
                        assemblyTree.DependencyAssemblies.Add(
                            new AssemblyTree
                            {
                                PackageId = alreadyLoadedAssembly.GetName().Name, 
                                PackageVersion = alreadyLoadedAssembly.GetName().Version.ToString(), 
                                IsManagedByNuGetManager = false, 
                                OwnAssemblies = new List<Assembly> { alreadyLoadedAssembly }
                            });
                        continue;
                    }
                    if (!downloadMissing)
                    {
                        throw new Exception($"Missing dependency {dependency.Id} version {dependency.VersionRange} for NuGetPackage {Id} in Path {_nuGetPackagePath}");
                    }
                }

                NuGetVersion bestMatchVersion = dependency.VersionRange.FindBestMatch(dependencyCandidates.Select(candidate => candidate.Version));
                if (bestMatchVersion == null)
                {
                    // if downloading is enabled try get the NuGet package from nuget.org
                    if (downloadMissing)
                    {
                        var nugetFilePath = _managerParent.DownloadNugetPackage(
                            dependency.Id, 
                            dependency.VersionRange.HasUpperBound ? dependency.VersionRange.MaxVersion : dependency.VersionRange.MinVersion,
                            _dependencyPackagePath);
                        
                        _managerParent.AddNugetFromFile(nugetFilePath);
                        // now that this package is available we retry the loading process.
                        i--;
                        continue;
                    }

                    throw new Exception($"Only found incompatible NuGet versions for {Id} {Version}. versions found for dependency {dependency.Id} ({string.Join(",", dependencyCandidates.Select(d => d.Version))}) valid range for that dependency is " +
                                        $"{_dependencies.Where(dep => dep.TargetFramework == mostCompatible.Item1).Select(dep => dep.Packages.FirstOrDefault(p => p.Id == dependency.Id)?.VersionRange).FirstOrDefault()}");
                }

                var bestMatchDependency = dependencyCandidates.First(candidate => candidate.Version == bestMatchVersion);

                // check if nuget is required at runtime
                if (!bestMatchDependency._dependencies.Any() && bestMatchDependency._isLinkingNuGet) continue;

                // if assembly is already loaded just add its tree without re-getting the assemblies
#if LANG_V11
                if (IsAssemblyLoaded(dependency.Id, new VersionRange(bestMatchVersion), out Assembly? dependencyAssembly))
#else
                if (IsAssemblyLoaded(dependency.Id, new VersionRange(bestMatchVersion), out Assembly dependencyAssembly))
#endif
                {
                    assemblyTree.DependencyAssemblies.Add(bestMatchDependency.AssemblyTree ?? new AssemblyTree { PackageId = dependency.Id, PackageVersion = bestMatchVersion.Version.ToString(), IsManagedByNuGetManager = false, OwnAssemblies = new List<Assembly> { dependencyAssembly } });
                    continue;
                }

                // load the selected assembly
                assemblyTree.DependencyAssemblies.Add(bestMatchDependency.GetAssemblies(mostCompatible.Item1, downloadMissing, _dependencyPackagePath));
            }

            if (mostCompatible.Item2 != null)
            {
                var mostCompatibleGroup = mostCompatible.Item2;
#if LANG_V11
                using var packageArchiveReader = new PackageArchiveReader(_nuGetPackagePath);
#else
                var packageArchiveReader = new PackageArchiveReader(_nuGetPackagePath);
#endif
                foreach (var item in mostCompatibleGroup.Items.Where(item => item.EndsWith(".dll")))
                {
                    var entry = packageArchiveReader.GetEntry(item);
#if LANG_V11
                    using var target = new MemoryStream();
                    using var source = entry.Open();
#else
                    var target = new MemoryStream();
                    var source = entry.Open();
#endif
                    source.CopyTo(target);
                    assemblyTree.OwnAssemblies.Add(Assembly.Load(target.ToArray()));
                }
            }

            AssemblyTree = assemblyTree;
            return assemblyTree;
        }


        /// <summary>
        /// Finds the most compatible <see cref="NuGetFramework"/> for this package considering the "target".
        /// </summary>
        /// <param name="target">The <see cref="NuGetFramework"/> to consider.</param>
        /// <returns>The most compatible <see cref="NuGetFramework"/> value 1 is from .nuspec and value 2 is from lib folder.</returns>
        /// <exception cref="Exception">Throws if no compatible <see cref="NuGetFramework"/> was found.</exception>
#if LANG_V11
        private (NuGetFramework, FrameworkSpecificGroup?) GetMostCompatibleFramework(NuGetFramework target)
#else
        private (NuGetFramework, FrameworkSpecificGroup) GetMostCompatibleFramework(NuGetFramework target)
#endif
        {
            var compatibleFrameworks = GetCompatibleFrameworksDec(target);
            if (!compatibleFrameworks.Any()) throw new Exception($"No compatible framework found for {Id} with target of {target.DotNetFrameworkName}. (IsLinkingNuGet={_isLinkingNuGet})");

            return compatibleFrameworks.First();
        }
        
#if LANG_V11
        private List<(NuGetFramework, FrameworkSpecificGroup?)> GetCompatibleFrameworksDec(NuGetFramework target)
#else
        private List<(NuGetFramework, FrameworkSpecificGroup)> GetCompatibleFrameworksDec(NuGetFramework target)
#endif
        {
            var compatibilityProvider = new DefaultCompatibilityProvider();
            // get compatible dependencies (from .nuspec)
            var compatibleDependencies = _dependencies.Select(dep => dep.TargetFramework)
                .Where(targetFramework => compatibilityProvider.IsCompatible(target, targetFramework)).ToList();
#if LANG_V11
            var sortedDependencies = new List<(NuGetFramework, FrameworkSpecificGroup?)>();
#else
            var sortedDependencies = new List<(NuGetFramework, FrameworkSpecificGroup)>();
#endif
            var reducer = new FrameworkReducer();
            while (compatibleDependencies.Count > 0)
            {
                var mostCompatible = reducer.GetNearest(target, compatibleDependencies);
                if (mostCompatible == null) continue;
                // add the most compatible dependency from .nuspec and also try to add corresponding dependency from lib folder also check if folder has at least 1 dll file. There are lib folders that only contain _._ to explicitly express incompatibility (https://learn.microsoft.com/en-us/nuget/reference/errors-and-warnings/nu5127)
                sortedDependencies.Add((mostCompatible, _frameworkItems.FirstOrDefault(fw => fw.TargetFramework == mostCompatible && fw.Items.Any(item => item.EndsWith(".dll")))));
                compatibleDependencies.Remove(mostCompatible);
            }

            return sortedDependencies;
        }

#if LANG_V11
        private static bool IsAssemblyLoaded(string assemblyName, VersionRange versionRange, [NotNullWhen(true)] out Assembly? bestMatch)
#else
        private static bool IsAssemblyLoaded(string assemblyName, VersionRange versionRange, out Assembly bestMatch)
#endif
        {
            var possibleCandidates = AppDomain.CurrentDomain.GetAssemblies().Where(asm => asm.GetName().Name?.Equals(assemblyName, StringComparison.OrdinalIgnoreCase) ?? false);
            bestMatch = possibleCandidates.FirstOrDefault(candidate => NuGetVersion.TryParse(candidate.GetName().Version?.ToString() ?? string.Empty, out NuGetVersion version) && versionRange.FindBestMatch(new[] { version }) != null);
            return bestMatch != null;
        }
    }
}
