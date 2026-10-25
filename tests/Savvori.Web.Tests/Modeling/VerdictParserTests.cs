using Savvori.Shared;
using Savvori.WebApi.Modeling;

namespace Savvori.Web.Tests.Modeling;

public sealed class VerdictParserTests
{
    [Theory]
    [InlineData("yes", JudgeVerdict.Yes)]
    [InlineData("Yes.", JudgeVerdict.Yes)]
    [InlineData("  YES\n", JudgeVerdict.Yes)]
    [InlineData("**Yes**", JudgeVerdict.Yes)]
    [InlineData("\"yes\"", JudgeVerdict.Yes)]
    [InlineData("Yes, they are the same product", JudgeVerdict.Yes)]
    [InlineData("sim", JudgeVerdict.Yes)]
    [InlineData("no", JudgeVerdict.No)]
    [InlineData("No.", JudgeVerdict.No)]
    [InlineData("Não", JudgeVerdict.No)]
    [InlineData("unsure", JudgeVerdict.Unclear)]
    [InlineData("Not sure", JudgeVerdict.Unclear)]
    [InlineData("Maybe yes?", JudgeVerdict.Unclear)]
    [InlineData("I think they differ", JudgeVerdict.Unclear)]
    [InlineData("", JudgeVerdict.Unclear)]
    [InlineData(null, JudgeVerdict.Unclear)]
    [InlineData("...", JudgeVerdict.Unclear)]
    public void Parse_IsDefensive(string? content, JudgeVerdict expected) =>
        Assert.Equal(expected, VerdictParser.Parse(content));
}
