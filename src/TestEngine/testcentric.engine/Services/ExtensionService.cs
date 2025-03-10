// ***********************************************************************
// Copyright (c) Charlie Poole and TestCentric GUI contributors.
// Licensed under the MIT License. See LICENSE file in root directory.
// ***********************************************************************

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Mono.Cecil;
using NUnit.Engine;
using NUnit.Engine.Extensibility;
using TestCentric.Engine.Extensibility;
using TestCentric.Engine.Internal;

namespace TestCentric.Engine.Services
{
    /// <summary>
    /// The ExtensionService discovers ExtensionPoints and Extensions and
    /// maintains them in a database. It can return extension nodes or
    /// actual extension objects on request.
    /// </summary>
    public class ExtensionService : Service, IExtensionService
    {
        static Logger log = InternalTrace.GetLogger(typeof(ExtensionService));
        static readonly Version COMPATIBLE_NUNIT_VERSION;

        private readonly List<ExtensionPoint> _extensionPoints = new List<ExtensionPoint>();
        private readonly Dictionary<string, ExtensionPoint> _pathIndex = new Dictionary<string, ExtensionPoint>();

        private readonly List<ExtensionNode> _extensions = new List<ExtensionNode>();
        private readonly List<ExtensionAssembly> _assemblies = new List<ExtensionAssembly>();

        static ExtensionService()
        {
            // Default - in case no attribute is found
            COMPATIBLE_NUNIT_VERSION = new Version(4, 0);

            var apiAssembly = typeof(IExtensionService).Assembly;
            log.Debug($"  Using API Assembly {apiAssembly.GetName().FullName}");
            var attrs = apiAssembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);

            if (attrs.Length > 0)
            {
                var attr = attrs[0] as AssemblyInformationalVersionAttribute;
                string version = attr.InformationalVersion;
                int dash = version.IndexOf('-');
                if (dash > 0)
                    version = version.Substring(0, dash);
                COMPATIBLE_NUNIT_VERSION = new Version(version);
            }
        }

        public IList<Assembly> RootAssemblies { get; } = new List<Assembly>();

        /// <summary>
        /// Gets an enumeration of all ExtensionPoints in the engine.
        /// </summary>
        public IEnumerable<IExtensionPoint> ExtensionPoints
        {
            get { return _extensionPoints.ToArray(); }
        }

        /// <summary>
        /// Gets an enumeration of all installed Extensions.
        /// </summary>
        public IEnumerable<IExtensionNode> Extensions
        {
            get { return _extensions.ToArray(); }
        }

        /// <summary>
        /// Get an ExtensionPoint based on its unique identifying path.
        /// </summary>
        IExtensionPoint IExtensionService.GetExtensionPoint(string path)
        {
            return this.GetExtensionPoint(path);
        }

        /// <summary>
        /// Get an enumeration of ExtensionNodes based on their identifying path.
        /// </summary>
        IEnumerable<IExtensionNode> IExtensionService.GetExtensionNodes(string path)
        {
            foreach (var node in this.GetExtensionNodes(path))
                yield return node;
        }

        /// <summary>
        /// Enable or disable an extension
        /// </summary>
        public void EnableExtension(string typeName, bool enabled)
        {
            foreach (var node in _extensions)
                if (node.TypeName == typeName)
                    node.Enabled = enabled;
        }

        /// <summary>
        /// Get an ExtensionPoint based on its unique identifying path.
        /// </summary>
        public ExtensionPoint GetExtensionPoint(string path)
        {
            return _pathIndex.ContainsKey(path) ? _pathIndex[path] : null;
        }

        /// <summary>
        /// Get an ExtensionPoint based on the required Type for extensions.
        /// </summary>
        public ExtensionPoint GetExtensionPoint(Type type)
        {
            foreach (var ep in _extensionPoints)
                if (ep.TypeName == type.FullName)
                    return ep;

            return null;
        }

        /// <summary>
        /// Get an ExtensionPoint based on a Cecil TypeReference.
        /// </summary>
        private ExtensionPoint GetExtensionPoint(TypeReference type)
        {
            foreach (var ep in _extensionPoints)
                if (ep.TypeName == type.FullName)
                    return ep;

            return null;
        }

        public IEnumerable<ExtensionNode> GetExtensionNodes(string path)
        {
            var ep = GetExtensionPoint(path);
            if (ep != null)
                foreach (var node in ep.Extensions)
                    yield return node;
        }

