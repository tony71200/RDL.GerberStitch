using System;
using System.IO;

namespace GerberViewer.Stitching.Configuration
{
    public static class AppPaths
    {
        public static string SampleConfigPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "gerber_sample_config.json"); }
        }
    }
}
