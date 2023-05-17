using System.Reflection;
using Spectre.Console;

namespace RuntimeNuGetLoader;

public class AssemblyTree
{
    /// <summary>
    /// <a href="https://learn.microsoft.com/en-us/nuget/reference/nuspec#id">Microsoft docs for Id in nuspec.</a>
    /// </summary>
    public string? PackageId;

    /// <summary>
    /// <a href="https://learn.microsoft.com/en-us/nuget/reference/nuspec#version">Microsoft docs for Version in nuspec.</a>
    /// </summary>
    public string? PackageVersion;

    /// <summary>
    /// Weather or not the <see cref="Assembly"/> was added by the <see cref="NuGetLoadingManager"/>.
    /// </summary>
    public bool? IsManagedByNuGetManager;

    /// <summary>
    /// A <see cref="List{T}"/> of <see cref="Assembly"/> from this packages.
    /// </summary>
    public List<Assembly> OwnAssemblies = new();

    /// <summary>
    /// A <see cref="List{T}"/> of <see cref="AssemblyTree"/> from the direct dependencies of this package.
    /// </summary>
    public List<AssemblyTree> DependencyAssemblies = new();

    /// <summary>
    /// Generates a <see cref="Tree"/> that represents the dependencies of this package.
    /// </summary>
    public Tree GenerateTree()
    {

        var tree = new Tree(FormatPackageName(this, true))
            .Style(Style.Parse("yellow"))
            .Guide(TreeGuide.Line);

        foreach (var assembly in DependencyAssemblies)
        {
            var branch = tree.AddNode(FormatPackageName(assembly, true));
            assembly.GenerateTreeNode(branch);
        }

        return tree;
    }

    private TreeNode GenerateTreeNode(TreeNode treeNode)
    {
        TreeNode branch = null;
        foreach (var assembly in DependencyAssemblies)
        {
            branch = treeNode.AddNode(FormatPackageName(assembly));
            assembly.GenerateTreeNode(branch);
        }
        return branch;
    }

    private string FormatPackageName(AssemblyTree assemblyTree, bool canBeRootNode = false) =>
        assemblyTree.PackageId == null ? (canBeRootNode ? "Root" : "unknown") :
            $"{assemblyTree.PackageId}." + (assemblyTree.PackageVersion ?? "unknown");
}
