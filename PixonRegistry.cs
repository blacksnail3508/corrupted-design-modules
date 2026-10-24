using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

// Self-contained on purpose: this single file is the whole Pixon installer, so dropping it into any project is enough to browse, install, update and remove Pixon modules.
namespace Pixon.Registry
{
    #region MODELS
    /// <summary>A third-party asset a module needs that cannot be shipped with it (Odin, DOTween, Spine...).</summary>
    [Serializable]
    public class RegistryRequirement
    {
        public string name;
        public string probe;
        public string hint;
    }

    /// <summary>A Unity package a module needs; version is a version number or a git URL.</summary>
    [Serializable]
    public class RegistryPackage
    {
        public string id;
        public string version;
    }

    /// <summary>One installable module as listed in catalog.json.</summary>
    [Serializable]
    public class RegistryModule
    {
        public string id;
        public string name;
        public string version;
        public string description;
        public string[] dependencies = Array.Empty<string>();
        // Modules installed with this one although they are not its dependencies, for code that refers back to it (a cycle a dependency list cannot hold).
        public string[] companions = Array.Empty<string>();
        public RegistryRequirement[] requires = Array.Empty<RegistryRequirement>();
        public RegistryPackage[] packages = Array.Empty<RegistryPackage>();
        public string installPath;
        // Further project folders the module owns (art, audio...), for modules that are more than one folder.
        public string[] extraPaths = Array.Empty<string>();
        public string zip;
        public string sha256;
        public long size;
    }

    /// <summary>The catalog.json at the root of the module repository.</summary>
    [Serializable]
    public class RegistryCatalog
    {
        public int schema = 1;
        public RegistryModule[] modules = Array.Empty<RegistryModule>();
    }

    [Serializable]
    public class InstalledFile
    {
        public string path;
        public string sha256;
    }

    [Serializable]
    public class InstalledModule
    {
        public string id;
        public string version;
        public string installPath;
        public string[] extraPaths = Array.Empty<string>();
        public string[] dependencies = Array.Empty<string>();
        public string[] companions = Array.Empty<string>();
        public InstalledFile[] files = Array.Empty<InstalledFile>();
    }

    /// <summary>What this project has installed, with a hash per file so local edits can be told from the shipped files.</summary>
    [Serializable]
    public class InstallLock
    {
        public InstalledModule[] modules = Array.Empty<InstalledModule>();

        public InstalledModule Find(string id) => modules.FirstOrDefault(m => m.id == id);
    }

    public static class ModulePaths
    {
        /// <summary>Every project folder of a module, the main one first.</summary>
        public static string[] All(RegistryModule module) => new[] { module.installPath }.Concat(module.extraPaths ?? Array.Empty<string>()).ToArray();

        public static string[] All(InstalledModule module) => new[] { module.installPath }.Concat(module.extraPaths ?? Array.Empty<string>()).ToArray();
    }

    public enum ModuleState
    {
        NotInstalled,
        Installed,
        UpdateAvailable,
        Modified,
        Unmanaged,
    }
    #endregion

    /// <summary>Install, update and uninstall logic on plain files; takes the project root so tests can run it on a temp folder.</summary>
    public static class PixonInstaller
    {
        public const string LockFile = "ProjectSettings/PixonModules.lock.json";
        public const string TrashFolder = "Library/PixonTrash";

        #region LOCK
        public static InstallLock ReadLock(string root)
        {
            var path = Path.Combine(root, LockFile);
            if (!File.Exists(path)) return new InstallLock();

            return JsonUtility.FromJson<InstallLock>(File.ReadAllText(path)) ?? new InstallLock();
        }

        public static void WriteLock(string root, InstallLock installLock)
        {
            var path = Path.Combine(root, LockFile);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(installLock, true));
        }

        private static void Record(string root, InstalledModule module)
        {
            var installLock = ReadLock(root);
            installLock.modules = installLock.modules.Where(m => m.id != module.id).Append(module).OrderBy(m => m.id, StringComparer.Ordinal).ToArray();
            WriteLock(root, installLock);
        }
        #endregion

