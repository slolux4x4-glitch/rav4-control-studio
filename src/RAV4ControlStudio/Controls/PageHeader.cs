using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RAV4ControlStudio.Controls;

/// <summary>Standard page heading used by every page: 24 pt title with a 14 pt muted subtitle.</summary>
public sealed class PageHeader : StackPanel
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(PageHeader), new PropertyMetadata("", (d, _) => ((PageHeader)d).Refresh()));

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(PageHeader), new PropertyMetadata("", (d, _) => ((PageHeader)d).Refresh()));

    private readonly TextBlock _title = new() { FontSize = 24, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _subtitle = new() { FontSize = 14, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };

    public PageHeader()
    {
        Margin = new Thickness(8, 4, 8, 10);
        Children.Add(_title);
        Children.Add(_subtitle);
        Loaded += (_, _) =>
        {
            _subtitle.Foreground = TryFindResource("MutedBrush") as Brush ?? Brushes.Gray;
        };
    }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }

    private void Refresh()
    {
        _title.Text = Title;
        _subtitle.Text = Subtitle;
        _subtitle.Visibility = string.IsNullOrEmpty(Subtitle) ? Visibility.Collapsed : Visibility.Visible;
    }
}
