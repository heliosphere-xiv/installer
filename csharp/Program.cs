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
        var workingIdStr = Marshal.PtrToStringUTF8((nint) workingIdRaw, workingIdLen);
        var repoUrl = Marshal.PtrToStringUTF8((nint) repoUrlRaw, repoUrlLen);

        var workingId = Guid.Parse(workingIdStr);

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

    [UnmanagedCallersOnly]
    public static unsafe bool IsPathValid(byte* pathRaw, int pathLen) {
        var path = Marshal.PtrToStringUTF8((nint) pathRaw, pathLen);

        try {
            var info = new DirectoryInfo(path);
            if ((info.Attributes & (FileAttributes.ReadOnly | FileAttributes.System)) != 0) {
                return false;
            }
        } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
            return false;
        }

        return true;
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
