namespace GitVersion
{
    using Helpers;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;

    public class AssemblyInfoFileUpdater : IDisposable
    {
        readonly List<Action> restoreBackupTasks = new List<Action>();
        readonly List<Action> cleanupBackupTasks = new List<Action>();

        readonly IDictionary<string, Regex> assemblyAttributeRegexes = new Dictionary<string, Regex>
        {
            {".cs", new Regex( @"(\s*\[\s*assembly:\s*(?:.*)\s*\]\s*$(\r?\n)?)", RegexOptions.Multiline) },
            {".fs", new Regex( @"(\s*\[\s*\<assembly:\s*(?:.*)\>\s*\]\s*$(\r?\n)?)", RegexOptions.Multiline) },
            {".vb", new Regex( @"(\s*\<Assembly:\s*(?:.*)\>\s*$(\r?\n)?)", RegexOptions.Multiline) },
        };

        ISet<string> assemblyInfoFileNames;
        string workingDirectory;
        VersionVariables variables;
        IFileSystem fileSystem;
        bool ensureAssemblyInfo;
        TemplateManager templateManager;

        public AssemblyInfoFileUpdater(string assemblyInfoFileName, string workingDirectory, VersionVariables variables, IFileSystem fileSystem, bool ensureAssemblyInfo) :
                this(new HashSet<string> { assemblyInfoFileName }, workingDirectory, variables, fileSystem, ensureAssemblyInfo)
        { }

        public AssemblyInfoFileUpdater(ISet<string> assemblyInfoFileNames, string workingDirectory, VersionVariables variables, IFileSystem fileSystem, bool ensureAssemblyInfo)
        {
            this.assemblyInfoFileNames = assemblyInfoFileNames;
            this.workingDirectory = workingDirectory;
            this.variables = variables;
            this.fileSystem = fileSystem;
            this.ensureAssemblyInfo = ensureAssemblyInfo;

            templateManager = new TemplateManager(TemplateType.VersionAssemblyInfoResources);
        }

        public void Update()
        {
            Logger.WriteInfo("Updating assembly info files");

            var assemblyInfoFiles = GetAssemblyInfoFiles(workingDirectory, assemblyInfoFileNames, fileSystem, ensureAssemblyInfo).ToList();
            Logger.WriteInfo($"Found {assemblyInfoFiles.Count} files");

            var assemblyVersion = variables.AssemblySemVer;
            var assemblyVersionRegex = new Regex(@"AssemblyVersion(Attribute)?\s*\(.*\)\s*");
            var assemblyVersionString = !string.IsNullOrWhiteSpace(assemblyVersion) ? $"AssemblyVersion(\"{assemblyVersion}\")" : null;

            var assemblyInfoVersion = variables.InformationalVersion;
            var assemblyInfoVersionRegex = new Regex(@"AssemblyInformationalVersion(Attribute)?\s*\(.*\)\s*");
            var assemblyInfoVersionString = $"AssemblyInformationalVersion(\"{assemblyInfoVersion}\")";

            var assemblyFileVersion = variables.AssemblySemFileVer;
            var assemblyFileVersionRegex = new Regex(@"AssemblyFileVersion(Attribute)?\s*\(.*\)\s*");
            var assemblyFileVersionString = !string.IsNullOrWhiteSpace(assemblyFileVersion) ? $"AssemblyFileVersion(\"{assemblyFileVersion}\")" : null;

            foreach (var assemblyInfoFile in assemblyInfoFiles)
            {
                var backupAssemblyInfo = assemblyInfoFile.FullName + ".bak";
                var localAssemblyInfo = assemblyInfoFile.FullName;
                fileSystem.Copy(assemblyInfoFile.FullName, backupAssemblyInfo, true);

                restoreBackupTasks.Add(() =>
                {
                    if (fileSystem.Exists(localAssemblyInfo))
                    {
                        fileSystem.Delete(localAssemblyInfo);
                    }

                    fileSystem.Move(backupAssemblyInfo, localAssemblyInfo);
                });

                cleanupBackupTasks.Add(() => fileSystem.Delete(backupAssemblyInfo));

                var originalFileContents = fileSystem.ReadAllText(assemblyInfoFile.FullName);
                var fileContents = originalFileContents;
                var appendedAttributes = false;

                if (!string.IsNullOrWhiteSpace(assemblyVersion))
                {
                    fileContents = ReplaceOrInsertAfterLastAssemblyAttributeOrAppend(assemblyVersionRegex, fileContents, assemblyVersionString, assemblyInfoFile.Extension, ref appendedAttributes);
                }

                if (!string.IsNullOrWhiteSpace(assemblyFileVersion))
                {
                    fileContents = ReplaceOrInsertAfterLastAssemblyAttributeOrAppend(assemblyFileVersionRegex, fileContents, assemblyFileVersionString, assemblyInfoFile.Extension, ref appendedAttributes);
                }

                fileContents = ReplaceOrInsertAfterLastAssemblyAttributeOrAppend(assemblyInfoVersionRegex, fileContents, assemblyInfoVersionString, assemblyInfoFile.Extension, ref appendedAttributes);

                if (appendedAttributes)
                {
                    // If we appended any attributes, put a new line after them
                    fileContents += Environment.NewLine;
                }

                if (originalFileContents != fileContents)
                {
                    fileSystem.WriteAllText(assemblyInfoFile.FullName, fileContents);
                }
            }
        }

