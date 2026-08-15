using System;
using System.ComponentModel;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace RDL.GerberStitch.Facade.Settings
{
    // [Claude] [Change time: 2026-08-15] [Purpose: Core's option classes (DirectAlignmentOptions,
    // HalconNccOptions, PoseGraphOptions, ...) all carry [TypeConverter(typeof(ExpandableObjectConverter))] to
    // support the WinForms PropertyGrid in GerberViewer -- that attribute must stay on Core (AGENTS.md 3.1: no
    // behavior change there without confirmation). But System.ComponentModel.TypeConverter.CanConvertTo(string)
    // is TRUE by default for every type (every object has ToString()), and Newtonsoft.Json's default contract
    // resolver treats any type with a TypeConverter whose CanConvertTo(string) is true as a string-convertible
    // primitive. That breaks BOTH directions: reading a settings file throws "type requires a JSON string value"
    // instead of populating the object's properties, and writing effective_config.json silently calls the
    // type's ToString() override (e.g. "HalconShapeModel -> PyramidEcc") instead of serializing its real
    // property values -- discovered only by running the harness against real data, not by static review.
    // Extracted to its own file because both AlignStitchSettingsReader (read direction) and
    // GerberStitchFacade's effective_config.json write (write direction) need the same fix.]
    internal sealed class IgnoreTypeConverterResolver : DefaultContractResolver
    {
        protected override JsonContract CreateContract(Type objectType)
        {
            if (objectType.GetCustomAttributes(typeof(TypeConverterAttribute), true).Length > 0)
                return CreateObjectContract(objectType);
            return base.CreateContract(objectType);
        }
    }
}
