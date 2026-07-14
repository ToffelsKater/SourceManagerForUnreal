namespace UnrealManager.ViewModels;

/// <summary>Static how-to page; all content lives in its DataTemplate.</summary>
public sealed class TutorialViewModel : PageViewModel
{
    public override string Title => "Guide";
    public override string Icon => "📖"; // open book
}
