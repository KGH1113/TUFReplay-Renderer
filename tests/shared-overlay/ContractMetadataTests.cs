using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
internal static class ContractMetadataTests
{
    internal static void Run()
    {
        string game = Environment.GetEnvironmentVariable("GAME_DIR") ?? "/Users/kgh/Library/Application Support/Steam/steamapps/common/A Dance of Fire and Ice";
        string managed = Environment.GetEnvironmentVariable("MANAGED_DIR") ?? Path.Combine(game, "ADanceOfFireAndIce.app", "Contents", "Resources", "Data", "Managed");
        using var source = new Contract(Path.Combine(managed, "SkyHook.Unity.dll"));
        const string ev = "SkyHook.SkyHookEvent", hub = "SkyHook.SkyHookManager";
        source.Field(ev, "TimeSec", "System.Int64", FieldAttributes.InitOnly);
        source.Field(ev, "TimeSubsecNano", "System.UInt32", FieldAttributes.InitOnly);
        source.Field(ev, "Type", "SkyHook.EventType", FieldAttributes.InitOnly);
        source.Field(ev, "Label", "SkyHook.KeyLabel", FieldAttributes.InitOnly);
        source.Field(ev, "Key", "System.UInt16", FieldAttributes.InitOnly);
        source.Method(ev, "GetTimeInTicks", "System.Int64");
        source.Method(hub, "NativeHookCallback", "System.Void", "System.IntPtr", ev);
        source.Method(hub, "HookCallback", "System.Void", ev);
        source.Method(hub, "get_isHookActive", "System.Boolean");
        source.Field(hub, "requireFocus", "System.Boolean", 0);
        source.Field(hub, "KeyUpdated", "UnityEngine.Events.UnityEvent`1<" + ev + ">", FieldAttributes.Static);
        source.Method("SkyHook.SkyHookKeyMapper", "UnityKeyToSkyHookKey", "SkyHook.KeyLabel", "UnityEngine.KeyCode");
        source.Method("SkyHook.SkyHookKeyMapper", "KeyLabelToNativeKeyCode", "System.UInt16", "SkyHook.KeyLabel");
        source.Method("SkyHook.SkyHookKeyMapper", "SkyHookKeyToUnityKey", "UnityEngine.KeyCode", "SkyHook.KeyLabel");
        using var unity = new Contract(Path.Combine(managed, "UnityEngine.CoreModule.dll"));
        unity.Method("UnityEngine.Events.UnityEvent`1", "Invoke", "System.Void", "!0");
        Console.WriteLine("PASS: shared SkyHook event, callback, focus policy and key mapper metadata contracts.");
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
