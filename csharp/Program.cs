using System.Reflection;
using System.Runtime.InteropServices;

namespace HeliosphereInstaller;

public class Installer {
    const string SeaOfStarsRepo = "https://raw.githubusercontent.com/Ottermandias/SeaOfStars/main/repo.json";
    const string SeaOfStarsStartsWith = "https://raw.githubusercontent.com/Ottermandias/SeaOfStars/";
    const string PenumbraInternalName = "Penumbra";
    const string HeliosphereInternalName = "heliosphere-plugin";

    private Serde Serde { get; }
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

    private static Installer Instance = null!;

    private static unsafe delegate*<char*, int, byte*> CopyToCString;

    [UnmanagedCallersOnly]
    public static unsafe void SetCopyToCStringFunctionPtr(delegate*<char*, int, byte*> copyToCString) => CopyToCString = copyToCString;

    [UnmanagedCallersOnly]
    public static unsafe void Initialise(
        byte* dalamudPathRaw,
        int dalamudPathLen,
        byte* dalamudCommonPathRaw,
        int dalamudCommonPathLen,
        byte* newtonsoftPathRaw,
        int newtonsoftPathLen
    ) {
        var dalamudPath = Marshal.PtrToStringUTF8((nint) dalamudPathRaw, dalamudPathLen);
        var dalamudCommonPath = Marshal.PtrToStringUTF8((nint) dalamudCommonPathRaw, dalamudCommonPathLen);
        var newtonsoftPath = Marshal.PtrToStringUTF8((nint) newtonsoftPathRaw, newtonsoftPathLen);

        var newtonsoft = Assembly.LoadFrom(newtonsoftPath);
        Assembly.LoadFrom(dalamudCommonPath);
        var dalamud = Assembly.LoadFrom(dalamudPath);
        Installer.Instance = new Installer(dalamud, newtonsoft);
    }

    [UnmanagedCallersOnly]
    public static unsafe byte* MakeRepo(byte* urlRaw, int urlLen) {
        var self = Installer.Instance;

        var url = Marshal.PtrToStringUTF8((nint) urlRaw, urlLen);

        var repo = Activator.CreateInstance(self.ThirdRepoSettingsType);
        self.ThirdRepoSettingsProps["Name"].SetValue(repo, null);
        self.ThirdRepoSettingsProps["Url"].SetValue(repo, url);
        self.ThirdRepoSettingsProps["IsEnabled"].SetValue(repo, true);

        var json = self.Serde.Serialise(repo, self.ThirdRepoSettingsType, self.Serde.ConfigJsonSettings);
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

        var json = self.Serde.Serialise(plugin, self.ProfilePluginType, self.Serde.ConfigJsonSettings);
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

        var manifest = self.Serde.Deserialise(manifestJson, self.LocalManifestType);
        self.LocalManifestProps["WorkingPluginId"].SetValue(manifest, workingId);
        self.LocalManifestProps["InstalledFromUrl"].SetValue(manifest, repoUrl);

        var json = self.Serde.Serialise(manifest, self.LocalManifestType, 1);
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

    private static readonly Environment.SpecialFolder[] BadRoots = [
        Environment.SpecialFolder.ProgramFiles,
        Environment.SpecialFolder.ProgramFilesX86,
        Environment.SpecialFolder.Programs,
        Environment.SpecialFolder.CommonProgramFiles,
        Environment.SpecialFolder.CommonProgramFilesX86,
        Environment.SpecialFolder.CommonPrograms,
        Environment.SpecialFolder.System,
        Environment.SpecialFolder.SystemX86,
    ];

    private static string MakeEndWithSeparator(string input) {
        return input.TrimEnd('/', '\\') + '/';
    }

    [UnmanagedCallersOnly]
    public static unsafe byte IsPathValid(byte* pathRaw, int pathLen) {
        var path = Marshal.PtrToStringUTF8((nint) pathRaw, pathLen);

        foreach (var badRootKind in BadRoots) {
            var badRoot = Environment.GetFolderPath(badRootKind);

            var badUri = new Uri(MakeEndWithSeparator(badRoot));
            var pathUri = new Uri(MakeEndWithSeparator(path));

            if (badUri.IsBaseOf(pathUri)) {
                return 0;
            }
        }

        try {
            var info = new DirectoryInfo(path);
            if ((info.Attributes & (FileAttributes.ReadOnly | FileAttributes.System)) != 0) {
                return 0;
            }
        } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
            return 0;
        }

        return 1;
    }