        #region QUERIES
        public static string Sha256(byte[] data)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", string.Empty).ToLowerInvariant();
        }

        /// <summary>Compares dotted version numbers; a part that is not a number counts as 0.</summary>
        public static int CompareVersions(string a, string b)
        {
            var left = (a ?? "0").Split('.');
            var right = (b ?? "0").Split('.');
            for (int i = 0; i < Math.Max(left.Length, right.Length); i++)
            {
                int x = i < left.Length && int.TryParse(left[i], out var px) ? px : 0;
                int y = i < right.Length && int.TryParse(right[i], out var py) ? py : 0;
                if (x != y) return x.CompareTo(y);
            }
            return 0;
        }

        public static ModuleState GetState(string root, RegistryModule module, InstallLock installLock)
        {
            var installed = installLock.Find(module.id);
            if (installed == null) return ModulePaths.All(module).Any(p => Directory.Exists(Path.Combine(root, p))) ? ModuleState.Unmanaged : ModuleState.NotInstalled;
            if (ModifiedFiles(root, installed).Count > 0) return ModuleState.Modified;

            return CompareVersions(module.version, installed.version) > 0 ? ModuleState.UpdateAvailable : ModuleState.Installed;
        }

        /// <summary>Files of an installed module that were edited or deleted since the install.</summary>
        public static List<string> ModifiedFiles(string root, InstalledModule module)
        {
            var result = new List<string>();
            foreach (var file in module.files)
            {
                var path = Path.Combine(root, file.path);
                if (!File.Exists(path) || Sha256(File.ReadAllBytes(path)) != file.sha256) result.Add(file.path);
            }
            return result;
        }

        /// <summary>The wanted modules plus everything they depend on, dependencies first; unknown ids go to <paramref name="missing"/>.</summary>
        public static List<RegistryModule> Closure(RegistryCatalog catalog, IEnumerable<string> ids, out List<string> missing)
        {
            var byId = catalog.modules.ToDictionary(m => m.id);
            var ordered = new List<RegistryModule>();
            var missingIds = new List<string>();
            var visiting = new HashSet<string>();

            // Cycles are fine for a file copy: the whole set is written before anything compiles.
            void Visit(string id)
            {
                if (!visiting.Add(id)) return;
                if (!byId.TryGetValue(id, out var module))
                {
                    missingIds.Add(id);
                    return;
                }
                foreach (var dependency in module.dependencies.Concat(module.companions ?? Array.Empty<string>())) Visit(dependency);
                ordered.Add(module);
            }

            foreach (var id in ids) Visit(id);
            missing = missingIds;
            return ordered;
        }

        public static bool TypeExists(string fullName) =>
            AppDomain.CurrentDomain.GetAssemblies().Any(a =>
            {
                try { return a.GetType(fullName, false) != null; }
                catch { return false; }
            });

        public static List<RegistryRequirement> MissingRequirements(RegistryModule module, Func<string, bool> typeExists) =>
            module.requires.Where(r => !string.IsNullOrEmpty(r.probe) && !typeExists(r.probe)).ToList();
        #endregion

        #region INSTALL
        /// <summary>Writes the module files from its zip and records them in the lock; an existing folder that is not in the lock is moved to the trash only when <paramref name="replaceUnmanaged"/> is set.</summary>
        public static InstalledModule Install(string root, RegistryModule module, byte[] zip, bool replaceUnmanaged = false)
        {
            if (!string.IsNullOrEmpty(module.sha256) && Sha256(zip) != module.sha256)
                throw new InvalidOperationException($"{module.id}: the downloaded file does not match the catalog hash.");

            var installLock = ReadLock(root);
            var previous = installLock.Find(module.id);
            var paths = ModulePaths.All(module);

            if (previous != null) Remove(root, previous, "update");
            else
            {
                var existing = paths.Where(p => Directory.Exists(Path.Combine(root, p))).ToList();
                if (existing.Count > 0 && !replaceUnmanaged) throw new InvalidOperationException($"{module.id}: {existing[0]} already exists and was not installed by the registry.");

                var label = TrashLabel(module.id, "replaced");
                foreach (var path in existing) MoveToTrash(root, path, label);
            }

            var files = new List<InstalledFile>();

            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;

                var name = entry.FullName.Replace('\\', '/');
                var inModule = paths.Any(p => name.StartsWith(p + "/", StringComparison.Ordinal) || name == p + ".meta");
                if (!inModule || name.Split('/').Contains("..") || Path.IsPathRooted(name))
                    throw new InvalidOperationException($"{module.id}: unsafe entry '{entry.FullName}' in the module zip.");

                var destination = Path.GetFullPath(Path.Combine(root, name));
                Directory.CreateDirectory(Path.GetDirectoryName(destination));

                using var input = entry.Open();
                using var buffer = new MemoryStream();
                input.CopyTo(buffer);
                var bytes = buffer.ToArray();
                File.WriteAllBytes(destination, bytes);
                files.Add(new InstalledFile { path = Relative(root, destination), sha256 = Sha256(bytes) });
            }

            var installed = new InstalledModule
            {
                id = module.id,
                version = module.version,
                installPath = module.installPath,
                extraPaths = module.extraPaths,
                dependencies = module.dependencies,
                companions = module.companions,
                files = files.OrderBy(f => f.path, StringComparer.Ordinal).ToArray(),
            };
            Record(root, installed);
            return installed;
        }

        /// <summary>Takes over a folder that already holds the module (copied by hand) so the registry can manage it from now on.</summary>
        public static InstalledModule Adopt(string root, RegistryModule module)
        {
            var paths = new List<string>();
            foreach (var folder in ModulePaths.All(module).Select(p => Path.Combine(root, p)).Where(Directory.Exists))
            {
                paths.AddRange(Directory.GetFiles(folder, "*", SearchOption.AllDirectories));
                if (File.Exists(folder + ".meta")) paths.Add(folder + ".meta");
            }

            var installed = new InstalledModule
            {
                id = module.id,
                version = module.version,
                installPath = module.installPath,
                extraPaths = module.extraPaths,
                dependencies = module.dependencies,
                companions = module.companions,
                files = paths.Select(p => new InstalledFile { path = Relative(root, p), sha256 = Sha256(File.ReadAllBytes(p)) }).OrderBy(f => f.path, StringComparer.Ordinal).ToArray(),
            };
            Record(root, installed);
            return installed;
        }

        /// <summary>Adds the packages that are not in Packages/manifest.json yet and returns their ids; existing entries are never changed.</summary>
        public static List<string> EnsurePackages(string root, IEnumerable<RegistryPackage> packages)
        {
            var added = new List<string>();
            var path = Path.Combine(root, "Packages", "manifest.json");
            if (!File.Exists(path)) return added;

            var text = File.ReadAllText(path);
            foreach (var package in packages.GroupBy(p => p.id).Select(g => g.First()))
            {
                if (Regex.IsMatch(text, "\"" + Regex.Escape(package.id) + "\"\\s*:")) continue;

                var open = Regex.Match(text, "\"dependencies\"\\s*:\\s*\\{");
                if (!open.Success) continue;

                var rest = text.Substring(open.Index + open.Length);
                var comma = rest.TrimStart().StartsWith("}") ? string.Empty : ",";
                text = text.Insert(open.Index + open.Length, $"\n    \"{package.id}\": \"{package.version}\"{comma}");
                added.Add(package.id);
            }
            if (added.Count > 0) File.WriteAllText(path, text);
            return added;
        }
        #endregion

        #region UNINSTALL
        /// <summary>Installed modules outside <paramref name="removing"/> that depend on one of them.</summary>
        public static List<string> Dependents(InstallLock installLock, IReadOnlyCollection<string> removing) =>
            installLock.modules.Where(m => !removing.Contains(m.id) && m.dependencies.Concat(m.companions ?? Array.Empty<string>()).Any(removing.Contains)).Select(m => m.id).ToList();

        /// <summary>Moves a module's files to Library/PixonTrash and drops it from the lock; returns the files left behind because they were not part of the install.</summary>
        public static List<string> Uninstall(string root, string id)
        {
            var installLock = ReadLock(root);
            var module = installLock.Find(id) ?? throw new InvalidOperationException($"{id} is not installed by the registry.");
            var dependents = Dependents(installLock, new[] { id });
            if (dependents.Count > 0) throw new InvalidOperationException($"{id} is needed by {string.Join(", ", dependents)}.");

            var leftover = Remove(root, module, "uninstall");
            installLock.modules = installLock.modules.Where(m => m.id != id).ToArray();
            WriteLock(root, installLock);
            return leftover;
        }

        private static List<string> Remove(string root, InstalledModule module, string reason)
        {
            var label = TrashLabel(module.id, reason);
            foreach (var file in module.files)
                if (File.Exists(Path.Combine(root, file.path))) MoveToTrash(root, file.path, label);

            var leftover = new List<string>();
            foreach (var folder in ModulePaths.All(module).Select(p => Path.Combine(root, p)).Where(Directory.Exists))
            {
                DeleteEmptyDirectories(folder);
                if (Directory.Exists(folder)) leftover.AddRange(Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(f => Relative(root, f)));
            }
            return leftover;
        }

        // Unique per operation, so two updates in the same second never write to the same trash folder.
        private static string TrashLabel(string id, string reason) => $"{id}_{reason}_{DateTime.UtcNow:yyyyMMdd-HHmmss}_{Guid.NewGuid().ToString("N").Substring(0, 6)}";

        private static void MoveToTrash(string root, string relativePath, string label)
        {
            var source = Path.Combine(root, relativePath);
            var destination = Path.Combine(root, TrashFolder, label, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));

            if (Directory.Exists(source)) Directory.Move(source, destination);
            else File.Move(source, destination);
        }

        private static void DeleteEmptyDirectories(string folder)
        {
            foreach (var child in Directory.GetDirectories(folder)) DeleteEmptyDirectories(child);
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }

        private static string Relative(string root, string path) =>
            Path.GetFullPath(path).Substring(Path.GetFullPath(root).TrimEnd('\\', '/').Length + 1).Replace('\\', '/');
        #endregion
    }

    /// <summary>Where the catalog and module zips are downloaded from: an http(s) URL (optionally with a GitHub token) or a local file.</summary>
    public static class PixonCatalogSource
    {
        public const string DefaultUrl = "https://raw.githubusercontent.com/blacksnail3508/pixon-modules/main/catalog.json";

        private const string UrlKey = "Pixon.Registry.Url";
        private const string TokenKey = "Pixon.Registry.Token";

        public static string Url
        {
            get => EditorPrefs.GetString(UrlKey, DefaultUrl);
            set => EditorPrefs.SetString(UrlKey, value);
        }

        public static string Token
        {
            get => EditorPrefs.GetString(TokenKey, string.Empty);
            set => EditorPrefs.SetString(TokenKey, value);
        }

        public static RegistryCatalog LoadCatalog(string url, string token)
        {
            var catalog = JsonUtility.FromJson<RegistryCatalog>(Encoding.UTF8.GetString(Get(url, token)));
            if (catalog == null || catalog.modules == null) throw new InvalidOperationException("The catalog is empty or not valid JSON.");
            return catalog;
        }

        /// <summary>Downloads a path relative to the catalog's folder, such as a module zip.</summary>
        public static byte[] GetRelative(string catalogUrl, string token, string relative) => Get(Resolve(catalogUrl, relative), token);

        public static string Resolve(string catalogUrl, string relative) => catalogUrl.Substring(0, catalogUrl.LastIndexOfAny(new[] { '/', '\\' }) + 1) + relative;

        // Blocks the editor for the duration of the download, which is a few megabytes at most.
        public static byte[] Get(string url, string token)
        {
            if (url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return File.ReadAllBytes(new Uri(url).LocalPath);
            if (!url.Contains("://")) return File.ReadAllBytes(url);

            using var request = UnityWebRequest.Get(url);
            if (!string.IsNullOrEmpty(token)) request.SetRequestHeader("Authorization", "token " + token);
            request.timeout = 60;

            var operation = request.SendWebRequest();
            while (!operation.isDone) System.Threading.Thread.Sleep(20);

            if (request.result != UnityWebRequest.Result.Success) throw new InvalidOperationException($"{url}: {request.error} ({request.responseCode})");
            return request.downloadHandler.data;
        }
    }

    /// <summary>Runs a full install / update / uninstall request: resolves dependencies, checks requirements, downloads, writes and refreshes.</summary>
    public static class PixonRegistryController
    {
        private static readonly string Root = Path.GetDirectoryName(Application.dataPath);

        public const string HubId = "Hub";

        public static string ProjectRoot => Root;

        [InitializeOnLoadMethod]
        private static void OnLoad() => EditorApplication.delayCall += RemoveLooseCopy;

        /// <summary>Everything an install would change, for the confirmation dialog; throws when it cannot proceed.</summary>
        public static List<RegistryModule> PlanInstall(RegistryCatalog catalog, IEnumerable<string> ids, out List<RegistryPackage> packages)
        {
            // The Hub carries the registry as an asmdef, so it keeps working when the project does not compile; it always comes along.
            var wanted = ids.ToList();
            if (!wanted.Contains(HubId) && catalog.modules.Any(m => m.id == HubId)) wanted.Add(HubId);

            var closure = PixonInstaller.Closure(catalog, wanted, out var missing);
            if (missing.Count > 0) throw new InvalidOperationException("Not in the catalog: " + string.Join(", ", missing));

            var absent = closure.SelectMany(m => PixonInstaller.MissingRequirements(m, PixonInstaller.TypeExists).Select(r => $"{m.id} needs {r.name}: {r.hint}")).ToList();
            if (absent.Count > 0) throw new InvalidOperationException("Install these first:\n" + string.Join("\n", absent));

            packages = closure.SelectMany(m => m.packages).ToList();
            return closure;
        }

        /// <summary>Installs (or updates to the catalog version) the modules and their dependencies; modules already current are skipped.</summary>
        public static List<string> Install(string catalogUrl, string token, RegistryCatalog catalog, IEnumerable<string> ids, bool replaceUnmanaged = false)
        {
            var closure = PlanInstall(catalog, ids, out var packages);
            var installLock = PixonInstaller.ReadLock(Root);
            var done = new List<string>();

            foreach (var module in closure)
            {
                var state = PixonInstaller.GetState(Root, module, installLock);
                if (state == ModuleState.Installed) continue;

                var zip = PixonCatalogSource.GetRelative(catalogUrl, token, module.zip);
                PixonInstaller.Install(Root, module, zip, replaceUnmanaged);
                done.Add($"{module.id} {module.version}");
            }

            PixonInstaller.EnsurePackages(Root, packages);
            AssetDatabase.Refresh();

            return done;
        }

        public static List<string> Uninstall(string id)
        {
            var leftover = PixonInstaller.Uninstall(Root, id);
            AssetDatabase.Refresh();
            return leftover;
        }

        // Once the Hub is installed it carries its own copy of this file, so the loose one is removed to keep a single menu entry.
        private static void RemoveLooseCopy()
        {
            var own = AssetDatabase.FindAssets("PixonRegistry t:MonoScript").Select(AssetDatabase.GUIDToAssetPath).Where(p => p.EndsWith("/PixonRegistry.cs")).ToList();
            var hub = PixonInstaller.ReadLock(Root).Find(HubId);
            if (hub == null || own.Count < 2) return;

            foreach (var path in own.Where(p => !p.StartsWith(hub.installPath + "/", StringComparison.Ordinal))) AssetDatabase.DeleteAsset(path);
        }
    }

    /// <summary>Batch mode entry point: <c>-executeMethod Pixon.Registry.PixonRegistryCli.Run -pixonCatalog URL -pixonAction status|install|uninstall [-pixonModule ID]</c>.</summary>
    public static class PixonRegistryCli
    {
        public static void Run()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                string Arg(string name)
                {
                    int i = Array.IndexOf(args, name);
                    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
                }

                var url = Arg("-pixonCatalog") ?? PixonCatalogSource.Url;
                var token = Arg("-pixonToken") ?? PixonCatalogSource.Token;
                var catalog = PixonCatalogSource.LoadCatalog(url, token);
                var id = Arg("-pixonModule");

                switch (Arg("-pixonAction") ?? "status")
                {
                    case "install":
                        foreach (var line in PixonRegistryController.Install(url, token, catalog, id.Split(','))) Debug.Log("[PixonRegistry] installed " + line);
                        break;
                    case "uninstall":
                        Debug.Log($"[PixonRegistry] uninstalled {id}, {PixonRegistryController.Uninstall(id).Count} files left behind");
                        break;
                    default:
                        var installLock = PixonInstaller.ReadLock(PixonRegistryController.ProjectRoot);
                        foreach (var module in catalog.modules) Debug.Log($"[PixonRegistry] {module.id} {module.version} {PixonInstaller.GetState(PixonRegistryController.ProjectRoot, module, installLock)}");
                        break;
                }
                if (Application.isBatchMode) EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError("[PixonRegistry] " + e.Message);
                if (Application.isBatchMode) EditorApplication.Exit(1);
            }
        }
    }

    /// <summary>The Pixon/Registry window: every module in the catalog with its state and Install / Update / Uninstall buttons.</summary>
    public class PixonRegistryWindow : EditorWindow
    {
        private RegistryCatalog catalog;
        private InstallLock installLock = new();
        private string url;
        private string token;
        private string status = "Press Refresh to load the catalog.";
        private string search = string.Empty;
        private Vector2 scroll;

        [MenuItem("Pixon/Registry")]
        public static void Open() => GetWindow<PixonRegistryWindow>("Pixon Registry").Show();

        private void OnEnable()
        {
            url = PixonCatalogSource.Url;
            token = PixonCatalogSource.Token;
            installLock = PixonInstaller.ReadLock(PixonRegistryController.ProjectRoot);
        }

        private void OnGUI()
        {
            DrawSource();
            if (catalog == null)
            {
                EditorGUILayout.HelpBox(status, MessageType.Info);
                return;
            }

            search = EditorGUILayout.TextField("Search", search);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var module in catalog.modules.Where(Matches)) DrawModule(module);
            EditorGUILayout.EndScrollView();
            EditorGUILayout.LabelField(status, EditorStyles.miniLabel);
        }

        private bool Matches(RegistryModule module) =>
            string.IsNullOrEmpty(search) || module.id.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 || (module.description ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;

        private void DrawSource()
        {
            url = EditorGUILayout.TextField("Catalog URL", url);
            token = EditorGUILayout.PasswordField("GitHub token (private repo)", token);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Refresh")) Run(Refresh);
                using (new EditorGUI.DisabledScope(catalog == null))
                    if (GUILayout.Button("Install all missing")) Run(() => InstallMany(catalog.modules.Where(m => PixonInstaller.GetState(PixonRegistryController.ProjectRoot, m, installLock) == ModuleState.NotInstalled).Select(m => m.id)));
            }
        }

        private void DrawModule(RegistryModule module)
        {
            var root = PixonRegistryController.ProjectRoot;
            var state = PixonInstaller.GetState(root, module, installLock);
            var installed = installLock.Find(module.id);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope())
                {
                    EditorGUILayout.LabelField($"{module.name}  v{module.version}", EditorStyles.boldLabel);
                    EditorGUILayout.LabelField(StateText(state, installed), EditorStyles.miniLabel);
                    if (!string.IsNullOrEmpty(module.description)) EditorGUILayout.LabelField(module.description, EditorStyles.wordWrappedMiniLabel);
                    if (module.dependencies.Length > 0) EditorGUILayout.LabelField("Needs: " + string.Join(", ", module.dependencies), EditorStyles.miniLabel);
                    foreach (var requirement in PixonInstaller.MissingRequirements(module, PixonInstaller.TypeExists))
                        EditorGUILayout.LabelField($"Missing {requirement.name}: {requirement.hint}", EditorStyles.miniBoldLabel);
                }

                using (new EditorGUILayout.VerticalScope(GUILayout.Width(90)))
                {
                    if (state == ModuleState.NotInstalled && GUILayout.Button("Install")) Run(() => InstallMany(new[] { module.id }));
                    if (state == ModuleState.UpdateAvailable && GUILayout.Button("Update")) Run(() => InstallMany(new[] { module.id }));
                    if (state == ModuleState.Modified && GUILayout.Button("Reinstall")) Run(() => InstallMany(new[] { module.id }, "Local edits in this module will be moved to Library/PixonTrash."));
                    if (state == ModuleState.Unmanaged && GUILayout.Button("Adopt")) Run(() => { PixonInstaller.Adopt(root, module); installLock = PixonInstaller.ReadLock(root); status = $"Adopted {module.id}."; });
                    if (state == ModuleState.Unmanaged && GUILayout.Button("Replace")) Run(() => InstallMany(new[] { module.id }, "The existing folder will be moved to Library/PixonTrash.", true));
                    if (installed != null && GUILayout.Button("Uninstall")) Run(() => UninstallOne(module.id));
                }
            }
        }

        private static string StateText(ModuleState state, InstalledModule installed) => state switch
        {
            ModuleState.NotInstalled => "Not installed",
            ModuleState.Installed => $"Installed v{installed.version}",
            ModuleState.UpdateAvailable => $"Installed v{installed.version}, update available",
            ModuleState.Modified => $"Installed v{installed.version}, edited locally",
            ModuleState.Unmanaged => "Folder exists but was not installed by the registry",
            _ => string.Empty,
        };

        private void Refresh()
        {
            PixonCatalogSource.Url = url;
            PixonCatalogSource.Token = token;
            catalog = PixonCatalogSource.LoadCatalog(url, token);
            installLock = PixonInstaller.ReadLock(PixonRegistryController.ProjectRoot);
            status = $"{catalog.modules.Length} modules in the catalog.";
        }

        private void InstallMany(IEnumerable<string> ids, string warning = null, bool replaceUnmanaged = false)
        {
            var list = ids.ToList();
            var plan = PixonRegistryController.PlanInstall(catalog, list, out var packages);
            var toInstall = plan.Where(m => PixonInstaller.GetState(PixonRegistryController.ProjectRoot, m, installLock) != ModuleState.Installed).ToList();
            if (toInstall.Count == 0) return;

            var message = string.Join("\n", toInstall.Select(m => $"{m.id} {m.version}"))
                + (packages.Count > 0 ? "\n\nPackages added if missing:\n" + string.Join("\n", packages.Select(p => p.id).Distinct()) : string.Empty)
                + (warning != null ? "\n\n" + warning : string.Empty);
            if (!EditorUtility.DisplayDialog("Install", message, "Install", "Cancel")) return;

            var done = PixonRegistryController.Install(url, token, catalog, list, replaceUnmanaged);
            installLock = PixonInstaller.ReadLock(PixonRegistryController.ProjectRoot);
            status = "Installed: " + string.Join(", ", done);
        }

        private void UninstallOne(string id)
        {
            var dependents = PixonInstaller.Dependents(installLock, new[] { id });
            if (dependents.Count > 0)
            {
                EditorUtility.DisplayDialog("Uninstall", $"{id} is needed by {string.Join(", ", dependents)}. Uninstall those first.", "OK");
                return;
            }
            if (!EditorUtility.DisplayDialog("Uninstall", $"Move {id} to Library/PixonTrash? Code that uses it will stop compiling.", "Uninstall", "Cancel")) return;

            var leftover = PixonRegistryController.Uninstall(id);
            installLock = PixonInstaller.ReadLock(PixonRegistryController.ProjectRoot);
            status = leftover.Count == 0 ? $"Uninstalled {id}." : $"Uninstalled {id}; {leftover.Count} files that were not part of it stay in the folder.";
        }

        // One place to turn an installer error into a message instead of a stack trace in the console.
        private void Run(Action action)
        {
            try { action(); }
            catch (Exception e)
            {
                status = e.Message;
                EditorUtility.DisplayDialog("Pixon Registry", e.Message, "OK");
            }
            Repaint();
        }
    }
}
