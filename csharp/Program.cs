using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Plugin;
using Newtonsoft.Json;

namespace HeliosphereInstaller;

public class Installer {
    const string SeaOfStarsRepo = "https://raw.githubusercontent.com/Ottermandias/SeaOfStars/main/repo.json";
    const string SeaOfStarsStartsWith = "https://raw.githubusercontent.com/Ottermandias/SeaOfStars/";
    const string PenumbraInternalName = "Penumbra";
    const string HeliosphereInternalName = "heliosphere-plugin";

    private HttpClient Client { get; }
    private string DalamudFolder { get; }

    private Type ConfigurationType { get; }
    private Dictionary<string, PropertyInfo> ConfigurationProps { get; }

    private Type LocalManifestType { get; }
    private Dictionary<string, PropertyInfo> LocalManifestProps { get; }

    private Type ProfileModelType { get; }
    private Dictionary<string, PropertyInfo> ProfileModelProps { get; }

    private Type ProfilePluginType { get; }
    private Dictionary<string, PropertyInfo> ProfilePluginProps { get; }

    private Type ThirdRepoSettingsType { get; }
    private Dictionary<string, PropertyInfo> ThirdRepoSettingsProps { get; }

    private object? Config { get; set; }
    private bool ConfigModified { get; set; }

    public static readonly Installer Instance = new();

    private static unsafe delegate*<char*, int, byte*> CopyToCString;

    [UnmanagedCallersOnly]
    public static unsafe void SetCopyToCStringFunctionPtr(delegate*<char*, int, byte*> copyToCString) => CopyToCString = copyToCString;

    [UnmanagedCallersOnly]
    public static unsafe byte* MakeRepo(byte* urlRaw, int urlLen) {
        var self = Installer.Instance;

        var url = Marshal.PtrToStringUTF8((nint) urlRaw, urlLen);

        var repo = Activator.CreateInstance(self.ThirdRepoSettingsType);
        self.ThirdRepoSettingsProps["Name"].SetValue(repo, null);
        self.ThirdRepoSettingsProps["Url"].SetValue(repo, url);
        self.ThirdRepoSettingsProps["IsEnabled"].SetValue(repo, true);

        var json = JsonConvert.SerializeObject(repo, self.ThirdRepoSettingsType, Installer.ConfigJsonSettings);
        fixed (char* ptr = json) {
            return CopyToCString(ptr, json.Length);
        }
    }

    [UnmanagedCallersOnly]
    public static unsafe byte* MakePlugin(
        byte* internalNameRaw,
        int internalNameLen,
        byte* workingIdRaw,
        int workingIdLen
    ) {
        var self = Installer.Instance;

        var internalName = Marshal.PtrToStringUTF8((nint) internalNameRaw, internalNameLen);
        var workingIdStr = Marshal.PtrToStringUTF8((nint) workingIdRaw, workingIdLen)!;
        var workingId = Guid.Parse(workingIdStr);

        var plugin = Activator.CreateInstance(self.ProfilePluginType);
        self.ProfilePluginProps["InternalName"].SetValue(plugin, internalName);
        self.ProfilePluginProps["WorkingPluginId"].SetValue(plugin, workingId);
        self.ProfilePluginProps["IsEnabled"].SetValue(plugin, true);

        var json = JsonConvert.SerializeObject(plugin, self.ProfilePluginType, Installer.ConfigJsonSettings);
        fixed (char* ptr = json) {
            return CopyToCString(ptr, json.Length);
        }
    }

    [UnmanagedCallersOnly]
    public static unsafe void FillOutManifest(
        byte* manifestJsonRaw,
        int manifestLen,
        byte* workingIdRaw,
        int workingIdLen,
        byte* repoUrlRaw,
        int repoUrlLen,
        byte** outManifestJson,
        byte** outVersion
    ) {
        var self = Installer.Instance;

        var manifestJson = Marshal.PtrToStringUTF8((nint) manifestJsonRaw, manifestLen)!;
        var workingId = Marshal.PtrToStringUTF8((nint) workingIdRaw, workingIdLen);
        var repoUrl = Marshal.PtrToStringUTF8((nint) repoUrlRaw, repoUrlLen);

        var manifest = JsonConvert.DeserializeObject(manifestJson, self.LocalManifestType);
        self.LocalManifestProps["WorkingPluginId"].SetValue(manifest, workingId);
        self.LocalManifestProps["InstalledFromUrl"].SetValue(manifest, repoUrl);

        var json = JsonConvert.SerializeObject(manifest, self.LocalManifestType, new JsonSerializerSettings());
        fixed (char* ptr = json) {
            *outManifestJson = CopyToCString(ptr, json.Length);
        }

        var version = (Version?) self.LocalManifestProps["AssemblyVersion"].GetValue(manifest);
        if (version != null) {
            var versionStr = version.ToString();
            fixed (char* ptr = versionStr) {
                *outVersion = CopyToCString(ptr, versionStr.Length);
            }
        }
    }

