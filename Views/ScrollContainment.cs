using System.Windows;

namespace UnrealManager.Views;

/// <summary>
/// Stops a list that scrolls internally from dragging the page around it.
///
/// Selecting a row, or replacing the rows under one, makes WPF ask for that row to be brought into
/// view. The request bubbles: the list's own scroll viewer answers it, then passes it on, and the
/// page's scroll viewer answers it too — scrolling the reader somewhere they did not ask to go.
/// Setting this on the list lets its own scroll viewer do its work and then stops the request there.
/// </summary>
public static class ScrollContainment
{
    public static readonly DependencyProperty ContainProperty =
        DependencyProperty.RegisterAttached(
            "Contain", typeof(bool), typeof(ScrollContainment), new PropertyMetadata(false, OnContainChanged));

    public static void SetContain(DependencyObject element, bool value) => element.SetValue(ContainProperty, value);

    public static bool GetContain(DependencyObject element) => (bool)element.GetValue(ContainProperty);

    private static void OnContainChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        element.RemoveHandler(FrameworkElement.RequestBringIntoViewEvent, Handler);
        if (e.NewValue is true)
            element.AddHandler(FrameworkElement.RequestBringIntoViewEvent, Handler, handledEventsToo: true);
    }

    // The list's own scroll viewer sits below this handler and has already scrolled by the time the
    // request gets here; marking it handled is what keeps it from carrying on up into the page.
    private static readonly RequestBringIntoViewEventHandler Handler = (_, e) => e.Handled = true;
}
