using DropSpace.Core.Actions;
using DropSpace.App.Services;

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

    public string AutomationName { get; } = strings.Get(capability.Descriptor.LabelResourceKey);
}
