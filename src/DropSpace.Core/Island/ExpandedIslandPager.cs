using DropSpace.Core.Media;

namespace DropSpace.Core.Island;

public enum ExpandedIslandPage
{
    Files,
    Music,
    Widgets,
}

public readonly record struct IslandPageSize(double Width, double Height)
{
    public static readonly IslandPageSize Files = new(560, 340);
    public static readonly IslandPageSize Music = new(560, 300);
    public static readonly IslandPageSize Widgets = new(560, 340);
}

/// <summary>
/// Owns the expanded island's page boundary. Activity priority decides whether the island is
/// visible; this pager decides which page is shown after an explicit expansion or navigation.
/// </summary>
public sealed class ExpandedIslandPager
{
    public ExpandedIslandPage CurrentPage { get; private set; } = ExpandedIslandPage.Files;

    public bool IsUserNavigating { get; private set; }

    public bool CanGoLeft => (int)CurrentPage > (int)ExpandedIslandPage.Files;

    public bool CanGoRight => (int)CurrentPage < (int)ExpandedIslandPage.Widgets;

    public IslandPageSize PreferredSize => CurrentPage switch
    {
        ExpandedIslandPage.Music => IslandPageSize.Music,
        ExpandedIslandPage.Widgets => IslandPageSize.Widgets,
        _ => IslandPageSize.Files,
    };

    public void SetDefault(MediaPlaybackState playbackState)
    {
        CurrentPage = playbackState == MediaPlaybackState.Playing
            ? ExpandedIslandPage.Music
            : ExpandedIslandPage.Files;
        IsUserNavigating = false;
    }

    public bool NavigateLeft() => (int)CurrentPage > (int)ExpandedIslandPage.Files &&
        NavigateTo((ExpandedIslandPage)((int)CurrentPage - 1), userInitiated: true);

    public bool NavigateRight() => (int)CurrentPage < (int)ExpandedIslandPage.Widgets &&
        NavigateTo((ExpandedIslandPage)((int)CurrentPage + 1), userInitiated: true);

    public bool NavigateTo(ExpandedIslandPage page, bool userInitiated = true)
    {
        if (!Enum.IsDefined(page) || page == CurrentPage)
        {
            return false;
        }

        CurrentPage = page;
        IsUserNavigating |= userInitiated;
        return true;
    }
}
