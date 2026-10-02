namespace DropSpace.Infrastructure.Lyrics;

/// <summary>A local resource admission failure, not a model or network failure.</summary>
public sealed class InferenceResourcesUnavailableException : IOException
{
    public InferenceResourcesUnavailableException()
        : base("Local CPU inference requires more available physical and commit memory, or the available memory could not be measured.") { }
}