        public ExtensionNode GetExtensionNode(string path)
        {
            var ep = GetExtensionPoint(path);

            return ep != null && ep.Extensions.Count > 0 ? ep.Extensions[0] : null;
        }

        public IEnumerable<ExtensionNode> GetExtensionNodes<T>(bool includeDisabled = false)
        {
            var ep = GetExtensionPoint(typeof(T));
            if (ep != null)
                foreach (var node in ep.Extensions)
                    if (includeDisabled || node.Enabled)
                        yield return node;
        }

        public IEnumerable<T> GetExtensions<T>()
        {
            foreach (var node in GetExtensionNodes<T>())
                yield return (T)node.ExtensionObject;
        }

        public override void StartService()
        {
            try
            {
                var thisAssembly = Assembly.GetExecutingAssembly();
                var apiAssembly = typeof(ITestEngine).Assembly;

                // TODO: We need a more general way to locate extension points
                // without needing to know which assemblies contain them in advance.
                // RootAssemblies could handle that if we initialized it somewhere
                // but we don't do that currently.
                foreach (var assembly in RootAssemblies)
                    FindExtensionPoints(assembly);
                FindExtensionPoints(thisAssembly);
                FindExtensionPoints(apiAssembly);

                // Temp adhoc fix
                FindExtensionPoints(typeof(IAgentLauncher).Assembly);

                // Create the list of possible extension assemblies,
                // eliminating duplicates. Start in Engine directory.
                var startDir = new DirectoryInfo(AssemblyHelper.GetDirectoryName(thisAssembly));
                FindExtensionAssemblies(startDir);

                // Check each assembly to see if it contains extensions
                foreach (var candidate in _assemblies)
                    FindExtensionsInAssembly(candidate);

                Status = ServiceStatus.Started;
            }
            catch
            {
                Status = ServiceStatus.Error;
                throw;
            }
        }

        /// <summary>
        /// Find the extension points in a loaded assembly.
        /// Public for testing.
        /// </summary>
        public void FindExtensionPoints(Assembly assembly)
        {
            log.Info("Scanning {0} assembly for extension points", assembly.GetName().Name);

            foreach (ExtensionPointAttribute attr in assembly.GetCustomAttributes(typeof(ExtensionPointAttribute), false))
            {
                if (_pathIndex.ContainsKey(attr.Path))
                {
                    string msg = string.Format(
                        "The Path {0} is already in use for another extension point.",
                        attr.Path);
                    throw new NUnitEngineException(msg);
                }

                var ep = new ExtensionPoint(attr.Path, attr.Type)
                {
                    Description = attr.Description,
                };

                _extensionPoints.Add(ep);
                _pathIndex.Add(ep.Path, ep);

                log.Info("  Found Path={0}, Type={1}", ep.Path, ep.TypeName);
            }

            foreach (Type type in assembly.GetExportedTypes())
            {
                foreach (TypeExtensionPointAttribute attr in type.GetCustomAttributes(typeof(TypeExtensionPointAttribute), false))
                {
                    string path = attr.Path ?? "/NUnit/Engine/TypeExtensions/" + type.Name;

                    if (_pathIndex.ContainsKey(path))
                    {
                        string msg = string.Format(
                            "The Path {0} is already in use for another extension point.",
                            attr.Path);
                        throw new NUnitEngineException(msg);
                    }

                    var ep = new ExtensionPoint(path, type)
                    {
                        Description = attr.Description,
                    };

                    _extensionPoints.Add(ep);
                    _pathIndex.Add(path, ep);

                    log.Info("  Found Path={0}, Type={1}", ep.Path, ep.TypeName);
                }
            }
        }

        /// <summary>
        /// Deduce the extension point based on the Type of an extension.
        /// Returns null if no extension point can be found that would
        /// be satisfied by the provided Type.
        /// </summary>
        private ExtensionPoint DeduceExtensionPointFromType(TypeReference typeRef)
        {
            var ep = GetExtensionPoint(typeRef);
            if (ep != null)
                return ep;

            TypeDefinition typeDef = typeRef.Resolve();


            foreach (InterfaceImplementation iface in typeDef.Interfaces)
            {
                ep = DeduceExtensionPointFromType(iface.InterfaceType);
                if (ep != null)
                    return ep;
            }

            TypeReference baseType = typeDef.BaseType;
            return baseType != null && baseType.FullName != "System.Object"
                ? DeduceExtensionPointFromType(baseType)
                : null;
        }

