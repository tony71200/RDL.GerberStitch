using System;
using System.IO;
using System.Runtime.Serialization.Json;

namespace GerberViewer.Stitching.Configuration
{
    public sealed class SampleConfigStore
    {
        private readonly string _path;
        public SampleConfigStore() : this(AppPaths.SampleConfigPath)
        {
        }
        public SampleConfigStore(string path)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
        }
        public string ConfigPath
        {
            get {
                return _path;
            }
        }

        public GerberSampleConfig LoadOrCreateDefault()
        {
            EnsureDirectory();
            if (!File.Exists(_path))
            {
                var config = new GerberSampleConfig();
                Save(config);
                return config;
            }
            try
            {
                return Load();
            }
            catch (Exception ex)
                when (ex is InvalidDataException || ex is System.Runtime.Serialization.SerializationException ||
                      ex is ArgumentException)
            {
                var backup = Path.Combine(Path.GetDirectoryName(_path),
                                          Path.GetFileNameWithoutExtension(_path) + ".invalid_" +
                                              DateTime.Now.ToString("yyyyMMdd_HHmmss") + Path.GetExtension(_path));
                File.Move(_path, backup);
                var config = new GerberSampleConfig();
                Save(config);
                throw new InvalidDataException("Sample config JSON was invalid. Backed up to: " + backup, ex);
            }
        }

        public GerberSampleConfig Load()
        {
            using (var stream = File.OpenRead(_path))
            {
                var serializer = new DataContractJsonSerializer(typeof(GerberSampleConfig));
                var config = serializer.ReadObject(stream) as GerberSampleConfig;
                if (config == null)
                    throw new InvalidDataException("Sample config JSON did not contain a GerberSampleConfig.");
                return config;
            }
        }

        public void Save(GerberSampleConfig config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            EnsureDirectory();
            var tmp = _path + ".tmp";
            IndentedJson.WriteDataContract(tmp, config, typeof(GerberSampleConfig));
            using (var stream = File.OpenRead(tmp))
            {
                var serializer = new DataContractJsonSerializer(typeof(GerberSampleConfig));
                if (!(serializer.ReadObject(stream) is GerberSampleConfig))
                    throw new InvalidDataException("Saved temp config could not be read back.");
            }
            if (File.Exists(_path))
                File.Delete(_path);
            File.Move(tmp, _path);
        }

        private void EnsureDirectory()
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
        }
    }
}