    internal readonly static JsonSerializerSettings ConfigJsonSettings = new() {
        TypeNameHandling = TypeNameHandling.All,
        TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
        Formatting = Formatting.Indented,
    };

    private Installer() {
        this.Client = new HttpClient();
        this.DalamudFolder = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher");

        var assembly = typeof(IDalamudPlugin).Assembly;

        var (localManifestType, localManifestProps) = GetTypeAndProperties(
            assembly,
            "Dalamud.Plugin.Internal.Types.Manifest.LocalPluginManifest",
            [
                "WorkingPluginId",
                "InstalledFromUrl",
                "AssemblyVersion",
            ]
        );
        this.LocalManifestType = localManifestType;
        this.LocalManifestProps = localManifestProps;

        var (configurationType, configurationProps) = GetTypeAndProperties(
            assembly,
            "Dalamud.Configuration.Internal.DalamudConfiguration",
            [
                "ThirdRepoList",
                "DefaultProfile",
            ]
        );

        this.ConfigurationType = configurationType;
        this.ConfigurationProps = configurationProps;

        var (profileModelType, profileModelProps) = GetTypeAndProperties(
            assembly,
            "Dalamud.Plugin.Internal.Profiles.ProfileModelV1",
            [
                "Plugins",
            ]
        );

        this.ProfileModelType = profileModelType;
        this.ProfileModelProps = profileModelProps;

        var profilePluginType = this.ProfileModelType.GetNestedType("ProfileModelV1Plugin");
        if (profilePluginType == null) {
            throw new Exception("missing type ProfileModelV1Plugin");
        }

        var pluginTypePropertiesWanted = new HashSet<string> {
            "InternalName",
            "WorkingPluginId",
            "IsEnabled",
        };
        var profilePluginProps = GetProperties(
            profilePluginType,
            [.. pluginTypePropertiesWanted]
        );

        var pluginTypePropertiesActual = profilePluginType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(prop => prop.CanWrite)
            .Select(prop => prop.Name)
            .ToHashSet();

        if (!pluginTypePropertiesActual.SetEquals(pluginTypePropertiesWanted)) {
            throw new Exception("unexpected properties in ProfileModelV1Plugin");
        }

        this.ProfilePluginType = profilePluginType;
        this.ProfilePluginProps = profilePluginProps;

        var thirdRepoSettingsPropsWanted = new HashSet<string> {
            "Url",
            "IsEnabled",
            "Name",
        };
        var (thirdRepoSettingsType, thirdRepoSettingsProps) = GetTypeAndProperties(
            assembly,
            "Dalamud.Configuration.ThirdPartyRepoSettings",
            [.. thirdRepoSettingsPropsWanted]
        );

        var thirdRepoSettingPropsActual = thirdRepoSettingsType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(prop => prop.CanWrite)
            .Select(prop => prop.Name)
            .ToHashSet();

        if (!thirdRepoSettingPropsActual.SetEquals(thirdRepoSettingsPropsWanted)) {
            throw new Exception("unexpected properties in ThirdPartyRepoSettings");
        }

        this.ThirdRepoSettingsType = thirdRepoSettingsType;
        this.ThirdRepoSettingsProps = thirdRepoSettingsProps;
    }

    public static async Task Main() {
        var installer = new Installer();
        await installer.Install();
    }

    private static (Type, Dictionary<string, PropertyInfo>) GetTypeAndProperties(Assembly assembly, string typeName, string[] propertyNames) {
        var type = assembly.GetType(typeName);
        if (type == null) {
            throw new Exception($"type {typeName} not found");
        }

        var props = GetProperties(type, propertyNames);

        return (type, props)!;
    }

