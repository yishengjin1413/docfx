﻿namespace Microsoft.DocAsCode.EntityModel
{
    using Microsoft.DocAsCode.Utility;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.MSBuild;
    using Microsoft.CodeAnalysis.Workspaces.Dnx;
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using CS = Microsoft.CodeAnalysis.CSharp;
    using VB = Microsoft.CodeAnalysis.VisualBasic;

    public class ExtractMetadataWorker
    {
        private static readonly Lazy<MSBuildWorkspace> Workspace = new Lazy<MSBuildWorkspace>(() => MSBuildWorkspace.Create());
        private static string[] SupportedSolutionExtensions = { ".sln" };
        private static string[] SupportedProjectName = { "project.json" };
        private static string[] SupportedProjectExtensions = { ".csproj", ".vbproj" };
        private static string[] SupportedSourceFileExtensions = { ".cs", ".vb" };
        private static string[] SupportedVBSourceFileExtensions = { ".vb" };
        private static string[] SupportedCSSourceFileExtensions = { ".cs" };
        private static List<string> SupportedExtensions = new List<string>();
        private readonly ExtractMetadataInputModel _validInput;
        private readonly ExtractMetadataInputModel _rawInput;
        private readonly bool _rebuild;

        static ExtractMetadataWorker()
        {
            SupportedExtensions.AddRange(SupportedSolutionExtensions);
            SupportedExtensions.AddRange(SupportedProjectExtensions);
            SupportedExtensions.AddRange(SupportedSourceFileExtensions);
        }

        public ExtractMetadataWorker(ExtractMetadataInputModel input, bool rebuild)
        {
            _rawInput = input;
            _validInput = ValidateInput(input);
            _rebuild = rebuild;
        }

        public async Task<ParseResult> ExtractMetadataAsync()
        {
            var validInput = _validInput;
            if (validInput == null)
            {
                var result = new ParseResult(ResultLevel.Warning, "No valid file is found from input {0}. Exiting...", _rawInput.ToString());
                return result;
            }

            try
            {
                foreach (var pair in validInput.Items)
                {
                    var inputs = pair.Value;
                    var outputFolder = pair.Key;
                    await SaveAllMembersFromCacheAsync(inputs, outputFolder, _rebuild);
                }
            }
            catch (Exception e)
            {
                var result = new ParseResult(ResultLevel.Error, "Error extracting metadata for {0}: {1}", _rawInput.ToString(), e.ToString());
                return result;
            }

            return new ParseResult(ResultLevel.Success);
        }

        #region Internal For UT
        internal static MetadataItem GenerateYamlMetadata(Compilation compilation)
        {
            if (compilation == null)
            {
                return null;
            }

            object visitorContext = new object();
            SymbolVisitorAdapter visitor;
            if (compilation.Language == "Visual Basic")
            {
                visitor = new SymbolVisitorAdapter(new CSYamlModelGenerator() + new VBYamlModelGenerator(), SyntaxLanguage.VB);
            }
            else if (compilation.Language == "C#")
            {
                visitor = new SymbolVisitorAdapter(new CSYamlModelGenerator() + new VBYamlModelGenerator(), SyntaxLanguage.CSharp);
            }
            else
            {
                Debug.Assert(false, "Language not supported: " + compilation.Language);
                ParseResult.WriteToConsole(ResultLevel.Error, "Language not supported: " + compilation.Language);
                return null;
            }

            MetadataItem item = compilation.Assembly.Accept(visitor);
            return item;
        }

        #endregion

        #region Private
        #region Check Supportability
        private static bool IsSupported(string filePath)
        {
            return IsSupported(filePath, SupportedExtensions, SupportedProjectName);
        }

        private static bool IsSupportedSolution(string filePath)
        {
            return IsSupported(filePath, SupportedSolutionExtensions);
        }

        private static bool IsSupportedProject(string filePath)
        {
            return IsSupported(filePath, SupportedProjectExtensions, SupportedProjectName);
        }

        private static bool IsSupportedSourceFile(string filePath)
        {
            return IsSupported(filePath, SupportedSourceFileExtensions);
        }

        private static bool IsSupportedVBSourceFile(string filePath)
        {
            return IsSupported(filePath, SupportedVBSourceFileExtensions);
        }

        private static bool IsSupportedCSSourceFile(string filePath)
        {
            return IsSupported(filePath, SupportedCSSourceFileExtensions);
        }

        private static bool IsSupported(string filePath, IEnumerable<string> supportedExtension, params string[] supportedFileName)
        {
            var fileExtension = Path.GetExtension(filePath);
            var fileName = Path.GetFileName(filePath);
            return supportedExtension.Contains(fileExtension, StringComparer.OrdinalIgnoreCase) || supportedFileName.Contains(fileName, StringComparer.OrdinalIgnoreCase);
        }
        #endregion

        private static ExtractMetadataInputModel ValidateInput(ExtractMetadataInputModel input)
        {
            if (input == null) return null;

            if (input.Items == null || input.Items.Count == 0)
            {
                ParseResult.WriteToConsole(ResultLevel.Warning, "No source project or file to process, exiting...");
                return null;
            }

            var items = new Dictionary<string, List<string>>();

            // 1. Input file should exists
            foreach (var pair in input.Items)
            {
                if (string.IsNullOrWhiteSpace(pair.Key))
                {
                    ParseResult.WriteToConsole(ResultLevel.Warning, "Empty folder name is found: '{0}': '{1}'. It is not supported, skipping", pair.Key, string.Join(",", pair.Value));
                    continue;
                }

                // HashSet to guarantee the input file path is unique
                HashSet<string> validFilePath = new HashSet<string>();
                foreach (var inputFilePath in pair.Value)
                {
                    if (!string.IsNullOrEmpty(inputFilePath))
                    {
                        if (File.Exists(inputFilePath))
                        {
                            if (IsSupported(inputFilePath))
                            {
                                var path = inputFilePath.ToNormalizedFullPath();
                                validFilePath.Add(path);
                            }
                            else
                            {
                                ParseResult.WriteToConsole(ResultLevel.Warning, "File {0} is not supported, supported file extension are: {1}. The file will be ignored.", inputFilePath, string.Join(",", SupportedExtensions));
                            }
                        }
                        else
                        {
                            ParseResult.WriteToConsole(ResultLevel.Warning, "File {0} does not exist, will be ignored.", inputFilePath);
                        }
                    }
                }

                if (validFilePath.Count > 0) items.Add(pair.Key, validFilePath.ToList());
            }

            if (items.Count > 0)
            {
                var clone = input.Clone();
                clone.Items = items;
                return clone;
            }
            else return null;
        }

        class ProjectDocumentCache
        {
            private readonly ConcurrentDictionary<string, HashSet<string>> _cache = new ConcurrentDictionary<string, HashSet<string>>();

            public ProjectDocumentCache() { }

            public ProjectDocumentCache(IDictionary<string, List<string>> inputs)
            {
                if (inputs == null) return;
                foreach(var input in inputs)
                {
                    HashSet<string> cacheValue = new HashSet<string>();
                    if (input.Value != null)
                    {
                        foreach (var item in input.Value)
                        {
                            cacheValue.Add(item.ToNormalizedFullPath());
                        }
                    }
                    if (cacheValue.Count > 0) _cache.TryAdd(input.Key.ToNormalizedFullPath(), cacheValue);
                }
            }

            public IDictionary<string, List<string>> Cache { get { return _cache.ToDictionary(s => s.Key, s => s.Value.ToList()); } }

            public IEnumerable<string> Documents { get { return _cache.Values.SelectMany(s => s.ToList()).Distinct(); } }
            public void AddDocuments(IEnumerable<string> documents)
            {
                var key = documents.OrderBy(s => s).FirstOrDefault();
                AddDocuments(key, documents);
            }

            public void AddDocuments(string projectPath, IEnumerable<string> documents)
            {
                if (string.IsNullOrEmpty(projectPath) || documents == null || !documents.Any()) return;
                var projectKey = projectPath.ToNormalizedFullPath();
                var documentCache = _cache.GetOrAdd(projectKey, s => new HashSet<string>());
                foreach(var document in documents)
                {
                    documentCache.Add(document.ToNormalizedFullPath());
                }
            }

            public void AddDocument(string projectPath, string document)
            {
                if (string.IsNullOrEmpty(projectPath) || string.IsNullOrEmpty(document)) return;
                var projectKey = projectPath.ToNormalizedFullPath();
                var documentCache = _cache.GetOrAdd(projectKey, s => new HashSet<string>());
                documentCache.Add(document.ToNormalizedFullPath());
            }

            public IEnumerable<string> GetDocuments(string projectPath)
            {
                if (string.IsNullOrEmpty(projectPath)) return null;
                var projectKey = projectPath.ToNormalizedFullPath();
                HashSet<string> documents = null;
                _cache.TryGetValue(projectKey, out documents);
                return documents.GetNormalizedFullPathList();
            }
        }

        private async Task SaveAllMembersFromCacheAsync(IEnumerable<string> inputs, string outputFolder, bool forceRebuild)
        {
            var projectCache = new ConcurrentDictionary<string, Project>();
            // Project<=>Documents
            var documentCache = new ProjectDocumentCache();
            DateTime triggeredTime = DateTime.UtcNow;
            var solutions = inputs.Where(s => IsSupportedSolution(s));
            var projects = inputs.Where(s => IsSupportedProject(s));

            var sourceFiles = inputs.Where(s => IsSupportedSourceFile(s));

            // Exclude not supported files from inputs
            inputs = solutions.Concat(projects).Concat(sourceFiles);

            // No matter is incremental or not, we have to load solutions into memory
            await solutions.ForEachInParallelAsync(async path =>
            {
                documentCache.AddDocument(path, path);
                var solution = await GetSolutionAsync(path);
                if (solution != null)
                {
                    foreach (var project in solution.Projects)
                    {
                        var filePath = project.FilePath;

                        // If the project is csproj/vbproj, add to project dictionary, otherwise, ignore
                        if (IsSupportedProject(filePath))
                        {
                            projectCache.GetOrAdd(project.FilePath, s => project);
                        }
                        else
                        {
                            ParseResult.WriteToConsole(ResultLevel.Warning, "Project {0} inside solution {1} is not supported, supported file extension are: {2}. The project will be ignored.", filePath, path, string.Join(",", SupportedExtensions));
                        }
                    }
                }
            }, 60);

            // Load additional projects out if it is not contained in expanded solution
            projects = projects.Except(projectCache.Keys).Distinct();

            await projects.ForEachInParallelAsync(async path =>
            {
                var project = await GetProjectAsync(path);
                if (project != null)
                {
                    projectCache.GetOrAdd(path, s => project);
                }
            }, 60);

            foreach(var item in projectCache)
            {
                var path = item.Key;
                var project = item.Value;
                documentCache.AddDocument(path, path);
                documentCache.AddDocuments(path, project.Documents.Select(s => s.FilePath));
                documentCache.AddDocuments(path, project.MetadataReferences
                    .Where(s => s is PortableExecutableReference)
                    .Select(s => ((PortableExecutableReference)s).FilePath));
            }

            documentCache.AddDocuments(sourceFiles);

            // Incremental check for inputs as a whole:
            var applicationCache = ApplicationLevelCache.Get(inputs);
            if (!forceRebuild)
            {
                BuildInfo buildInfo = applicationCache.GetValidConfig(inputs);
                if (buildInfo != null)
                {
                    IncrementalCheck check = new IncrementalCheck(buildInfo);
                    // 1. Check if sln files/ project files and its contained documents/ source files are modified
                    var projectModified = check.AreFilesModified(documentCache.Documents);

                    if (!projectModified)
                    {
                        // 2. Check if documents/ assembly references are changed in a project
                        // e.g. <Compile Include="*.cs* /> and file added/deleted
                        foreach (var project in projectCache.Values)
                        {
                            var key = project.FilePath.ToNormalizedFullPath();
                            IEnumerable<string> currentContainedFiles = documentCache.GetDocuments(project.FilePath);
                            var previousDocumentCache = new ProjectDocumentCache(buildInfo.ContainedFiles);

                            IEnumerable<string> previousContainedFiles = previousDocumentCache.GetDocuments(project.FilePath);
                            if (previousContainedFiles != null && currentContainedFiles != null)
                            {
                                projectModified = !previousContainedFiles.SequenceEqual(currentContainedFiles);
                            }
                            else
                            {
                                // When one of them is not null, project is modified
                                if (!object.Equals(previousContainedFiles, currentContainedFiles))
                                {
                                    projectModified = true;
                                }
                            }
                            if (projectModified) break;
                        }
                    }

                    if (!projectModified)
                    {
                        // Nothing modified, use the result in cache
                        try
                        {
                            CopyFromCachedResult(buildInfo, inputs, outputFolder);
                            return;
                        }
                        catch (Exception e)
                        {
                            ParseResult.WriteToConsole(ResultLevel.Warning, "Unable to copy results from cache: {0}. Rebuild starts.", e.Message);
                        }
                    }
                }
            }

            // Build all the projects to get the output and save to cache
            List<MetadataItem> projectMetadataList = new List<MetadataItem>();

            foreach (var project in projectCache)
            {
                var projectMetadata = await GetProjectMetadataFromCacheAsync(project.Value, outputFolder, documentCache, forceRebuild);
                if (projectMetadata != null) projectMetadataList.Add(projectMetadata);
            }

            var csFiles = sourceFiles.Where(s => IsSupportedCSSourceFile(s));
            if (csFiles.Any())
            {
                var csContent = string.Join(Environment.NewLine, csFiles.Select(s => File.ReadAllText(s)));
                var csCompilation = CreateCompilationFromCsharpCode(csContent);
                if (csCompilation != null)
                {
                    var csMetadata = await GetFileMetadataFromCacheAsync(csFiles, csCompilation, outputFolder, forceRebuild);
                    if (csMetadata != null) projectMetadataList.Add(csMetadata);
                }
            }

            var vbFiles = sourceFiles.Where(s => IsSupportedVBSourceFile(s));
            if (vbFiles.Any())
            {
                var vbContent = string.Join(Environment.NewLine, vbFiles.Select(s => File.ReadAllText(s)));
                var vbCompilation = CreateCompilationFromVBCode(vbContent);
                if (vbCompilation != null)
                {
                    var vbMetadata = await GetFileMetadataFromCacheAsync(vbFiles, vbCompilation, outputFolder, forceRebuild);
                    if (vbMetadata != null) projectMetadataList.Add(vbMetadata);
                }
            }

            var allMemebers = MergeYamlProjectMetadata(projectMetadataList);
            var allReferences = MergeYamlProjectReferences(projectMetadataList);
            
            if (allMemebers == null || allMemebers.Count == 0)
            {
                ParseResult.WriteToConsole(ResultLevel.Warning, "No metadata is generated for {0}.", projectMetadataList.Select(s => s.Name).ToDelimitedString());
                applicationCache.SaveToCache(inputs, null, triggeredTime, outputFolder, null);
            }
            else
            {
                // TODO: need an intermediate folder? when to clean it up?
                // Save output to output folder
                var outputFiles = ResolveAndExportYamlMetadata(allMemebers, allReferences, outputFolder, _validInput.IndexFileName, _validInput.TocFileName, _validInput.ApiFolderName);
                applicationCache.SaveToCache(inputs, documentCache.Cache, triggeredTime, outputFolder, outputFiles);
            }
        }

        private static void CopyFromCachedResult(BuildInfo buildInfo, IEnumerable<string> inputs, string outputFolder)
        {
            var outputFolderSource = buildInfo.OutputFolder;
            var relativeFiles = buildInfo.RelatvieOutputFiles;
            if (relativeFiles == null)
            {
                ParseResult.WriteToConsole(ResultLevel.Warning, "No metadata is generated for '{0}'.", inputs.ToDelimitedString());
                return;
            }

            ParseResult.WriteToConsole(ResultLevel.Info, "'{0}' keep up-to-date since '{1}', cached result from '{2}' is used.", inputs.ToDelimitedString(), buildInfo.TriggeredUtcTime.ToString(), buildInfo.OutputFolder);
            relativeFiles.Select(s => Path.Combine(outputFolderSource, s)).CopyFilesToFolder(outputFolderSource, outputFolder, true, s => ParseResult.WriteToConsole(ResultLevel.Info, s), null);
        }
        
        private static Task<MetadataItem> GetProjectMetadataFromCacheAsync(Project project, string outputFolder, ProjectDocumentCache documentCache, bool forceRebuild)
        {
            var projectFilePath = project.FilePath;
            var k = documentCache.GetDocuments(projectFilePath);
            return GetMetadataFromProjectLevelCacheAsync(
                project,
                new[] { projectFilePath },
                s => Task.FromResult(forceRebuild || s.AreFilesModified(k)),
                s => project.GetCompilationAsync(),
                s =>
                {
                    return new Dictionary<string, List<string>> { { s.FilePath.ToNormalizedFullPath(), k.ToList() } };
                },
                outputFolder);
        }

        private static Task <MetadataItem> GetFileMetadataFromCacheAsync(IEnumerable<string> files, Compilation compilation, string outputFolder, bool forceRebuild)
        {
            if (files == null || !files.Any()) return null;
            return GetMetadataFromProjectLevelCacheAsync(
                files,
                files, s => Task.FromResult(forceRebuild || s.AreFilesModified(files)),
                s => Task.FromResult(compilation),
                s => null,
                outputFolder);
        }

        private static async Task<MetadataItem> GetMetadataFromProjectLevelCacheAsync<T>(
            T input,
            IEnumerable<string> inputKey,
            Func<IncrementalCheck, Task<bool>> rebuildChecker,
            Func<T, Task<Compilation>> compilationProvider,
            Func<T, IDictionary<string, List<string>>> containedFilesProvider,
            string outputFolder)
        {
            DateTime triggeredTime = DateTime.UtcNow;
            var projectLevelCache = ProjectLevelCache.Get(inputKey);
            var projectConfig = projectLevelCache.GetValidConfig(inputKey);
            var rebuildProject = true;
            if (projectConfig != null)
            {
                var projectCheck = new IncrementalCheck(projectConfig);
                rebuildProject = await rebuildChecker(projectCheck);
            }

            MetadataItem projectMetadata;
            if (!rebuildProject)
            {
                // Load from cache
                var cacheFile = Path.Combine(projectConfig.OutputFolder, projectConfig.RelatvieOutputFiles.First());
                ParseResult.WriteToConsole(ResultLevel.Info, "'{0}' keep up-to-date since '{1}', cached intermediate result '{2}' is used.", projectConfig.InputFilesKey, projectConfig.TriggeredUtcTime.ToString(), cacheFile);
                var result = TryParseYamlMetadataFile(cacheFile, out projectMetadata);
                if (result.ResultLevel != ResultLevel.Success) result.WriteToConsole();
                if (projectMetadata == null)
                {
                    ParseResult.WriteToConsole(ResultLevel.Info, "'{0}' is invalid, rebuild needed.", projectConfig.InputFilesKey);
                }
                else
                {
                    return projectMetadata;
                }
            }

            var compilation = await compilationProvider(input);

            projectMetadata = GenerateYamlMetadata(compilation);
            var file = Path.GetRandomFileName();
            var cacheOutputFolder = projectLevelCache.OutputFolder;
            var path = Path.Combine(cacheOutputFolder, file);
            YamlUtility.Serialize(path, projectMetadata);
            ParseResult.WriteToConsole(ResultLevel.Success, "Successfully generated metadata {0} for {1}", cacheOutputFolder, projectMetadata.Name);

            IDictionary<string, List<string>> containedFiles = null;

            if (containedFilesProvider != null)
            {
                containedFiles = containedFilesProvider(input);
            }

            // Save to cache
            projectLevelCache.SaveToCache(inputKey, containedFiles, triggeredTime, cacheOutputFolder, new List<string>() { file });

            return projectMetadata;
        }

        private static IList<string> ResolveAndExportYamlMetadata(
            Dictionary<string, MetadataItem> allMembers,
            Dictionary<string, ReferenceItem> allReferences,
            string folder,
            string indexFileName,
            string tocFileName,
            string apiFolder)
        {
            var outputFiles = new List<string>();
            var model = YamlMetadataResolver.ResolveMetadata(allMembers, allReferences, apiFolder);
            
            // 1. generate toc.yml
            outputFiles.Add(tocFileName);
            model.TocYamlViewModel.Href = tocFileName;
            model.TocYamlViewModel.Type = MemberType.Toc;

            // TOC do not change
            var tocViewModel = TocViewModel.Convert(model.TocYamlViewModel);
            string tocFilePath = Path.Combine(folder, tocFileName);

            YamlUtility.Serialize(tocFilePath, tocViewModel);

            // 2. generate index.yml
            outputFiles.Add(indexFileName);
            string indexFilePath = Path.Combine(folder, indexFileName);
            YamlUtility.Serialize(indexFilePath, model.Indexer.ToViewModel());

            // 3. generate each item's yaml
            var members = model.Members;
            foreach (var memberModel in members)
            {
                outputFiles.Add(Path.Combine(apiFolder, memberModel.Href));
                string itemFilepath = Path.Combine(folder, apiFolder, memberModel.Href);
                Directory.CreateDirectory(Path.GetDirectoryName(itemFilepath));
                var memberViewModel = OnePageViewModel.Convert(memberModel);
                YamlUtility.Serialize(itemFilepath, memberViewModel);
                ParseResult.WriteToConsole(ResultLevel.Success, "Metadata file for {0} is saved to {1}", memberModel.Name, itemFilepath);
            }

            return outputFiles;
        }

        private static Dictionary<string, MetadataItem> MergeYamlProjectMetadata(List<MetadataItem> projectMetadataList)
        {
            if (projectMetadataList == null || projectMetadataList.Count == 0)
            {
                return null;
            }

            Dictionary<string, MetadataItem> namespaceMapping = new Dictionary<string, MetadataItem>();
            Dictionary<string, MetadataItem> allMembers = new Dictionary<string, MetadataItem>();

            foreach (var project in projectMetadataList)
            {
                if (project.Items != null)
                {
                    foreach (var ns in project.Items)
                    {
                        if (ns.Type == MemberType.Namespace)
                        {
                            MetadataItem nsOther;
                            if (namespaceMapping.TryGetValue(ns.Name, out nsOther))
                            {
                                if (ns.Items != null)
                                {
                                    if (nsOther.Items == null)
                                    {
                                        nsOther.Items = new List<MetadataItem>();
                                    }

                                    foreach(var i in ns.Items)
                                    {
                                        if (!nsOther.Items.Any(s => s.Name == i.Name))
                                        {
                                            nsOther.Items.Add(i);
                                        }
                                        else
                                        {
                                            ParseResult.WriteToConsole(ResultLevel.Info, "{0} already exists in {1}, ignore current one", i.Name, nsOther.Name);
                                        }
                                    }
                                }
                            }
                            else
                            {
                                namespaceMapping.Add(ns.Name, ns);
                            }
                        }

                        if (!allMembers.ContainsKey(ns.Name))
                        {
                            allMembers.Add(ns.Name, ns);
                        }

                        ns.Items?.ForEach(s =>
                        {
                            MetadataItem existingMetadata;
                            if (allMembers.TryGetValue(s.Name, out existingMetadata))
                            {
                                ParseResult.WriteToConsole(ResultLevel.Warning, "Duplicate member {0} is found from {1} and {2}, use the one in {1} and ignore the one from {2}", s.Name, existingMetadata.Source.Path, s.Source.Path);
                            }
                            else
                            {
                                allMembers.Add(s.Name, s);
                            }

                            s.Items?.ForEach(s1 =>
                            {
                                MetadataItem existingMetadata1;
                                if (allMembers.TryGetValue(s1.Name, out existingMetadata1))
                                {
                                    ParseResult.WriteToConsole(ResultLevel.Warning, "Duplicate member {0} is found from {1} and {2}, use the one in {1} and ignore the one from {2}", s1.Name, existingMetadata1.Source.Path, s1.Source.Path);
                                }
                                else
                                {
                                    allMembers.Add(s1.Name, s1);
                                }
                            });
                        });
                    }
                }
            }

            return allMembers;
        }

        private static ParseResult TryParseYamlMetadataFile(string metadataFileName, out MetadataItem projectMetadata)
        {
            projectMetadata = null;
            try
            {
                using (StreamReader reader = new StreamReader(metadataFileName))
                {
                    projectMetadata = YamlUtility.Deserialize<MetadataItem>(reader);
                    return new ParseResult(ResultLevel.Success);
                }
            }
            catch (Exception e)
            {
                return new ParseResult(ResultLevel.Error, e.Message);
            }
        }

        private static Dictionary<string, ReferenceItem> MergeYamlProjectReferences(List<MetadataItem> projectMetadataList)
        {
            if (projectMetadataList == null || projectMetadataList.Count == 0)
            {
                return null;
            }

            var result = new Dictionary<string, ReferenceItem>();

            foreach (var project in projectMetadataList)
            {
                if (project.References != null)
                {
                    foreach (var pair in project.References)
                    {
                        if (!result.ContainsKey(pair.Key))
                        {
                            result[pair.Key] = pair.Value;
                        }
                    }
                }
            }

            return result;
        }

        private static Compilation CreateCompilationFromCsharpCode(string code)
        {
            try
            {
                var tree = CS.SyntaxFactory.ParseSyntaxTree(code);
                var compilation = CS.CSharpCompilation.Create(
                    "cs.temp.dll",
                    options: new CS.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                    syntaxTrees: new[] { tree },
                    references: new[] { MetadataReference.CreateFromAssembly(typeof(object).Assembly) });
                return compilation;
            }
            catch (Exception e)
            {
                ParseResult.WriteToConsole(ResultLevel.Warning, "Error generating compilation for C# code {0}: {1}. Ignored.", GetAbbreviateString(code), e.Message);
                return null;
            }
        }

        private static Compilation CreateCompilationFromVBCode(string code)
        {
            try
            {
                var tree = VB.SyntaxFactory.ParseSyntaxTree(code);
                var compilation = VB.VisualBasicCompilation.Create(
                    "vb.temp.dll",
                    options: new VB.VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                    syntaxTrees: new[] { tree },
                    references: new[] { MetadataReference.CreateFromAssembly(typeof(object).Assembly) });
                return compilation;
            }
            catch (Exception e)
            {
                ParseResult.WriteToConsole(ResultLevel.Warning, "Error generating compilation for VB code {0}: {1}. Ignored.", GetAbbreviateString(code), e.Message);
                return null;
            }
        }

        private static string GetAbbreviateString(string input, int length = 20)
        {
            if (string.IsNullOrEmpty(input) || input.Length <= 20) return input;
            return input.Substring(0, length) + "...";
        }

        private static async Task<Solution> GetSolutionAsync(string path)
        {
            try
            {
                return await Workspace.Value.OpenSolutionAsync(path);
            }
            catch (Exception e)
            {
                ParseResult.WriteToConsole(ResultLevel.Warning, "Error opening solution {0}: {1}. Ignored.", path, e.Message);
                return null;
            }
        }

        private static async Task<Project> GetProjectAsync(string path)
        {
            try
            {
                string name = Path.GetFileName(path);
                if (name.Equals("project.json", StringComparison.OrdinalIgnoreCase))
                {
                    var workspace = new ProjectJsonWorkspace(path);
                    return workspace.CurrentSolution.Projects.FirstOrDefault(p => p.FilePath == Path.GetFullPath(path));
                }

                return await Workspace.Value.OpenProjectAsync(path);
            }
            catch (Exception e)
            {
                ParseResult.WriteToConsole(ResultLevel.Warning, "Error opening project {0}: {1}. Ignored.", path, e.Message);
                return null;
            }
        }

        #endregion
    }
}
