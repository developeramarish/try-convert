﻿using Facts;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Locator;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace MSBuildAbstractions
{
    /// <summary>
    /// Static helper methods for working with general MSBuildisms.
    /// </summary>
    public static class MSBuildHelpers
    {
        /// <summary>
        /// matches $(name) pattern
        /// </summary>
        private static readonly Regex DimensionNameInConditionRegex = new Regex(@"^\$\(([^\$\(\)]*)\)$");

        /// <summary>
        /// Converts configuration dimensional value vector to a msbuild condition
        /// Use the standard format of
        /// '$(DimensionName1)|$(DimensionName2)|...|$(DimensionNameN)'=='DimensionValue1|...|DimensionValueN'
        /// </summary>
        /// <param name="dimensionalValues">vector of configuration dimensional properties</param>
        /// <returns>msbuild condition representation</returns>
        internal static string DimensionalValuePairsToCondition(ImmutableDictionary<string, string> dimensionalValues)
        {
            if (null == dimensionalValues || 0 == dimensionalValues.Count)
            {
                return string.Empty; // no condition. Returns empty string to match MSBuild.
            }

            string left = string.Empty;
            string right = string.Empty;

            foreach (string key in dimensionalValues.Keys)
            {
                if (!string.IsNullOrEmpty(left))
                {
                    left += "|";
                    right += "|";
                }

                left += "$(" + key + ")";
                right += dimensionalValues[key];
            }

            string condition = "'" + left + "'=='" + right + "'";
            return condition;
        }

        /// <summary>
        /// Returns a name of a configuration like Debug|AnyCPU
        /// </summary>
        public static string GetConfigurationName(ImmutableDictionary<string, string> dimensionValues) => dimensionValues.IsEmpty ? "" : dimensionValues.Values.Aggregate((x, y) => $"{x}|{y}");

        /// <summary>
        /// Returns a name of a configuration like Debug|AnyCPU
        /// </summary>
        public static string GetConfigurationName(string condition)
        {
            if (ConditionToDimensionValues(condition, out var dimensionValues))
            {
                return GetConfigurationName(dimensionValues);
            }

            return "";
        }

        /// <summary>
        /// Tries to parse an MSBuild condition to a dimensional vector
        /// only matches standard pattern:
        /// '$(DimensionName1)|$(DimensionName2)|...|$(DimensionNameN)'=='DimensionValue1|...|DimensionValueN'
        /// </summary>
        /// <param name="condition">msbuild condition string</param>
        /// <param name="dimensionalValues">configuration dimensions vector (output)</param>
        /// <returns>true on success</returns>
        public static bool ConditionToDimensionValues(string condition, out ImmutableDictionary<string, string> dimensionalValues)
        {
            string left;
            string right;
            dimensionalValues = ImmutableDictionary<string, string>.Empty;

            if (string.IsNullOrEmpty(condition))
            {
                // yes empty condition is recognized as a empty dimension vector
                return true;
            }

            int equalPos = condition.IndexOf("==", StringComparison.OrdinalIgnoreCase);
            if (equalPos <= 0)
            {
                return false;
            }

            left = condition.Substring(0, equalPos).Trim();
            right = condition.Substring(equalPos + 2).Trim();

            // left and right needs to ba a valid quoted strings
            if (!UnquoteString(ref left) || !UnquoteString(ref right))
            {
                return false;
            }

            string[] dimensionNamesInCondition = left.Split(new char[] { '|' });
            string[] dimensionValuesInCondition = right.Split(new char[] { '|' });

            // number of keys need to match number of values
            if (dimensionNamesInCondition.Length == 0 || dimensionNamesInCondition.Length != dimensionValuesInCondition.Length)
            {
                return false;
            }

            Dictionary<string, string> parsedDimensionalValues = new Dictionary<string, string>(dimensionNamesInCondition.Length);

            for (int i = 0; i < dimensionNamesInCondition.Length; i++)
            {
                // matches "$(name)" patern.
                Match match = DimensionNameInConditionRegex.Match(dimensionNamesInCondition[i]);
                if (!match.Success)
                {
                    return false;
                }

                string dimensionName = match.Groups[1].ToString();
                if (string.IsNullOrEmpty(dimensionName))
                {
                    return false;
                }

                parsedDimensionalValues[dimensionName] = dimensionValuesInCondition[i];
            }

            dimensionalValues = parsedDimensionalValues.ToImmutableDictionary();
            return true;
        }

        /// <summary>
        /// Given a TFM string, determines if that TFM has an explicit System.ValueTuple reference.
        /// </summary>
        public static bool FrameworkHasAValueTuple(string tfm)
        {
            if (tfm is null
                || tfm.ContainsIgnoreCase(MSBuildFacts.NetstandardPrelude)
                || tfm.ContainsIgnoreCase(MSBuildFacts.NetcoreappPrelude))
            {
                return false;
            }

            if (!tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return tfm.StartsWith(MSBuildFacts.LowestFrameworkVersionWithSystemValueTuple);
        }

        /// <summary>
        /// Gets all Reference items from a given item group.
        /// </summary>
        private static IEnumerable<ProjectItemElement> GetReferences(ProjectItemGroupElement itemGroup) =>
            itemGroup.Items.Where(item => item.ElementName.Equals(MSBuildFacts.MSBuildReferenceName, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Determines if a given project is a WPF project by looking at its references.
        /// </summary>
        public static bool IsWPF(IProjectRootElement projectRoot)
        {
            var references = projectRoot.ItemGroups.SelectMany(GetReferences)?.Select(elem => elem.Include);
            return DesktopFacts.KnownWPFReferences.All(reference => references.Contains(reference, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Determines if a given project is a WinForms project by looking at its references.
        /// </summary>
        public static bool IsWinForms(IProjectRootElement projectRoot)
        {
            var references = projectRoot.ItemGroups.SelectMany(GetReferences)?.Select(elem => elem.Include);
            return DesktopFacts.KnownWinFormsReferences.All(reference => references.Contains(reference, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Checks if a given TFM is not .NET Framework.
        /// </summary>
        public static bool IsNotNetFramework(string tfm) => 
            !tfm.ContainsIgnoreCase(MSBuildFacts.NetcoreappPrelude)
            && !tfm.ContainsIgnoreCase(MSBuildFacts.NetstandardPrelude);

        /// <summary>
        /// Finds the item group where a packages.config is included. Assumes only one.
        /// </summary>
        public static ProjectItemGroupElement GetPackagesConfigItemGroup(IProjectRootElement root) =>
            root.ItemGroups.FirstOrDefault(pige => pige.Items.Any(pe => pe.Include.Equals(PackageFacts.PackagesConfigIncludeName, StringComparison.OrdinalIgnoreCase)));

        /// <summary>
        /// Finds the packages.config item in its containing item group.
        /// </summary>
        public static ProjectItemElement GetPackagesConfigItem(ProjectItemGroupElement packagesConfigItemGroup) =>
            packagesConfigItemGroup.Items.Single(pe => pe.Include.Equals(PackageFacts.PackagesConfigIncludeName, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Adds the UseWindowsForms=True property to the top-level project property group.
        /// </summary>
        public static void AddUseWinForms(ProjectPropertyGroupElement propGroup) => propGroup.AddProperty(DesktopFacts.UseWinFormsPropertyName, "true");

        /// <summary>
        /// Adds the UseWPF=true property to the top-level project property group.
        /// </summary>
        public static void AddUseWPF(ProjectPropertyGroupElement propGroup) => propGroup.AddProperty(DesktopFacts.UseWPFPropertyName, "true");

        /// <summary>
        /// Finds the property group with the TFM specified, which is normally the top-level property group.
        /// </summary>
        public static ProjectPropertyGroupElement GetOrCreateTopLevelPropertyGroupWithTFM(IProjectRootElement rootElement) =>
            rootElement.PropertyGroups.Single(pg => pg.Properties.Any(p => p.ElementName.Equals(MSBuildFacts.TargetFrameworkNodeName, StringComparison.OrdinalIgnoreCase)))
            ?? rootElement.AddPropertyGroup();
        
        /// <summary>
        /// Finds the item group where PackageReferences are specified. Usually there is only one.
        /// </summary>
        public static ProjectItemGroupElement GetOrCreatePackageReferencesItemGroup(IProjectRootElement rootElement) =>
            rootElement.ItemGroups.SingleOrDefault(ig => ig.Items.All(i => i.ElementName.Equals(PackageFacts.PackageReferencePackagesNodeName, StringComparison.OrdinalIgnoreCase)))
            ?? rootElement.AddItemGroup();

        /// <summary>
        /// Checks if a metadata item can stay or if it needs to be converted.
        /// </summary>
        /// <returns>True if the metadata item is fine. False if it needs to be removed or converted.</returns>
        public static bool IsValidMetadataForConversionPurposes(IProjectMetadata projectMetadata) =>
            !projectMetadata.Name.Equals(MSBuildFacts.RequiredTargetFrameworkNodeName, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Gets the top-level property group, and if it doesn't exist, creates it.
        /// </summary>
        public static ProjectPropertyGroupElement GetOrCreateTopLevelPropertyGroup(BaselineProject baselineProject, IProjectRootElement projectRootElement)
        {
            bool IsAfterFirstImport(ProjectPropertyGroupElement propertyGroup)
            {
                if (baselineProject.ProjectStyle == ProjectStyle.Default
                    || baselineProject.ProjectStyle == ProjectStyle.WindowsDesktop
                    || baselineProject.ProjectStyle == ProjectStyle.DefaultSubset)
                {
                    return true;
                }

                var firstImport = projectRootElement.Imports.Where(i => i.Label != MSBuildFacts.SharedProjectsImportLabel).FirstOrDefault();
                return firstImport is { } && propertyGroup.Location.Line > firstImport.Location.Line;
            }

            return projectRootElement.PropertyGroups.FirstOrDefault(pg => pg.Condition == "" && IsAfterFirstImport(pg))
                   ?? projectRootElement.AddPropertyGroup();
        }

        /// <summary>
        /// Determines if all the properties in two property groups are identical.
        /// </summary>
        public static bool ArePropertyGroupElementsIdentical(ProjectPropertyGroupElement groupA, ProjectPropertyGroupElement groupB)
        {
            return groupA.Properties.Count == groupB.Properties.Count
                   && groupA.Properties.All(propA => groupB.Properties.Any(propB => ProjectPropertyHelpers.ArePropertiesEqual(propA, propB)));
        }

        /// <summary>
        /// Checks if there is a reference to System.Web, which is unsupported on .NET Core, in the given project.
        /// </summary>
        public static bool IsProjectReferencingSystemWeb(MSBuildProjectRootElement root) => root.ItemGroups.Any(ig => ig.Items.Any(ProjectItemHelpers.IsReferencingSystemWeb));

        /// <summary>
        /// Unquote string. It simply removes the starting and ending "'", and checks they are present before.
        /// </summary>
        /// <param name="s">string to unquote </param>
        /// <returns>true if string is successfuly unquoted</returns>
        private static bool UnquoteString(ref string s)
        {
            if (s.Length < 2 || s[0] != '\'' || s[s.Length - 1] != '\'')
            {
                return false;
            }

            s = s.Substring(1, s.Length - 2);
            return true;
        }

        /// <summary>
        /// Given an optional path to MSBuild, registers an MSBuild.exe to be used for assembly resolution with this tool.
        /// </summary>
        public static string HookAssemblyResolveForMSBuild(string msbuildPath = null)
        {
            msbuildPath = GetMSBuildPathIfNotSpecified(msbuildPath);
            if (string.IsNullOrWhiteSpace(msbuildPath))
            {
                Console.WriteLine("Cannot find MSBuild. Please pass in a path to msbuild using -m or run from a developer command prompt.");
                return null;
            }

            AppDomain.CurrentDomain.AssemblyResolve += (sender, eventArgs) =>
            {
                var targetAssembly = Path.Combine(msbuildPath, new AssemblyName(eventArgs.Name).Name + ".dll");
                return File.Exists(targetAssembly) ? Assembly.LoadFrom(targetAssembly) : null;
            };

            return msbuildPath;
        }

        /// <summary>
        /// Given an optional path to MSBuild, finds an MSBuild path. Will query Visual Studio instances and ask for user input if there are multitple ones.
        /// </summary>
        private static string GetMSBuildPathIfNotSpecified(string msbuildPath = null)
        {
            // If the user specified a msbuild path use that.
            if (!string.IsNullOrEmpty(msbuildPath))
            {
                return msbuildPath;
            }

            // If the user is running from a developer command prompt use the MSBuild of that VS
            var vsinstalldir = Environment.GetEnvironmentVariable("VSINSTALLDIR");
            if (!string.IsNullOrEmpty(vsinstalldir))
            {
                return Path.Combine(vsinstalldir, "MSBuild", "Current", "Bin");
            }

            // Attempt to set the version of MSBuild.
            var visualStudioInstances = MSBuildLocator.QueryVisualStudioInstances().ToArray();
            var instance = visualStudioInstances.Length == 1
                // If there is only one instance of MSBuild on this machine, set that as the one to use.
                ? visualStudioInstances[0]
                // Handle selecting the version of MSBuild you want to use.
                : SelectVisualStudioInstance(visualStudioInstances);

            return instance?.MSBuildPath;
        }

        private static VisualStudioInstance SelectVisualStudioInstance(VisualStudioInstance[] visualStudioInstances)
        {
            Console.WriteLine("Multiple installs of MSBuild detected please select one:");
            for (int i = 0; i < visualStudioInstances.Length; i++)
            {
                Console.WriteLine($"Instance {i + 1}");
                Console.WriteLine($"    Name: {visualStudioInstances[i].Name}");
                Console.WriteLine($"    Version: {visualStudioInstances[i].Version}");
                Console.WriteLine($"    MSBuild Path: {visualStudioInstances[i].MSBuildPath}");
            }

            while (true)
            {
                var userResponse = Console.ReadLine();
                if (int.TryParse(userResponse, out int instanceNumber) &&
                    instanceNumber > 0 &&
                    instanceNumber <= visualStudioInstances.Length)
                {
                    return visualStudioInstances[instanceNumber - 1];
                }
                Console.WriteLine("Input not accepted, try again.");
            }
        }
    }
}