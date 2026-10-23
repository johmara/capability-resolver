using CapabilityResolver.Formats.Json;
using CapabilityResolver.Formats.Uvl;
using CapabilityResolver.Loading;
using CapabilityResolver.Model;

namespace CapabilityResolver.Formats.Tests;

internal static class PhoneFixtures
{
    public static string Dir(string format) => Path.Combine(AppContext.BaseDirectory, "fixtures", "phone", format);

    /// <summary>The phone system in the given format, with the global rules from the JSON fixtures.</summary>
    public static DecentralizedSystem Load(string format)
    {
        var loader = new ModelLoader(new JsonModelReader(), new YamlModelReader(), new UvlModelReader());
        var rules = RulesReader.Load(Path.Combine(Dir("json"), "rules.json"));
        return loader.LoadSystem([Dir(format)], rules);
    }
}