        /// <summary>
        /// Find candidate extension assemblies starting from a
        /// given base directory.
        /// </summary>
        /// <param name="startDir"></param>
        private void FindExtensionAssemblies(DirectoryInfo startDir)
        {
            log.Info($"Searching for extensions compatible with NUnit {COMPATIBLE_NUNIT_VERSION} API");

            ProcessAddinsFiles(startDir, false);
        }

        /// <summary>
        /// Scans a directory for candidate addin assemblies. Note that assemblies in
        /// the directory are only scanned if no file of type .addins is found. If such
        /// a file is found, then those assemblies it references are scanned.
        /// </summary>
        private void ProcessDirectory(DirectoryInfo startDir, bool fromWildCard)
        {
            log.Info("Scanning directory {0} for extensions", startDir.FullName);

            if (ProcessAddinsFiles(startDir, fromWildCard) == 0)
                foreach (var file in startDir.GetFiles("*.dll"))
                    ProcessCandidateAssembly(file.FullName, true);
        }

        /// <summary>
        /// Process all .addins files found in a directory
        /// </summary>
        private int ProcessAddinsFiles(DirectoryInfo startDir, bool fromWildCard)
        {
            var addinsFiles = startDir.GetFiles("*.addins");

            if (addinsFiles.Length > 0)
                foreach (var file in addinsFiles)
                    ProcessAddinsFile(startDir, file.FullName, fromWildCard);

            return addinsFiles.Length;
        }

        /// <summary>
        /// Process a .addins type file. The file contains one entry per
        /// line. Each entry may be a directory to scan, an assembly
        /// path or a wildcard pattern used to find assemblies. Blank
        /// lines and comments started by # are ignored.
        /// </summary>
        private void ProcessAddinsFile(DirectoryInfo baseDir, string fileName, bool fromWildCard)
        {
            log.Info("Processing file " + fileName);

            using (var rdr = new StreamReader(fileName))
            {
                while (!rdr.EndOfStream)
                {
                    var line = rdr.ReadLine();
                    if (line == null)
                        break;

                    line = line.Split(new char[] { '#' })[0].Trim();

                    if (line == string.Empty)
                        continue;

                    if (Path.DirectorySeparatorChar == '\\')
                        line = line.Replace(Path.DirectorySeparatorChar, '/');

                    bool isWild = fromWildCard || line.Contains("*");
                    if (line.EndsWith("/"))
                        foreach (var dir in DirectoryFinder.GetDirectories(baseDir, line))
                            ProcessDirectory(dir, isWild);
                    else
                        foreach (var file in DirectoryFinder.GetFiles(baseDir, line))
                            ProcessCandidateAssembly(file.FullName, isWild);
                }
            }
        }

        private void ProcessCandidateAssembly(string filePath, bool fromWildCard)
        {
            if (!Visited(filePath))
            {
                Visit(filePath);

                try
                {
                    var candidate = new ExtensionAssembly(filePath, fromWildCard);

                    for (int i = 0; i < _assemblies.Count; i++)
                    {
                        var assembly = _assemblies[i];

                        if (candidate.IsDuplicateOf(assembly))
                        {
                            if (candidate.IsBetterVersionOf(assembly))
                                _assemblies[i] = candidate;

                            return;
                        }
                    }

                    _assemblies.Add(candidate);
                }
                catch (BadImageFormatException e)
                {
                    if (!fromWildCard)
                        throw new NUnitEngineException(String.Format("Specified extension {0} could not be read", filePath), e);
                }
                catch (NUnitEngineException)
                {
                    if (!fromWildCard)
                        throw;
                }
            }
        }

        private Dictionary<string, object> _visited = new Dictionary<string, object>();

        private bool Visited(string filePath)
        {
            return _visited.ContainsKey(filePath);
        }

        private void Visit(string filePath)
        {
            _visited.Add(filePath, null);
        }

