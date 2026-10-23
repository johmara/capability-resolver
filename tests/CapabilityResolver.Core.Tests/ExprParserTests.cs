using CapabilityResolver.Model;

namespace CapabilityResolver.Core.Tests;

public class ExprParserTests
{
    [Theory]
    [InlineData("A", "A")]
    [InlineData("!A", "!A")]
    [InlineData("A & B | C", "((A & B) | C)")]
    [InlineData("A | B & C", "(A | (B & C))")]
    [InlineData("A => B => C", "(A => (B => C))")]
    [InlineData("A <=> B => C", "(A <=> (B => C))")]
    [InlineData("!(A | B) & C", "(!(A | B) & C)")]
    [InlineData("\"Mobile Data\" => Wifi", "(\"Mobile Data\" => Wifi)")]
    public void ParsesWithUvlPrecedence(string text, string expected)
    {
        Assert.Equal(expected, ExprParser.Parse(text).ToString());
    }

    [Theory]
    [InlineData("A & ")]
    [InlineData("(A")]
    [InlineData("A B")]
    [InlineData("A # B")]
    [InlineData("\"open")]
    public void RejectsMalformedInput(string text)
    {
        Assert.Throws<FormatException>(() => ExprParser.Parse(text));
    }

    [Fact]
    public void ToStringOutputParsesBackToTheSameExpression()
    {
        var expr = ExprParser.Parse("!(A & \"B C\") <=> (D => E | F)");
        Assert.Equal(expr, ExprParser.Parse(expr.ToString()));
    }

    [Fact]
    public void EvaluatesAgainstASelection()
    {
        var expr = ExprParser.Parse("A => B & !C");
        Assert.True(expr.Evaluate(new HashSet<string> { "A", "B" }.Contains));
        Assert.False(expr.Evaluate(new HashSet<string> { "A", "B", "C" }.Contains));
        Assert.True(expr.Evaluate(_ => false));
    }
}
