using CapabilityResolver.Loading;
using CapabilityResolver.Model;

namespace CapabilityResolver.Formats.Clafer;

/// <summary>
/// Reads <c>*.cfr</c> files: the feature-model subset of Clafer, parsed with an ANTLR grammar generated from the
/// official LBNF grammar <c>clafer.cf</c> by BNFC (see <c>Grammar/README.md</c>; indentation is resolved like upstream's LayoutResolver).
/// <list type="bullet">
/// <item>Clafers are features. No cardinality means mandatory, <c>?</c> (or <c>0..1</c>) optional.</item>
/// <item>A group keyword before a clafer (<c>xor</c>, <c>or</c>, <c>mux</c>, <c>opt</c>, or an interval such as <c>1..2</c>)
/// applies to all of its children. A clafer without one gets a mandatory group and an optional group. Mixing a group
/// with separate mandatory or optional children therefore needs an intermediate clafer, as in Clafer itself.</item>
/// <item><c>abstract</c> marks a feature as abstract (no inheritance: <c>:</c> is rejected).</item>
/// <item><c>[ ... ]</c> are boolean constraints (<c>&amp;&amp;</c>, <c>||</c>, <c>!</c>, <c>=&gt;</c>, <c>&lt;=&gt;</c>,
/// <c>some X</c>, <c>no X</c>); several expressions in one bracket are several constraints and a constraint may span
/// lines. Dotted paths such as <c>Shop.Card</c> resolve to their last segment.</item>
/// <item>Capabilities are string sub-clafers: <c>provides -&gt; string = "cap"</c>, <c>requires -&gt; string = "cap"</c>, repeatable.</item>
/// <item>The model id is the file name without its extension, since Clafer has no module name.</item>
/// <item>The words reserved by the Clafer grammar (its keywords and the single capitals <c>U W F G X</c>) cannot be feature names.</item>
/// </list>
/// Quantifiers over declarations, arithmetic, references, inheritance, enums and multiple instances
/// (<c>*</c>, <c>+</c>) are rejected.
/// </summary>
public sealed class ClaferModelReader : IModelReader
{
    public bool CanRead(string path) => path.EndsWith(".cfr", StringComparison.OrdinalIgnoreCase);

    public FeatureModel Read(string text, string source)
    {
        try
        {
            return ClaferTreeMapper.Read(text, Path.GetFileNameWithoutExtension(source));
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            throw new ModelFormatException(source, e.Message, e);
        }
    }
}
