using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace TUFReplayRenderer.Contracts {
    public sealed class RecordingBundle {
        public RecordingManifest Manifest { get; } = new() { InputsFile = "inputs.csv" };
        public string Directory { get; set; }
        public string ResolveFile(string path) => Path.Combine(Directory, path);
    }
}
