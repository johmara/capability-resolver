using CapabilityResolver.Model;

namespace CapabilityResolver.Loading;

/// <summary>Raised when a model file cannot be read. <see cref="SourceName"/> is the file (or other origin) that failed.</summary>
public sealed class ModelFormatException(string source, string message, Exception? inner = null)
    : Exception($"{source}: {message}", inner)
{
    public string SourceName { get; } = source;
}

/// <summary>Reads one model language. Each format ships its own reader, so consumers only take the dependencies they use.</summary>
public interface IModelReader
{
    bool CanRead(string path);
    FeatureModel Read(string text, string source);
}

/// <summary>Picks a reader by file name and assembles scattered per-scope model files into one system.</summary>
public sealed class ModelLoader(params IModelReader[] readers)
{
    public FeatureModel Load(string path)
    {
        var reader = readers.FirstOrDefault(r => r.CanRead(path))
            ?? throw new ModelFormatException(path, "no reader registered for this file type.");
        return reader.Read(File.ReadAllText(path), path);
    }

    /// <summary>
    /// Loads every model file under the given files and directories. Directories are searched recursively;
    /// files no reader recognizes are skipped.
    /// </summary>
    public DecentralizedSystem LoadSystem(IEnumerable<string> paths, IReadOnlyList<GlobalRule>? rules = null)
    {
        var files = paths
            .SelectMany(p => Directory.Exists(p) ? Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories) : [p])
            .Where(f => readers.Any(r => r.CanRead(f)))
            .Order(StringComparer.Ordinal)
            .ToList();

        return new DecentralizedSystem(files.Select(Load).ToList(), rules);
    }
}