    private Installer(Assembly dalamud, Assembly newtonsoft) {
        this.Serde = new Serde(newtonsoft);
        this.Client = new HttpClient();
        this.DalamudFolder = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher");

        var (localManifestType, localManifestProps) = GetTypeAndProperties(
            dalamud,
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
            dalamud,
            "Dalamud.Configuration.Internal.DalamudConfiguration",
            [
                "ThirdRepoList",
                "DefaultProfile",
            ]
        );

        this.ConfigurationType = configurationType;
        this.ConfigurationProps = configurationProps;

        var (profileModelType, profileModelProps) = GetTypeAndProperties(
            dalamud,
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
            dalamud,
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

public class Serde {
    private Type SettingsType { get; }
    private Type FormattingType { get; }
    private MethodInfo SerialiseMethod { get; }
    private MethodInfo DeserialiseMethod { get; }
    public object ConfigJsonSettings { get; }

    public Serde(Assembly assembly) {
        var convert = assembly.GetType("Newtonsoft.Json.JsonConvert");
        var settings = assembly.GetType("Newtonsoft.Json.JsonSerializerSettings");
        var formatting = assembly.GetType("Newtonsoft.Json.Formatting");
        if (convert == null || settings == null || formatting == null) {
            throw new Exception("missing JsonConvert, JsonSerializerSettings, or Formatting");
        }

        var serialiseParams = new Type[] {
            typeof(object),
            typeof(Type),
            settings,
        };
        var serialiseMethod = convert
            .GetMethods(BindingFlags.Static | BindingFlags.Public)
            .First(
                method =>
                    method.Name == "SerializeObject"
                    && method.GetParameters().Select(param => param.ParameterType).SequenceEqual(serialiseParams)
            );

        var deserialiseParams = new Type[] {
            typeof(string),
            settings,
        };
        var deserialiseMethod = convert
            .GetMethods(BindingFlags.Static | BindingFlags.Public)
            .First(
                method =>
                    method.Name == "DeserializeObject"
                    && method.IsGenericMethod
                    && method.GetParameters().Select(param => param.ParameterType).SequenceEqual(deserialiseParams)
            );

        var configSettings = Activator.CreateInstance(settings)!;
        settings.GetProperty("TypeNameHandling")!.SetValue(configSettings, 1 | 2);
        settings.GetProperty("TypeNameAssemblyFormatHandling")!.SetValue(configSettings, 0);
        settings.GetProperty("Formatting")!.SetValue(configSettings, 1);

        this.SettingsType = settings;
        this.FormattingType = formatting;
        this.SerialiseMethod = serialiseMethod;
        this.DeserialiseMethod = deserialiseMethod;
        this.ConfigJsonSettings = configSettings;
    }

    public string Serialise(object? obj, Type type, int formatting, object? settings = null) {
        settings ??= Activator.CreateInstance(this.SettingsType);
        this.SettingsType.GetProperty("Formatting")!.SetValue(settings, formatting);

        return this.Serialise(obj, type, settings);
    }

    public string Serialise(object? obj, Type type, object? settings = null) {
        settings ??= Activator.CreateInstance(this.SettingsType);
        var json = this.SerialiseMethod.Invoke(null, [
            obj,
            type,
            settings,
        ]) as string;

        return json!;
    }

    public object? Deserialise(string json, Type type, object? settings = null) {
        settings ??= Activator.CreateInstance(this.SettingsType);

        return this.DeserialiseMethod
            .MakeGenericMethod(type)
            .Invoke(null, [
                json,
                settings
            ]);
    }
}
