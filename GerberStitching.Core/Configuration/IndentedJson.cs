using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;

namespace GerberViewer.Stitching.Configuration
{
    internal static class IndentedJson
    {
        public static void WriteDataContract(string path, object value, Type type)
        {
            if (value == null) throw new ArgumentNullException("value");
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            using (var stream = File.Create(path))
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, false, true, "  "))
            {
                new DataContractJsonSerializer(type).WriteObject(writer, value);
                writer.Flush();
            }
        }
    }
}
