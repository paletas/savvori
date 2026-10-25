using Savvori.Shared;

namespace Savvori.WebApi.Modeling;

public static class VerdictParser
{
    /// <summary>
    /// Defensive one-word parse: first word only, ignoring case, punctuation and quotes.
    /// Anything that is not clearly yes/no (including empty, "not sure", chatter) is Unclear.
    /// </summary>
    public static JudgeVerdict Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return JudgeVerdict.Unclear;
        var trimmed = content.TrimStart(' ', '\t', '\r', '\n', '"', '\'', '*', '`');
        var word = new string(trimmed.TakeWhile(char.IsLetter).ToArray()).ToLowerInvariant();
        return word switch
        {
            "yes" or "sim" => JudgeVerdict.Yes,
            "no" or "não" or "nao" => JudgeVerdict.No,
            _ => JudgeVerdict.Unclear
        };
    }
}
