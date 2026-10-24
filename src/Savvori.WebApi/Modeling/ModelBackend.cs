using Savvori.Shared;

namespace Savvori.WebApi.Modeling;

/// <summary>The remote service a model call goes to; each has its own circuit breaker and probe.</summary>
public enum ModelBackend { Gateway, OpenRouter }

public static class ModelBackends
{
    /// <summary>Embed and Translate jobs run on the Gateway, Judge jobs on OpenRouter.</summary>
    public static ModelBackend For(ModelJobType t) => t == ModelJobType.Judge ? ModelBackend.OpenRouter : ModelBackend.Gateway;

    public static string Tag(ModelBackend b) => b == ModelBackend.Gateway ? "gateway" : "openrouter";
}