    private static Dictionary<string, PropertyInfo> GetProperties(Type type, string[] propertyNames) {
        var props = propertyNames.ToDictionary(
            name => name,
            name => type.GetProperty(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        );

        var exceptions = props
            .Where(entry => entry.Value == null)
            .Select(entry => new Exception($"{type.FullName} missing property {entry.Key}"))
            .ToList();

        if (exceptions.Count > 0) {
            throw new AggregateException("missing properties", exceptions);
        }

        return props!;
    }

    private async Task Install() {
        Console.Write("reading dalamud config... ");
        var dalamudConfigPath = Path.Join(this.DalamudFolder, "dalamudConfig.json");

        string dalamudConfigJson;
        try {
            dalamudConfigJson = await File.ReadAllTextAsync(dalamudConfigPath);
            Console.WriteLine("done");
        } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
            Console.WriteLine("file not found");
            return;
        }

        Console.WriteLine("parsing dalamud config...");

        // load the config
        this.Config = JsonConvert.DeserializeObject(dalamudConfigJson, this.ConfigurationType, Installer.ConfigJsonSettings);

        Console.Write("adding sea of stars to repo list... ");

        // add sea of stars to third party repo list if missing
        var repos = (System.Collections.IList?) this.ConfigurationProps["ThirdRepoList"].GetValue(this.Config);
        if (repos == null) {
            throw new Exception("third-party repos list was null");
        }

        string? already = null;
        foreach (var repo in repos) {
            var url = (string?) this.ThirdRepoSettingsProps["Url"].GetValue(repo);
            if (url == null) {
                throw new Exception("third-party repo url was null");
            }

            if (url.StartsWith(SeaOfStarsStartsWith, StringComparison.InvariantCultureIgnoreCase)) {
                already = url;
                break;
            }
        }

        if (already != null) {
            Console.WriteLine("already exists");
        } else {
            this.ConfigModified = true;

            var repo = Activator.CreateInstance(this.ThirdRepoSettingsType);
            this.ThirdRepoSettingsProps["Name"].SetValue(repo, null);
            this.ThirdRepoSettingsProps["Url"].SetValue(repo, SeaOfStarsRepo);
            this.ThirdRepoSettingsProps["IsEnabled"].SetValue(repo, true);

            repos.Add(repo);
            Console.WriteLine("done");
        }

        Console.WriteLine("downloading sea of stars repo...");

        var sosResp = await this.Client.GetAsync(already ?? SeaOfStarsRepo);
        var sosStream = await sosResp.Content.ReadAsStringAsync();
        var sosPlugins = JsonConvert.DeserializeObject<RepoPlugin[]>(sosStream)!;
        var penumbraPlugin = sosPlugins.First(plugin => plugin.InternalName == PenumbraInternalName);
        var heliospherePlugin = sosPlugins.First(plugin => plugin.InternalName == HeliosphereInternalName);

        this.ConfigModified |= await this.InstallPlugin(penumbraPlugin, already ?? SeaOfStarsRepo);
        this.ConfigModified |= await this.InstallPlugin(heliospherePlugin, already ?? SeaOfStarsRepo);

        if (this.ConfigModified) {
            Console.WriteLine("saving modified dalamud config...");

            // re-save the config
            var newJson = JsonConvert.SerializeObject(this.Config, this.ConfigurationType, Installer.ConfigJsonSettings);
            File.WriteAllText(dalamudConfigPath, newJson);
        } else {
            Console.WriteLine("config not modified, skipping...");
        }

        await this.CreatePenumbraConfig(penumbraPlugin);

        Console.WriteLine("installed!");
    }