        string ReplaceOrInsertAfterLastAssemblyAttributeOrAppend(Regex replaceRegex, string inputString, string replaceString, string fileExtension, ref bool appendedAttributes)
        {
            var assemblyAddFormat = templateManager.GetAddFormatFor(fileExtension);

            if (replaceRegex.IsMatch(inputString))
            {
                return replaceRegex.Replace(inputString, replaceString);
            }

            if (assemblyAttributeRegexes.TryGetValue(fileExtension, out var assemblyRegex))
            {
                var assemblyMatches = assemblyRegex.Matches(inputString);
                if (assemblyMatches.Count > 0)
                {
                    var lastMatch = assemblyMatches[assemblyMatches.Count - 1];
                    var replacementString = lastMatch.Value;
                    if (!lastMatch.Value.EndsWith(Environment.NewLine)) replacementString += Environment.NewLine;
                    replacementString += string.Format(assemblyAddFormat, replaceString);
                    replacementString += Environment.NewLine;
                    return inputString.Replace(lastMatch.Value, replacementString);
                }
            }
			
            inputString += Environment.NewLine + string.Format(assemblyAddFormat, replaceString);
            appendedAttributes = true;
            return inputString;
        }

        IEnumerable<FileInfo> GetAssemblyInfoFiles(string workingDirectory, ISet<string> assemblyInfoFileNames, IFileSystem fileSystem, bool ensureAssemblyInfo)
        {
            if (assemblyInfoFileNames != null && assemblyInfoFileNames.Any(x => !string.IsNullOrWhiteSpace(x)))
            {
                foreach (var item in assemblyInfoFileNames)
                {
                    var fullPath = Path.Combine(workingDirectory, item);

                    if (EnsureVersionAssemblyInfoFile(ensureAssemblyInfo, fileSystem, fullPath))
                    {
                        yield return new FileInfo(fullPath);
                    }
                }
            }
            else
            {
                foreach (var item in fileSystem.DirectoryGetFiles(workingDirectory, "AssemblyInfo.*", SearchOption.AllDirectories))
                {
                    var assemblyInfoFile = new FileInfo(item);

                    if (templateManager.IsSupported(assemblyInfoFile.Extension))
                    {
                        yield return assemblyInfoFile;
                    }
                }
            }
        }

        bool EnsureVersionAssemblyInfoFile(bool ensureAssemblyInfo, IFileSystem fileSystem, string fullPath)
        {
            if (fileSystem.Exists(fullPath))
            {
                return true;
            }

            if (!ensureAssemblyInfo)
            {
                return false;
            }

            var assemblyInfoSource = templateManager.GetTemplateFor(Path.GetExtension(fullPath));

            if (!string.IsNullOrWhiteSpace(assemblyInfoSource))
            {
                var fileInfo = new FileInfo(fullPath);

                if (fileInfo.Directory != null && !fileSystem.DirectoryExists(fileInfo.Directory.FullName))
                {
                    fileSystem.CreateDirectory(fileInfo.Directory.FullName);
                }

                fileSystem.WriteAllText(fullPath, assemblyInfoSource);
                return true;
            }

            Logger.WriteWarning($"No version assembly info template available to create source file '{fullPath}'");
            return false;
        }

        public void Dispose()
        {
            foreach (var restoreBackup in restoreBackupTasks)
            {
                restoreBackup();
            }

            cleanupBackupTasks.Clear();
            restoreBackupTasks.Clear();
        }

        public void CommitChanges()
        {
            foreach (var cleanupBackupTask in cleanupBackupTasks)
            {
                cleanupBackupTask();
            }

            cleanupBackupTasks.Clear();
            restoreBackupTasks.Clear();
        }
    }
}
