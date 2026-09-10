using DropSpace.Core.Actions;
using DropSpace.Core.Abstractions;

namespace DropSpace.App.ViewModels;

public sealed class QuickActionButtonViewModel(
    ItemCardViewModel card,
    ItemActionCapability capability,
    IAppStringLocalizer strings)
{
    public ItemCardViewModel Card { get; } = card;

    public ItemActionId ActionId => capability.Descriptor.Id;

    public string Label { get; } = strings.Get(capability.Descriptor.LabelResourceKey);

    public string Icon { get; } = capability.Descriptor.Icon;

    public string Description { get; } = strings.Get(capability.Descriptor.Id == ItemActionId.HashSha256
        ? "HashResultDescription" : capability.Descriptor.LabelResourceKey);

    public string Glyph => ActionId switch
    {
        ItemActionId.ResizeImage => "\uE740",
        ItemActionId.ConvertImage => "\uE8AB",
        ItemActionId.StripMetadata => "\uE72E",
        ItemActionId.HashSha256 => "\uE9D9",
        ItemActionId.CompressZip => "\uF012",
        ItemActionId.GenerateQr => "\uED14",
        _ => "\uE10F",
    };

    public string AutomationName { get; } = strings.Get(capability.Descriptor.LabelResourceKey);
}
