using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;

namespace DropSpace.Infrastructure.Lyrics;

public sealed class LyricsProviderRegistry
{
    private readonly IReadOnlyDictionary<LyricsProviderKind, ILyricsProvider> _providers;
    public LyricsProviderRegistry(LyricsHttpClient http, Func<string> localDirectory, ILyricsLanguageIdentifier? languageIdentifier = null)
        : this([new NetEaseLyricsProvider(http, languageIdentifier: languageIdentifier), new QqMusicLyricsProvider(http, languageIdentifier),
            new KugouLyricsProvider(http, languageIdentifier), new LrclibLyricsProvider(http), new AmllLyricsProvider(http, languageIdentifier), new LocalLrcLyricsProvider(localDirectory)]) { }
    public LyricsProviderRegistry(IEnumerable<ILyricsProvider> providers)
    {
        _providers = providers.ToDictionary(provider => provider.Kind);
    }
    public ILyricsProvider Get(LyricsProviderKind kind) => _providers.TryGetValue(kind, out var provider)
        ? provider : throw new ArgumentOutOfRangeException(nameof(kind));
    public void ClearResponseCaches()
    {
        foreach (var provider in _providers.Values.OfType<ILyricsResponseCache>()) provider.ClearResponseCache();
    }
}