    private async Task<bool> InstallPlugin(RepoPlugin repoPlugin, string repoUrl) {
        Console.Write($"checking if {repoPlugin.InternalName} is already installed... ");
        var defaultProfile = this.ConfigurationProps["DefaultProfile"].GetValue(this.Config);
        if (defaultProfile == null) {
            throw new Exception("default profile was null");
        }

        var plugins = (System.Collections.IList?) this.ProfileModelProps["Plugins"].GetValue(defaultProfile);
        if (plugins == null) {
            throw new Exception("plugins was null");
        }

        foreach (var installed in plugins) {
            var installedName = (string?) this.ProfilePluginProps["InternalName"].GetValue(installed);
            if (installedName == repoPlugin.InternalName) {
                Console.WriteLine("yes");
                return false;
            }
        }

        Console.WriteLine("no");

        Console.WriteLine($"installing {repoPlugin.InternalName}...");

        var resp = await this.Client.GetAsync(repoPlugin.DownloadLinkInstall);
        var zipStream = await resp.Content.ReadAsStreamAsync();

        // generate a working id
        var workingId = Guid.NewGuid();

        // download and extract the zip
        var zip = new ZipArchive(zipStream);
        var zipEntry = zip.GetEntry($"{repoPlugin.InternalName}.json");
        if (zipEntry == null) {
            throw new Exception($"missing {repoPlugin.InternalName}.json");
        }

        var json = await new StreamReader(zipEntry.Open()).ReadToEndAsync();
        var localManifest = JsonConvert.DeserializeObject(json, this.LocalManifestType);
        this.LocalManifestProps["WorkingPluginId"].SetValue(localManifest, workingId);
        this.LocalManifestProps["InstalledFromUrl"].SetValue(localManifest, repoUrl);

        var version = (Version?) this.LocalManifestProps["AssemblyVersion"].GetValue(localManifest);
        if (version == null) {
            throw new Exception("version was null");
        }

        var installDir = Path.Join(
            this.DalamudFolder,
            "installedPlugins",
            repoPlugin.InternalName,
            version.ToString()
        );

        try {
            Directory.Delete(installDir, true);
        } catch (Exception ex) when (ex is DirectoryNotFoundException) {
            // no-op
        }

        Directory.CreateDirectory(installDir);
        zip.ExtractToDirectory(installDir);

        await File.WriteAllTextAsync(Path.Join(installDir, $"{repoPlugin.InternalName}.json"), JsonConvert.SerializeObject(localManifest, this.LocalManifestType, Formatting.Indented, new JsonSerializerSettings()));

        Console.WriteLine($"installing {repoPlugin.InternalName} into default profile...");

        // install the plugin in the default profile
        var plugin = Activator.CreateInstance(this.ProfilePluginType);
        this.ProfilePluginProps["InternalName"].SetValue(plugin, repoPlugin.InternalName);
        this.ProfilePluginProps["WorkingPluginId"].SetValue(plugin, workingId);
        this.ProfilePluginProps["IsEnabled"].SetValue(plugin, true);

        plugins.Add(plugin);

        return true;
    }

    private async Task CreatePenumbraConfig(RepoPlugin plugin) {
        var pluginConfigs = Path.Join(this.DalamudFolder, "pluginConfigs");
        Directory.CreateDirectory(pluginConfigs);

        var penumbraConfigPath = Path.Join(pluginConfigs, $"{plugin.InternalName}.json");
        var good = false;
        MiniPenumbraConfig? config = null;
        try {
            var penumbraJson = await File.ReadAllTextAsync(penumbraConfigPath);
            config = JsonConvert.DeserializeObject<MiniPenumbraConfig>(penumbraJson)!;
            var info = new DirectoryInfo(config.ModDirectory);
            if (!info.Exists) {
                Console.WriteLine("penumbra directory doesn't exist: creating...");
                Directory.CreateDirectory(info.FullName);
            }

            var isGood = IsGood(info.FullName);
            good = isGood;
            if (!isGood) {
                Console.WriteLine("penumbra directory is in a bad location, making you select another...");
            }
        } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
            // no-op
        }

        if (good) {
            return;
        }

        while (!good) {
            Console.WriteLine("where would you like mods to be stored?");
            Console.Write("> ");
            var input = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(input)) {
                continue;
            }

            try {
                Directory.CreateDirectory(input);
            } catch (Exception ex) {
                Console.WriteLine($"could not create a directory at that path {ex.Message}. try again.");
                continue;
            }

            good = IsGood(input);
            if (good) {
                config ??= new MiniPenumbraConfig();
                config.ModDirectory = input;
            }
        }

        Console.WriteLine("writing penumbra config...");
        await File.WriteAllTextAsync(penumbraConfigPath, JsonConvert.SerializeObject(config, Formatting.Indented));

        return;

        static bool IsGood(string path) {
            var info = new DirectoryInfo(path);
            if ((info.Attributes & (FileAttributes.ReadOnly | FileAttributes.System)) != 0) {
                return false;
            }

            return true;
        }
    }
}

[Serializable]
public class MiniPenumbraConfig {
    public string ModDirectory { get; set; } = "";
}

[Serializable]
public class RepoPlugin {
    public string InternalName { get; set; } = "";
    public string DownloadLinkInstall { get; set; } = "";
}
