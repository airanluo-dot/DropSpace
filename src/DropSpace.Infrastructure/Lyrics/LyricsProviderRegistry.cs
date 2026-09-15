using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;

namespace DropSpace.Infrastructure.Lyrics;

public sealed class LyricsProviderRegistry
{
    private readonly IReadOnlyDictionary<LyricsProviderKind, ILyricsProvider> _providers;
    public LyricsProviderRegistry(LyricsHttpClient http, Func<string> localDirectory)
        : this([new NetEaseLyricsProvider(http), new QqMusicLyricsProvider(http),
            new KugouLyricsProvider(http), new LrclibLyricsProvider(http), new AmllLyricsProvider(http), new LocalLrcLyricsProvider(localDirectory)]) { }
    public LyricsProviderRegistry(IEnumerable<ILyricsProvider> providers)
    {
        _providers = providers.ToDictionary(provider => provider.Kind);
    }
    public ILyricsProvider Get(LyricsProviderKind kind) => _providers.TryGetValue(kind, out var provider)
        ? provider : throw new ArgumentOutOfRangeException(nameof(kind));
}