        /// <summary>
        /// Scan a single assembly for extensions marked by ExtensionAttribute.
        /// For each extension, create an ExtensionNode and link it to the
        /// correct ExtensionPoint. Internal for testing.
        /// </summary>
        internal void FindExtensionsInAssembly(ExtensionAssembly assembly)
        {
            log.Info("Scanning {0} assembly for Extensions", assembly.FilePath);

            if (CanLoadTargetFramework(Assembly.GetEntryAssembly(), assembly))
            {

                IRuntimeFramework assemblyTargetFramework = null;

                foreach (var type in assembly.MainModule.GetTypes())
                {
                    log.Debug($"Examining Type {type.Name}");

                    CustomAttribute extensionAttr = type.GetAttribute("NUnit.Engine.Extensibility.ExtensionAttribute");

                    if (extensionAttr == null)
                        continue;

                    log.Info($"ExtensionAttribute found on {type.Name}");

                    object versionArg = extensionAttr.GetNamedArgument("EngineVersion");
                    if (versionArg != null)
                    {
                        var requiredVersion = new Version((string)versionArg);
                        if (requiredVersion > COMPATIBLE_NUNIT_VERSION)
                        {
                            log.Warning($"  Ignoring {type.Name} because it requires NUnit {requiredVersion} API");
                            continue;
                        }
                    }

                    var node = new ExtensionNode(assembly.FilePath, assembly.AssemblyVersion, type.FullName, assemblyTargetFramework);
                    node.Path = extensionAttr.GetNamedArgument("Path") as string;
                    node.Description = extensionAttr.GetNamedArgument("Description") as string;

                    object enabledArg = extensionAttr.GetNamedArgument("Enabled");
                    node.Enabled = enabledArg != null
                        ? (bool)enabledArg : true;

                    foreach (var attr in type.GetAttributes("NUnit.Engine.Extensibility.ExtensionPropertyAttribute"))
                    {
                        string name = attr.ConstructorArguments[0].Value as string;
                        string value = attr.ConstructorArguments[1].Value as string;

                        if (name != null && value != null)
                        {
                            node.AddProperty(name, value);
                            log.Info("        ExtensionProperty {0} = {1}", name, value);
                        }
                    }

                    _extensions.Add(node);
                    log.Info($"Added extension {node.TypeName}");

                    ExtensionPoint ep;
                    if (node.Path == null)
                    {
                        ep = DeduceExtensionPointFromType(type);
                        if (ep == null)
                        {
                            string msg = string.Format(
                                "Unable to deduce ExtensionPoint for Type {0}. Specify Path on ExtensionAttribute to resolve.",
                                type.FullName);
                            throw new NUnitEngineException(msg);
                        }

                        node.Path = ep.Path;
                    }
                    else
                    {
                        ep = GetExtensionPoint(node.Path);
                        if (ep == null)
                        {
                            string msg = string.Format(
                                "Unable to locate ExtensionPoint for Type {0}. The Path {1} cannot be found.",
                                type.FullName,
                                node.Path);
                            throw new NUnitEngineException(msg);
                        }
                    }

                    ep.Install(node);
                    log.Info($"Installed extension {node.TypeName} at path {node.Path}");
                }
            }
        }

        /// <summary>
        /// Checks that the target framework of the current runner can load the extension assembly. For example, .NET Core
        /// cannot load .NET Framework assemblies and vice-versa.
        /// </summary>
        /// <param name="runnerAsm">The executing runner</param>
        /// <param name="extensionAsm">The extension we are attempting to load</param>
        internal static bool CanLoadTargetFramework(Assembly runnerAsm, ExtensionAssembly extensionAsm)
        {
            if (runnerAsm == null)
                return true;

            var extensionFrameworkName = AssemblyDefinition.ReadAssembly(extensionAsm.FilePath).GetFrameworkName();
            var runnerFrameworkName = AssemblyDefinition.ReadAssembly(runnerAsm.Location).GetFrameworkName();
            if (runnerFrameworkName?.StartsWith(".NETStandard") == true)
            {
                throw new NUnitEngineException($"{runnerAsm.FullName} test runner must target .NET Core or .NET Framework, not .NET Standard");
            }
            else if (runnerFrameworkName?.StartsWith(".NETCoreApp") == true)
            {
                if (extensionFrameworkName?.StartsWith(".NETStandard") != true && extensionFrameworkName?.StartsWith(".NETCoreApp") != true)
                {
                    log.Info($".NET Core runners require .NET Core or .NET Standard extension for {extensionAsm.FilePath}");
                    return false;
                }
            }
            else if (extensionFrameworkName?.StartsWith(".NETCoreApp") == true)
            {
                log.Info($".NET Framework runners cannot load .NET Core extension {extensionAsm.FilePath}");
                return false;
            }

            return true;
        }
    }
}
