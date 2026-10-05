using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// Metadata-only reads verify the installed contract without loading a game assembly or running any
// Unity/static initialization. These checks deliberately fail when an investigated plugin changes.
internal static class ContractMetadataTests
{
    internal static void Run()
    {
        string game = Environment.GetEnvironmentVariable("GAME_DIR") ?? "/Users/kgh/Library/Application Support/Steam/steamapps/common/A Dance of Fire and Ice";
        string mod = Path.Combine(game, "Mods", "JipperResourcePack");
        using var source = new Contract(Environment.GetEnvironmentVariable("JIPPER_RP_DLL") ?? Path.Combine(mod, "JipperResourcePack.dll"));
        const string viewer = "JipperResourcePack.KeyViewerContents.KeyViewer", key = "JipperResourcePack.KeyViewerContents.Key", text = "JipperResourcePack.Async.AsyncText", counts = "JipperResourcePack.KeyViewerContents.KeyCountData";
        source.Method(viewer + "+KeyEvent", ".ctor", "System.Void", "SkyHook.KeyLabel", "System.UInt16", "System.Boolean", "System.Int64");
        source.Method(viewer, "ProcessKeyEvent", "System.Void", viewer + "+KeyEvent");
        source.Method(viewer, "OnKeyEvent", "System.Void", "SkyHook.SkyHookEvent");
        source.Method(viewer, "get_CurrentTicks", "System.Int64");
        source.Method(viewer, "Work", "System.Boolean", "System.Int32[]", "System.Boolean", "System.Int64");
        source.Method(key, "UpdateRequestKey", "System.Void", "System.Boolean");
        source.Method(key, "UpdateKey", "System.Void", "System.Boolean");
        source.Method(text, "set_Text", "System.Void", "System.String");
        source.Method(text, "SetTextForce", "System.Void", "System.String");
        source.Method(counts, "Save", "System.Void");
        source.Field(counts, "Count", "System.Int32[]", FieldAttributes.InitOnly);
        source.Field(counts, "Instance", counts, FieldAttributes.Static);
        source.Field(counts, "TotalCount", "System.Int32", 0);
        source.Field(viewer, "_keyState", "System.Boolean[]", FieldAttributes.InitOnly);
        source.Field(viewer, "_listening", "System.Boolean", 0);
        source.Field(viewer, "_eventSignal", "System.Threading.SemaphoreSlim", 0);
        source.Field(viewer, "_eventQueue", "System.Collections.Concurrent.ConcurrentQueue`1<" + viewer + "+KeyEvent>", 0);
        source.Field(viewer, "_pressTimes", "System.Collections.Concurrent.ConcurrentQueue`1<System.Int64>", 0);
        source.Field(key, "RainPool", "JipperResourcePack.KeyViewerContents.RainPool", 0);
        source.Field(key, "LastRain", "JipperResourcePack.KeyViewerContents.RawRain", 0);
        source.Field(key, "_requestEnabled", "System.Boolean", 0);
        source.Field(key, "_updateRequested", "System.Int32", 0);
        source.Field(text, "_text", "System.String", 0);
        source.Field(text, "_textChangeRequested", "System.Int32", 0);
        source.Method("JipperResourcePack.KeyViewerContents.RainPool", ".ctor", "System.Void", "UnityEngine.RectTransform");
        source.Method("JipperResourcePack.KeyViewerContents.RainPool", "GetOrNewRain", "JipperResourcePack.KeyViewerContents.Rain", "System.Boolean");
        source.Method("JipperResourcePack.KeyViewerContents.RainManager", "Update", "System.Void");
        using var version = new Contract(Environment.GetEnvironmentVariable("JIPPER_RP_VERSION_DLL") ?? Path.Combine(mod, "VersionSafe", "JipperResourcePack.VersionSafe.R149.dll"));
        version.Method("JipperResourcePack.VersionSafe", "UnityKeyToSkyHookKey", "SkyHook.KeyLabel", "UnityEngine.KeyCode");
        version.Method("JipperResourcePack.VersionSafe", "GetHitMarginsCount", "System.Int32[]");
        string managed = Environment.GetEnvironmentVariable("MANAGED_DIR") ?? Path.Combine(game, "ADanceOfFireAndIce.app", "Contents", "Resources", "Data", "Managed");
        using var skyHook = new Contract(Path.Combine(managed, "SkyHook.Unity.dll"));
        skyHook.Method("SkyHook.SkyHookKeyMapper", "KeyLabelToNativeKeyCode", "System.UInt16", "SkyHook.KeyLabel");
        source.Method("JipperResourcePack.OverlayContents.Overlay", "UpdateJudgement", "System.Void", "System.Int32");
        source.Method("JipperResourcePack.OverlayContents.Overlay", "SetupTextManager", "System.Void");
        string ghostPath = Environment.GetEnvironmentVariable("GHOSTIFY_OVERLAY_DLL");
        if (!string.IsNullOrEmpty(ghostPath)) Ghostify(ghostPath);
    }
    private static void Ghostify(string path)
    {
        using var source = new Contract(path);
        const string prefix = "DonQuixoteOverlay.KeyViewerContents.";
        string viewer = prefix + "KeyViewer", key = prefix + "Key", count = prefix + "KeyCountData", state = prefix + "KeyTransitionState";
        source.Method(viewer, "WorkUnity", "System.Void", "UnityEngine.KeyCode", "System.Boolean", "System.Int64");
        source.Method(viewer, "Update", "System.Void");
        source.Method(viewer, "UpdateCounters", "System.Void");
        source.Method(viewer, "BeginGameplayRun", "System.Void");
        source.Method(viewer, "PumpInput", "System.Void");
        source.Method(viewer, "ResetTransient", "System.Void", "System.Boolean");
        source.Method(viewer, "OnApplicationFocus", "System.Void", "System.Boolean");
        source.Method(viewer, "OnKeyEvent", "System.Void", "SkyHook.SkyHookEvent");
        source.Method(viewer, "ObserveRawEvent", "System.Void", "SkyHook.SkyHookEvent");
        source.Method(viewer, "get_CurrentTicks", "System.Int64");
        source.Field(viewer, "_shownCounts", "System.Int64[]", 0);
        source.Field(viewer, "_state", state, FieldAttributes.InitOnly);
        source.Field(viewer, "_lastTotalCount", "System.Int64", 0);
        source.Field(viewer, "_lastKpsCount", "System.Int32", 0);
        source.Field(viewer, "_keyState", "System.Boolean[]", FieldAttributes.InitOnly);
        foreach (string field in new[] { "_focused", "_visible", "_suspended", "_rawGhostInput" }) source.Field(viewer, field, "System.Boolean", 0);
        source.Field(state, "_state", "System.Boolean[]", FieldAttributes.InitOnly);
        source.Field(state, "_startHeld", "System.Boolean[]", FieldAttributes.InitOnly);
        source.Method(count, "Flush", "System.Void", "System.Int64", "System.Boolean");
        source.Method(count, "Save", "System.Void");
        source.Method(count, ".ctor", "System.Void");
        source.Field(count, "Count", "System.Int64[]", 0);
        source.Method(key, "UpdateKey", "System.Void", "System.Boolean");
        foreach (string field in new[] { "_requested", "_current", "_dirty" }) source.Field(key, field, "System.Boolean", 0);
        source.Method(prefix + "RainPool", ".ctor", "System.Void", "UnityEngine.RectTransform");
        source.Method(prefix + "RainPool", "GetOrNewRain", prefix + "Rain", "System.Boolean");
        source.Field(prefix + "RainPool", "_layers", "UnityEngine.RectTransform[]", FieldAttributes.InitOnly);
        source.Method(prefix + "RainManager", "Tick", "System.Void");
        source.Method("DonQuixoteOverlay.OverlayController", "Update", "System.Void");
        source.Field("DonQuixoteOverlay.OverlayController", "_metadataNextUpdate", "System.Single", 0);
        source.Method("DonQuixoteOverlay.OverlayController", "ErrorMeterAnchor", "UnityEngine.Vector2", "scrController");
    }
    private sealed class Contract : IDisposable
    {
        private readonly Stream stream;
        private readonly PEReader pe;
        private readonly MetadataReader reader;
        private readonly Names names = new();
        internal Contract(string path) { stream = File.OpenRead(path); pe = new PEReader(stream); reader = pe.GetMetadataReader(); }
        private TypeDefinition Type(string name) => reader.GetTypeDefinition(reader.TypeDefinitions.Single(h => names.GetTypeFromDefinition(reader, h, 0) == name));
        internal void Method(string type, string name, string returnType, params string[] parameters)
        {
            bool found = Type(type).GetMethods().Select(reader.GetMethodDefinition).Where(m => reader.GetString(m.Name) == name).Any(m => { var signature = m.DecodeSignature(names, (object)null); return signature.ReturnType == returnType && signature.ParameterTypes.SequenceEqual(parameters); });
            if (!found) throw new Exception("Installed native contract changed: " + type + "." + name + "(" + string.Join(",", parameters) + ") -> " + returnType);
        }
        internal void Field(string type, string name, string fieldType, FieldAttributes required)
        {
            bool found = Type(type).GetFields().Select(reader.GetFieldDefinition).Any(f => reader.GetString(f.Name) == name && f.DecodeSignature(names, (object)null) == fieldType && (f.Attributes & required) == required);
            if (!found) throw new Exception("Installed native field changed: " + type + "." + name);
        }
        public void Dispose() { pe.Dispose(); stream.Dispose(); }
    }
    private sealed class Names : ISignatureTypeProvider<string, object>
    {
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "function-pointer";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";
        public string GetGenericMethodParameter(object genericContext, int index) => "!!" + index;
        public string GetGenericTypeParameter(object genericContext, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "System." + typeCode;
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var type = reader.GetTypeDefinition(handle); var declaring = type.GetDeclaringType();
            return declaring.IsNil ? Prefix(reader.GetString(type.Namespace), reader.GetString(type.Name)) : GetTypeFromDefinition(reader, declaring, 0) + "+" + reader.GetString(type.Name);
        }
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) { var type = reader.GetTypeReference(handle); return Prefix(reader.GetString(type.Namespace), reader.GetString(type.Name)); }
        public string GetTypeFromSpecification(MetadataReader reader, object genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        private static string Prefix(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;
    }
}
