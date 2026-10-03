using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace AdofaiIpc.Core { public sealed class IpcRequest { public object Params { get; set; } } }
namespace AdofaiIpc {
    // The actual ABI is checked by the mod build. This isolated transport fixture
    // exercises its callbacks without initializing Unity or the installed IPC mod.
    public sealed class AdofaiIpcNamespace {
        private readonly Dictionary<string, Func<Core.IpcRequest, object>> methods = new();
        public void Register(string name, Func<Core.IpcRequest, object> method) => methods.Add(name, method);
        public JObject Call(string name, JObject parameters) => JObject.FromObject(methods[name](new Core.IpcRequest { Params = parameters }));
    }
}
namespace TUFReplayRenderer.Contracts {
    public sealed class RecordingBundle {
        public RecordingManifest Manifest { get; } = new() { InputsFile = "inputs.csv" };
        public string Directory { get; set; }
        public string ResolveFile(string path) => Path.Combine(Directory, path);
    }
}
