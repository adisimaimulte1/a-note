using System.Collections.ObjectModel;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ANote.Ink;
using ANote.Models;
using ANote.Services;
using ANote.Controls;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WinRT.Interop;
using Windows.Graphics;
using Windows.System;
using Windows.Storage.Pickers;
using Windows.UI;

namespace ANote;

public sealed partial class MainWindow : Window
{
    private enum NotebookSortMode { Default, Alphabetical }
    private const uint WmGetMinMaxInfo = 0x0024;
    private const int MinimumWindowWidth = 1000;
    private const int MinimumWindowHeight = 640;
    private readonly DatabaseService _db = new();
    private readonly ObservableCollection<Notebook> _notebooks = [];
    private Notebook? _currentNotebook;
    private ListView? _pageList;
    private readonly ObservableCollection<PageView> _pageViews = [];
    private Border? _floatingToolbar;
    private Button? _pageTypeButton;
    private Button? _undoButton;
    private Button? _redoButton;
    private Button? _scrollToolButton;
    private bool _penScrollMode;
    private Action? _toggleEditorDrawer;
    private Action? _hideEditorDrawer;
    private Action? _syncEditorDrawerGestureHitTesting;
    private TextBox? _searchBox;
    private ListView? _libraryList;
    private Grid? _libraryScrollTrack;
    private StackPanel? _libraryEmptyState;
    private TextBlock? _libraryEmptyTitle;
    private TextBlock? _libraryEmptyDetail;
    private string _searchQuery = "";
    private NotebookSortMode _notebookSortMode = NotebookSortMode.Default;
    private bool _favoritesOnly;
    private string? _subjectFilter;
    private string? _colorFilter;
    private Button? _filterButton;
    private Flyout? _libraryFilterFlyout;
    private const double NotebookGap = 18;
    private double _notebookViewportHeight;
    private double _notebookViewportWidth;
    private readonly Dictionary<string, int> _favoriteSaveRevisions = [];
    private int _searchRevision;
    private InkPageControl? _activeInk;
    private InkTool _tool = InkTool.Pen;
    private InkTool _eraseTarget = InkTool.Pen;
    private bool _eraserEnabled;
    private AppSettings _settings = new();
    private AppWindow? _appWindow;
    private readonly SubclassProc _windowSubclass;
    private bool _windowSubclassInstalled;
    private bool _closing;
    private bool _initialEntrancePlayed;
    private List<UIElement>? _pendingInitialEntranceStages;
    private DateTimeOffset _lastPenActivity = DateTimeOffset.MinValue;
    private string _editorAccent = "#FF7A18";

    public MainWindow()
    {
        _windowSubclass = WindowSubclassProc;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        Title = "A-Note";
        Activated += OnActivated;
        Closed += OnClosed;
        Content.KeyDown += OnKeyDown;
    }

    private static Task AnimateElementAsync(UIElement element, double fromOpacity, double toOpacity, double fromX, double toX, double fromY, double toY, int durationMs)
    {
        var completion = new TaskCompletionSource<bool>();
        var transform = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = transform;
        element.Opacity = fromOpacity;
        transform.X = fromX;
        transform.Y = fromY;

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(durationMs));
        var opacity = new DoubleAnimation { From = fromOpacity, To = toOpacity, Duration = duration, EasingFunction = easing, EnableDependentAnimation = true };
        var x = new DoubleAnimation { From = fromX, To = toX, Duration = duration, EasingFunction = easing, EnableDependentAnimation = true };
        var y = new DoubleAnimation { From = fromY, To = toY, Duration = duration, EasingFunction = easing, EnableDependentAnimation = true };
        Storyboard.SetTarget(opacity, element);
        Storyboard.SetTargetProperty(opacity, nameof(UIElement.Opacity));
        Storyboard.SetTarget(x, transform);
        Storyboard.SetTargetProperty(x, nameof(TranslateTransform.X));
        Storyboard.SetTarget(y, transform);
        Storyboard.SetTargetProperty(y, nameof(TranslateTransform.Y));

        var storyboard = new Storyboard();
        storyboard.Children.Add(opacity);
        storyboard.Children.Add(x);
        storyboard.Children.Add(y);
        storyboard.Completed += (_, _) =>
        {
            element.Opacity = toOpacity;
            transform.X = toX;
            transform.Y = toY;
            storyboard.Stop();
            completion.TrySetResult(true);
        };
        storyboard.Begin();
        return completion.Task;
    }

    private async Task SetMainContentAnimatedAsync(UIElement next, bool forward, bool animateExisting = true)
    {
        var current = MainContent.Content as UIElement;
        var firstPresentation = current is null;

        if (!firstPresentation && animateExisting && current is not null)
        {
            var exitX = forward ? -18d : 18d;
            await AnimateElementAsync(current, 1, 0, 0, exitX, 0, 0, 115);
        }

        MainContent.Content = next;

        if (firstPresentation)
        {
            if (!_initialEntrancePlayed)
            {
                _initialEntrancePlayed = true;
                next.Opacity = 1;
                TitleBar.Opacity = 0;

                var stages = _pendingInitialEntranceStages?.Where(stage => stage is not null).ToList() ?? [];
                _pendingInitialEntranceStages = null;
                foreach (var stage in stages)
                {
                    stage.Opacity = 0;
                    stage.RenderTransform = new TranslateTransform { Y = 18 };
                }

                // Keep the staged launch, but tighten the whole reveal to roughly one second.
                // Brand appears first, then heading, controls, and content overlap smoothly.
                var titleTask = AnimateElementAsync(TitleBar, 0, 1, 0, 0, -8, 0, 360);
                await Task.Delay(90);

                var stageTasks = new List<Task>();
                for (var i = 0; i < stages.Count; i++)
                {
                    if (i > 0) await Task.Delay(145);
                    var duration = i == stages.Count - 1 ? 520 : 440;
                    stageTasks.Add(AnimateElementAsync(stages[i], 0, 1, 0, 0, 14, 0, duration));
                }

                if (stageTasks.Count == 0)
                    stageTasks.Add(AnimateElementAsync(next, 0, 1, 0, 0, 14, 0, 900));

                await Task.WhenAll(stageTasks.Append(titleTask));
                return;
            }

            await AnimateElementAsync(next, 0, 1, 0, 0, 12, 0, 360);
            return;
        }

        if (!animateExisting)
        {
            next.Opacity = 1;
            return;
        }

        var enterX = forward ? 22d : -22d;
        await AnimateElementAsync(next, 0, 1, enterX, 0, 0, 0, 220);
    }

    private async void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_appWindow is not null) return;
        var hwnd = WindowNative.GetWindowHandle(this);
        if (!_windowSubclassInstalled)
            _windowSubclassInstalled = SetWindowSubclass(hwnd, _windowSubclass, new UIntPtr(1), UIntPtr.Zero);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "logo", "A-Note.ico");
            if (File.Exists(iconPath)) _appWindow.SetIcon(iconPath);
        }
        catch { }
        await _db.InitializeAsync();
        await LoadSettingsAsync();
        RestoreWindow();
        // Always start on the notebook library. LastNotebookId is still kept for compatibility,
        // but startup intentionally never resumes directly into an editor.
        await ShowLibraryAsync();
    }

    private async Task LoadSettingsAsync()
    {
        var json = await _db.GetSettingAsync("app");
        if (json is not null)
            try { _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new(); } catch (JsonException) { }
    }

    private void RestoreWindow()
    {
        if (_appWindow is null) return;
        _appWindow.MoveAndResize(new RectInt32(_settings.X, _settings.Y, Math.Max(1100, _settings.Width), Math.Max(700, _settings.Height)));
        if (_appWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        if (_closing) return;
        _closing = true;
        await FlushPagesAsync();
        if (_appWindow is not null)
        {
            var p = _appWindow.Position; var s = _appWindow.Size;
            _settings.X = p.X; _settings.Y = p.Y; _settings.Width = s.Width; _settings.Height = s.Height;
            _settings.Maximized = _appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        }
        _settings.LastNotebookId = _currentNotebook?.Id;
        await _db.SetSettingAsync("app", JsonSerializer.Serialize(_settings));
    }

    private async Task ShowLibraryAsync(string? search = null)
    {
        var returningFromEditor = _currentNotebook is not null;
        await FlushPagesAsync();
        _currentNotebook = null;
        _pageViews.Clear();
        TitleBarGestureTarget.Visibility = Visibility.Collapsed;
        SetTitleBar(TitleBar);
        TitleContext.Text = "NOTEBOOKS";
        TitleContext.Foreground = Brush("#92928C");
        if (TitleBarDragTarget.Children.OfType<Border>().FirstOrDefault() is Border libraryBrandSquare)
            libraryBrandSquare.Background = Brush("#FF7A18");
        if (search is not null) _searchQuery = search;

        var outer = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        var root = new Grid { Padding = new Thickness(0, 52, 0, 48), MaxWidth = 1500, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(56, 0, 56, 0) };
        outer.SizeChanged += (_, e) =>
        {
            var gutter = e.NewSize.Width switch { < 1260 => 28, < 1700 => 40, _ => 56 };
            root.Margin = new Thickness(gutter, 0, gutter, 0);
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid { Margin = new Thickness(0, 0, 0, 34), HorizontalAlignment = HorizontalAlignment.Stretch };
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel { Spacing = 7, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 30) };
        heading.Children.Add(new TextBlock { Text = "Your notebooks", FontFamily = (FontFamily)Application.Current.Resources["DisplayFontFamily"], FontSize = 42, FontWeight = Microsoft.UI.Text.FontWeights.Bold, CharacterSpacing = -15, TextAlignment = TextAlignment.Center });
        heading.Children.Add(new TextBlock { Text = "A focused space for every subject.", Foreground = Brush("#92928C"), FontSize = 15, TextAlignment = TextAlignment.Center });
        header.Children.Add(heading);
        var actions = new Grid { ColumnSpacing = 16, HorizontalAlignment = HorizontalAlignment.Stretch };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        Grid.SetRow(actions, 1);
        var searchBox = _searchBox = new NoClearTextBox
        {
            PlaceholderText = "Search notebooks",
            Text = _searchQuery,
            Height = 48,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush("#F4F4F2"),
            SelectionHighlightColor = Brush("#FF7A18"),
            VerticalContentAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources[typeof(TextBox)]
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(searchBox, "Search notebooks");
        var searchLayout = new Grid { ColumnSpacing = 8 };
        searchLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var searchIconScale = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
        var searchIcon = new FontIcon
        {
            Glyph = "\uE721",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 18,
            Foreground = Brush("#FF7A18"),
            RenderTransform = searchIconScale,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5)
        };
        var searchIconButton = new Button
        {
            Content = searchIcon,
            Width = 48,
            Height = 48,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            AllowFocusOnInteraction = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(searchIconButton, "Search notebooks");
        void AnimateIcon(ScaleTransform transform, double scale)
        {
            var duration = new Duration(TimeSpan.FromMilliseconds(100));
            var scaleX = new DoubleAnimation { To = scale, Duration = duration, EnableDependentAnimation = true };
            var scaleY = new DoubleAnimation { To = scale, Duration = duration, EnableDependentAnimation = true };
            Storyboard.SetTarget(scaleX, transform);
            Storyboard.SetTarget(scaleY, transform);
            Storyboard.SetTargetProperty(scaleX, nameof(ScaleTransform.ScaleX));
            Storyboard.SetTargetProperty(scaleY, nameof(ScaleTransform.ScaleY));
            var animation = new Storyboard();
            animation.Children.Add(scaleX);
            animation.Children.Add(scaleY);
            animation.Begin();
        }
        searchIconButton.PointerEntered += (_, _) => AnimateIcon(searchIconScale, 1.18);
        searchIconButton.PointerExited += (_, _) => AnimateIcon(searchIconScale, 1);
        searchLayout.Children.Add(searchIconButton);
        Grid.SetColumn(searchBox, 1); searchLayout.Children.Add(searchBox);
        var clearIconScale = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
        var clearIcon = new FontIcon
        {
            Glyph = "\uE711",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 16,
            Foreground = Brush("#FF7A18"),
            RenderTransform = clearIconScale,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5)
        };
        var clearSearchButton = new Button
        {
            Content = clearIcon,
            Width = 48,
            Height = 48,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            AllowFocusOnInteraction = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = string.IsNullOrEmpty(_searchQuery) ? Visibility.Collapsed : Visibility.Visible
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(clearSearchButton, "Clear search");
        clearSearchButton.PointerEntered += (_, _) => AnimateIcon(clearIconScale, 1.18);
        clearSearchButton.PointerExited += (_, _) => AnimateIcon(clearIconScale, 1);
        clearSearchButton.Click += (_, _) =>
        {
            searchBox.Text = "";
        };
        Grid.SetColumn(clearSearchButton, 2);
        searchLayout.Children.Add(clearSearchButton);
        var searchSurface = new Border { Child = searchLayout, Height = 52, Background = Brush("#111110"), BorderBrush = Brush("#2D2D2A"), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(3) };
        searchBox.GotFocus += (_, _) =>
        {
            searchSurface.BorderBrush = Brush("#FF7A18");
        };
        searchBox.LostFocus += (_, _) =>
        {
            searchSurface.BorderBrush = Brush("#2D2D2A");
        };
        async Task ExecuteSearchAsync()
        {
            _searchQuery = searchBox.Text;
            ++_searchRevision;
            await RefreshNotebookResultsAsync(_searchQuery);
        }
        searchIconButton.Click += async (_, _) => await ExecuteSearchAsync();
        searchBox.KeyDown += async (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            await ExecuteSearchAsync();
        };
        searchBox.TextChanged += async (_, _) =>
        {
            _searchQuery = searchBox.Text;
            clearSearchButton.Visibility = string.IsNullOrEmpty(_searchQuery) ? Visibility.Collapsed : Visibility.Visible;
            var revision = ++_searchRevision;
            await Task.Delay(90);
            if (revision == _searchRevision) await RefreshNotebookResultsAsync(_searchQuery);
        };
        actions.Children.Add(searchSurface);

        var filterButton = _filterButton = new Button
        {
            Content = "Filter",
            Height = 52,
            Width = 150,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsTabStop = false,
            Background = Brush("#111110"),
            BorderBrush = Brush("#2D2D2A"),
            BorderThickness = new Thickness(2),
            Foreground = Brush("#FF7A18"),
            CornerRadius = new CornerRadius(3),
            Style = FlatButtonStyle()
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(filterButton, "Filter notebooks");

        // A-Note hover: scale only. The flat template deliberately has no system hover wash.
        var filterScale = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
        filterButton.RenderTransform = filterScale;
        filterButton.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        void AnimateFilterScale(double target)
        {
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            var x = new DoubleAnimation { To = target, Duration = new Duration(TimeSpan.FromMilliseconds(115)), EasingFunction = easing };
            var y = new DoubleAnimation { To = target, Duration = new Duration(TimeSpan.FromMilliseconds(115)), EasingFunction = easing };
            Storyboard.SetTarget(x, filterScale);
            Storyboard.SetTarget(y, filterScale);
            Storyboard.SetTargetProperty(x, "ScaleX");
            Storyboard.SetTargetProperty(y, "ScaleY");
            var storyboard = new Storyboard();
            storyboard.Children.Add(x);
            storyboard.Children.Add(y);
            storyboard.Begin();
        }
        filterButton.PointerEntered += (_, _) => AnimateFilterScale(1.05);
        filterButton.PointerExited += (_, _) => AnimateFilterScale(1.0);
        filterButton.PointerPressed += (_, _) => AnimateFilterScale(0.985);
        filterButton.PointerReleased += (_, _) => AnimateFilterScale(1.05);
        filterButton.PointerCanceled += (_, _) => AnimateFilterScale(1.0);

        var filterFlyout = _libraryFilterFlyout = new Flyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
            ShouldConstrainToRootBounds = true,
            FlyoutPresenterStyle = (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
                <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="FlyoutPresenter">
                    <Setter Property="Padding" Value="0"/>
                    <Setter Property="Background" Value="Transparent"/>
                    <Setter Property="BorderThickness" Value="0"/>
                    <Setter Property="CornerRadius" Value="0"/>
                </Style>
                """)
        };
        var filterPanel = new StackPanel { Spacing = 2, MinWidth = 240 };
        var filterContent = new Border
        {
            Background = Brush("#111110"),
            BorderBrush = Brush("#2D2D2A"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(9),
            Child = filterPanel
        };
        filterFlyout.Content = filterContent;

        filterPanel.Children.Add(new TextBlock
        {
            Text = "SORT",
            Foreground = Brush("#92928C"),
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            CharacterSpacing = 110,
            Margin = new Thickness(12, 6, 12, 7)
        });

        void ApplyFilterChoiceVisual(Button choice, bool active, bool animate)
        {
            choice.Background = active ? Brush("#1D1D1B") : new SolidColorBrush(Colors.Transparent);
            choice.Foreground = active ? Brush("#FF7A18") : Brush("#F4F4F2");
            if (!animate || !active) return;

            choice.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            var transform = choice.RenderTransform as ScaleTransform ?? new ScaleTransform();
            choice.RenderTransform = transform;
            transform.ScaleX = 0.97;
            transform.ScaleY = 0.97;
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            var storyboard = new Storyboard();
            foreach (var property in new[] { nameof(ScaleTransform.ScaleX), nameof(ScaleTransform.ScaleY) })
            {
                var animation = new DoubleAnimation
                {
                    To = 1,
                    Duration = new Duration(TimeSpan.FromMilliseconds(140)),
                    EasingFunction = easing,
                    EnableDependentAnimation = true
                };
                Storyboard.SetTarget(animation, transform);
                Storyboard.SetTargetProperty(animation, property);
                storyboard.Children.Add(animation);
            }
            storyboard.Begin();
        }

        Button FilterChoice(string label, Func<Task> action, Func<bool>? isActive = null, double leftPadding = 14, bool animateInitial = false)
        {
            var active = isActive?.Invoke() == true;
            var choice = new Button
            {
                Content = label,
                Height = 36,
                Padding = new Thickness(leftPadding, 0, 14, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Foreground = Brush("#F4F4F2"),
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                IsTabStop = false,
                CornerRadius = new CornerRadius(3),
                Style = FlatButtonStyle()
            };
            choice.PointerEntered += (_, _) => choice.Foreground = Brush("#FF7A18");
            choice.PointerExited += (_, _) => ApplyFilterChoiceVisual(choice, isActive?.Invoke() == true, false);
            choice.Click += async (_, _) =>
            {
                await action();
                ApplyFilterChoiceVisual(choice, isActive?.Invoke() == true, false);
            };
            ApplyFilterChoiceVisual(choice, active, animateInitial);
            return choice;
        }

        Button defaultChoice = null!;
        Button alphabeticalChoice = null!;
        Button favoritesChoice = null!;
        void RefreshTopFilterVisuals()
        {
            ApplyFilterChoiceVisual(defaultChoice, _notebookSortMode == NotebookSortMode.Default, false);
            ApplyFilterChoiceVisual(alphabeticalChoice, _notebookSortMode == NotebookSortMode.Alphabetical, false);
            ApplyFilterChoiceVisual(favoritesChoice, _favoritesOnly, false);
        }

        defaultChoice = FilterChoice("Default", async () =>
        {
            var changed = _notebookSortMode != NotebookSortMode.Default;
            _notebookSortMode = NotebookSortMode.Default;
            RefreshTopFilterVisuals();
            if (changed) ApplyFilterChoiceVisual(defaultChoice, true, true);
            UpdateFilterButtonLabel();
            await RefreshNotebookResultsAsync(_searchQuery);
        }, () => _notebookSortMode == NotebookSortMode.Default);
        filterPanel.Children.Add(defaultChoice);
        alphabeticalChoice = FilterChoice("Alphabetical  A–Z", async () =>
        {
            var changed = _notebookSortMode != NotebookSortMode.Alphabetical;
            _notebookSortMode = NotebookSortMode.Alphabetical;
            RefreshTopFilterVisuals();
            if (changed) ApplyFilterChoiceVisual(alphabeticalChoice, true, true);
            UpdateFilterButtonLabel();
            await RefreshNotebookResultsAsync(_searchQuery);
        }, () => _notebookSortMode == NotebookSortMode.Alphabetical);
        filterPanel.Children.Add(alphabeticalChoice);

        filterPanel.Children.Add(new Border { Height = 1, Background = Brush("#2D2D2A"), Margin = new Thickness(8, 7, 8, 5) });
        filterPanel.Children.Add(new TextBlock
        {
            Text = "FAVORITES",
            Foreground = Brush("#92928C"),
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            CharacterSpacing = 110,
            Margin = new Thickness(12, 5, 12, 4)
        });
        favoritesChoice = FilterChoice("Favorites", async () =>
        {
            _favoritesOnly = !_favoritesOnly;
            RefreshTopFilterVisuals();
            if (_favoritesOnly) ApplyFilterChoiceVisual(favoritesChoice, true, true);
            UpdateFilterButtonLabel();
            await RefreshNotebookResultsAsync(_searchQuery);
        }, () => _favoritesOnly);
        filterPanel.Children.Add(favoritesChoice);

        filterPanel.Children.Add(new Border { Height = 1, Background = Brush("#2D2D2A"), Margin = new Thickness(8, 7, 8, 5) });
        filterPanel.Children.Add(new TextBlock
        {
            Text = "COLOR",
            Foreground = Brush("#92928C"),
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            CharacterSpacing = 110,
            Margin = new Thickness(12, 5, 12, 4)
        });

        var colorPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(12, 4, 12, 8)
        };
        var colorScroller = new ScrollViewer
        {
            Content = colorPanel,
            MaxWidth = 280,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled
        };
        filterPanel.Children.Add(colorScroller);

        filterPanel.Children.Add(new Border { Height = 1, Background = Brush("#2D2D2A"), Margin = new Thickness(8, 7, 8, 5) });
        filterPanel.Children.Add(new TextBlock
        {
            Text = "SUBJECT",
            Foreground = Brush("#92928C"),
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            CharacterSpacing = 110,
            Margin = new Thickness(12, 5, 12, 4)
        });

        var subjectPanel = new StackPanel { Spacing = 3 };
        var subjectScroller = new ScrollViewer
        {
            Content = subjectPanel,
            MaxHeight = 132,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            Padding = new Thickness(0)
        };
        filterPanel.Children.Add(subjectScroller);

        async Task ReloadDynamicFilterChoicesAsync(
            bool animateColorSelection = false,
            string? animatedColor = null,
            bool animateSubjectSelection = false,
            string? animatedSubject = null)
        {
            var allNotebooks = await _db.GetNotebooksAsync();

            colorPanel.Children.Clear();

            Button MakeColorSwatch(string? accent, bool selected, string tooltip)
            {
                var swatch = new Button
                {
                    Width = 34,
                    Height = 34,
                    MinWidth = 0,
                    MinHeight = 0,
                    Padding = new Thickness(0),
                    IsTabStop = false,
                    CornerRadius = new CornerRadius(4),
                    Background = accent is null ? Brush("#111110") : Brush(accent),
                    BorderBrush = selected ? Brush("#F4F4F2") : Brush("#444440"),
                    BorderThickness = new Thickness(selected ? 3 : 1.5),
                    Style = FlatButtonStyle(),
                    Content = accent is null ? EmptyColorIndicator(selected) : null
                };
                ToolTipService.SetToolTip(swatch, tooltip);
                var isAnimatedColor = animateColorSelection && selected &&
                    string.Equals(accent, animatedColor, StringComparison.OrdinalIgnoreCase);
                if (isAnimatedColor)
                {
                    swatch.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
                    var scale = new ScaleTransform { ScaleX = 0.88, ScaleY = 0.88 };
                    swatch.RenderTransform = scale;
                    var animation = new Storyboard();
                    foreach (var property in new[] { nameof(ScaleTransform.ScaleX), nameof(ScaleTransform.ScaleY) })
                    {
                        var grow = new DoubleAnimation
                        {
                            To = 1,
                            Duration = new Duration(TimeSpan.FromMilliseconds(140)),
                            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                            EnableDependentAnimation = true
                        };
                        Storyboard.SetTarget(grow, scale);
                        Storyboard.SetTargetProperty(grow, property);
                        animation.Children.Add(grow);
                    }
                    animation.Begin();
                }
                swatch.Click += async (_, _) =>
                {
                    var changed = !string.Equals(_colorFilter, accent, StringComparison.OrdinalIgnoreCase);
                    _colorFilter = accent;
                    UpdateFilterButtonLabel();
                    await RefreshNotebookResultsAsync(_searchQuery);
                    await ReloadDynamicFilterChoicesAsync(changed, accent);
                };
                return swatch;
            }

            static Grid EmptyColorIndicator(bool selected)
            {
                var mark = new Grid { Width = 34, Height = 34, Margin = new Thickness(-3), IsHitTestVisible = false };
                mark.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
                {
                    // Both states meet the rounded border at the same endpoints. Selection
                    // changes only visual weight/contrast, never the line's geometry.
                    X1 = 2,
                    Y1 = 32,
                    X2 = 32,
                    Y2 = 2,
                    Stroke = selected ? Brush("#F4F4F2") : Brush("#5A5A56"),
                    StrokeThickness = selected ? 3 : 1.5,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                });
                return mark;
            }

            colorPanel.Children.Add(MakeColorSwatch(null, string.IsNullOrWhiteSpace(_colorFilter), "All colors"));
            var colors = allNotebooks
                .Select(n => n.Accent?.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var accent in colors)
            {
                var capturedAccent = accent;
                var selected = string.Equals(_colorFilter, capturedAccent, StringComparison.OrdinalIgnoreCase);
                colorPanel.Children.Add(MakeColorSwatch(capturedAccent, selected, capturedAccent));
            }

            subjectPanel.Children.Clear();
            subjectPanel.Children.Add(FilterChoice("All subjects", async () =>
            {
                var changed = !string.IsNullOrWhiteSpace(_subjectFilter);
                _subjectFilter = null;
                UpdateFilterButtonLabel();
                await RefreshNotebookResultsAsync(_searchQuery);
                await ReloadDynamicFilterChoicesAsync(false, null, changed, null);
            }, () => string.IsNullOrWhiteSpace(_subjectFilter), 22,
                animateSubjectSelection && string.IsNullOrWhiteSpace(animatedSubject)));

            var subjects = allNotebooks
                .Select(n => n.Subject?.Trim())
                .Where(subject => !string.IsNullOrWhiteSpace(subject))
                .Select(subject => subject!)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(subject => subject, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            foreach (var subjectName in subjects)
            {
                var capturedSubject = subjectName;
                subjectPanel.Children.Add(FilterChoice(capturedSubject, async () =>
                {
                    var changed = !string.Equals(_subjectFilter, capturedSubject, StringComparison.CurrentCultureIgnoreCase);
                    _subjectFilter = capturedSubject;
                    UpdateFilterButtonLabel();
                    await RefreshNotebookResultsAsync(_searchQuery);
                    await ReloadDynamicFilterChoicesAsync(false, null, changed, capturedSubject);
                }, () => string.Equals(_subjectFilter, capturedSubject, StringComparison.CurrentCultureIgnoreCase), 22,
                    animateSubjectSelection && string.Equals(animatedSubject, capturedSubject, StringComparison.CurrentCultureIgnoreCase)));
            }
        }

        await ReloadDynamicFilterChoicesAsync();
        filterFlyout.Opening += async (_, _) => await ReloadDynamicFilterChoicesAsync();

        filterButton.Flyout = filterFlyout;
        UpdateFilterButtonLabel();
        Grid.SetColumn(filterButton, 1);
        actions.Children.Add(filterButton);

        var createButton = ActionButton("✚  New notebook", async (_, _) => await CreateNotebookAsync(), true);
        createButton.Height = 52;
        createButton.Width = 220;
        createButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(createButton, 2); actions.Children.Add(createButton);
        header.Children.Add(actions); root.Children.Add(header);

        var list = _libraryList = new ListView
        {
            ItemsSource = _notebooks,
            SelectionMode = ListViewSelectionMode.None,
            Background = new SolidColorBrush(Colors.Transparent),
            IsItemClickEnabled = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            CanDragItems = false,
            CanReorderItems = false,
            ReorderMode = ListViewReorderMode.Disabled,
            AllowDrop = false,
            ItemContainerTransitions = new TransitionCollection(),
            ManipulationMode = ManipulationModes.System
        };
        list.Padding = new Thickness(0);
        list.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        list.SetValue(ScrollViewer.HorizontalScrollModeProperty, ScrollMode.Disabled);
        // Keep WinUI's native scrolling/direct manipulation but hide its default bar.
        // A-Note draws its own rail beside the cards so content never sits underneath it.
        list.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        list.SetValue(ScrollViewer.VerticalScrollModeProperty, ScrollMode.Enabled);
        list.SetValue(ScrollViewer.IsDeferredScrollingEnabledProperty, false);
        list.ItemsPanel = (ItemsPanelTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load($"""
          <ItemsPanelTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            <StackPanel Orientation="Vertical" Spacing="{NotebookGap}"/>
          </ItemsPanelTemplate>
        """);
        list.ItemContainerStyle = (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
          <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ListViewItem">
            <Setter Property="Padding" Value="0"/><Setter Property="Margin" Value="0"/><Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="VerticalContentAlignment" Value="Stretch"/><Setter Property="Background" Value="Transparent"/><Setter Property="MinHeight" Value="0"/>
            <Setter Property="IsTabStop" Value="False"/><Setter Property="UseSystemFocusVisuals" Value="False"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListViewItem"><ContentPresenter Content="{TemplateBinding Content}" ContentTemplate="{TemplateBinding ContentTemplate}" HorizontalAlignment="Stretch" VerticalAlignment="Stretch"/></ControlTemplate></Setter.Value></Setter>
          </Style>
        """);
        list.ItemTemplate = NotebookTemplate();
        list.ContainerContentChanging += (_, e) =>
        {
            if (!e.InRecycleQueue && e.ItemContainer?.ContentTemplateRoot is Border card && e.Item is Notebook notebook)
            {
                WireNotebookCard(card, notebook);
            }
        };
        // Reserve a dedicated column for the scrollbar so notebook cards never overlap it.
        var listHost = new Grid { ColumnSpacing = 14 };
        listHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        listHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        listHost.Children.Add(list);
        // A filter flyout normally installs a light-dismiss layer that consumes the first
        // pointer press outside it. Let the notebook surface receive input directly so every
        // visible card button remains actionable while the live filter flyout is open.
        filterFlyout.OverlayInputPassThroughElement = listHost;
        listHost.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Height <= 0 || e.NewSize.Width <= 0) return;
            var heightChanged = Math.Abs(_notebookViewportHeight - e.NewSize.Height) >= 0.5;
            var widthChanged = Math.Abs(_notebookViewportWidth - e.NewSize.Width) >= 0.5;
            if (!heightChanged && !widthChanged) return;
            _notebookViewportHeight = e.NewSize.Height;
            _notebookViewportWidth = e.NewSize.Width;
            UpdateNotebookCardHeights();
        };

        var scrollTrack = _libraryScrollTrack = new Grid
        {
            Width = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Colors.Transparent),
            Visibility = Visibility.Collapsed
        };
        Grid.SetColumn(scrollTrack, 1);
        var trackBackground = new Border
        {
            Width = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brush("#141413"),
            BorderBrush = Brush("#2D2D2A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            IsHitTestVisible = false
        };
        var scrollThumb = new Border
        {
            Width = 8,
            Height = 48,
            Background = Brush("#FF7A18"),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var thumbLayer = new Canvas { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        thumbLayer.Children.Add(scrollThumb);
        scrollTrack.Children.Add(trackBackground);
        scrollTrack.Children.Add(thumbLayer);
        listHost.Children.Add(scrollTrack);

        ScrollViewer? libraryScrollViewer = null;
        bool thumbDragging = false;
        uint thumbPointerId = 0;
        double thumbDragStartY = 0;
        double thumbDragStartTop = 0;

        void SyncNotebookScrollThumb()
        {
            if (libraryScrollViewer is null || scrollTrack.ActualHeight <= 0) return;
            var viewport = Math.Max(1, libraryScrollViewer.ViewportHeight);
            var extent = Math.Max(viewport, libraryScrollViewer.ExtentHeight);
            var scrollable = Math.Max(0, extent - viewport);
            var trackHeight = scrollTrack.ActualHeight;
            var thumbHeight = scrollable <= 0 ? trackHeight : Math.Max(44, trackHeight * (viewport / extent));
            thumbHeight = Math.Min(trackHeight, thumbHeight);
            if (Math.Abs(scrollThumb.Height - thumbHeight) > 0.25)
                scrollThumb.Height = thumbHeight;
            // Keep the complete rail out of the visual and hit-test trees when there are no
            // results. The list can be collapsed before its Loaded/LayoutUpdated events run,
            // so the track starts collapsed and RefreshNotebookResultsAsync also updates it.
            var railVisibility = _notebooks.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (scrollTrack.Visibility != railVisibility)
                scrollTrack.Visibility = railVisibility;
            var travel = Math.Max(0, trackHeight - thumbHeight);
            var top = scrollable <= 0 ? 0 : (libraryScrollViewer.VerticalOffset / scrollable) * travel;
            Canvas.SetLeft(scrollThumb, 2);
            top = Math.Clamp(top, 0, travel);
            var currentTop = Canvas.GetTop(scrollThumb);
            if (double.IsNaN(currentTop) || Math.Abs(currentTop - top) > 0.25)
                Canvas.SetTop(scrollThumb, top);
        }

        list.Loaded += (_, _) =>
        {
            libraryScrollViewer = Descendants<ScrollViewer>(list).FirstOrDefault();
            if (libraryScrollViewer is null) return;
            libraryScrollViewer.ViewChanged += (_, _) => SyncNotebookScrollThumb();
            _notebookViewportHeight = listHost.ActualHeight;
            _notebookViewportWidth = listHost.ActualWidth;
            RefreshRealizedNotebookCards();
            SyncNotebookScrollThumb();
        };
        // Extent changes caused by filtering do not necessarily raise ViewChanged. Keep the
        // custom indicator synchronized after WinUI completes each collection layout pass.
        list.LayoutUpdated += (_, _) => SyncNotebookScrollThumb();
        scrollTrack.SizeChanged += (_, _) => SyncNotebookScrollThumb();

        scrollThumb.PointerPressed += (_, e) =>
        {
            if (libraryScrollViewer is null) return;
            thumbDragging = true;
            thumbPointerId = e.Pointer.PointerId;
            var point = e.GetCurrentPoint(scrollTrack);
            thumbDragStartY = point.Position.Y;
            thumbDragStartTop = Canvas.GetTop(scrollThumb);
            if (double.IsNaN(thumbDragStartTop)) thumbDragStartTop = 0;
            scrollThumb.CapturePointer(e.Pointer);
            e.Handled = true;
        };
        scrollThumb.PointerMoved += (_, e) =>
        {
            if (!thumbDragging || e.Pointer.PointerId != thumbPointerId || libraryScrollViewer is null) return;
            var point = e.GetCurrentPoint(scrollTrack);
            var travel = Math.Max(0, scrollTrack.ActualHeight - scrollThumb.ActualHeight);
            if (travel <= 0) return;
            var top = Math.Clamp(thumbDragStartTop + point.Position.Y - thumbDragStartY, 0, travel);
            var scrollable = Math.Max(0, libraryScrollViewer.ExtentHeight - libraryScrollViewer.ViewportHeight);
            libraryScrollViewer.ChangeView(null, scrollable * (top / travel), null, true);
            e.Handled = true;
        };
        void EndThumbDrag(PointerRoutedEventArgs e)
        {
            if (!thumbDragging || e.Pointer.PointerId != thumbPointerId) return;
            thumbDragging = false;
            scrollThumb.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
        scrollThumb.PointerReleased += (_, e) => EndThumbDrag(e);
        scrollThumb.PointerCanceled += (_, e) => EndThumbDrag(e);
        scrollThumb.PointerCaptureLost += (_, _) => thumbDragging = false;
        scrollTrack.PointerPressed += (_, e) =>
        {
            if (libraryScrollViewer is null || FindAncestor<Border>(e.OriginalSource as DependencyObject) == scrollThumb) return;
            var travel = Math.Max(0, scrollTrack.ActualHeight - scrollThumb.ActualHeight);
            var scrollable = Math.Max(0, libraryScrollViewer.ExtentHeight - libraryScrollViewer.ViewportHeight);
            if (travel <= 0 || scrollable <= 0) return;
            var y = e.GetCurrentPoint(scrollTrack).Position.Y - (scrollThumb.ActualHeight / 2);
            var top = Math.Clamp(y, 0, travel);
            libraryScrollViewer.ChangeView(null, scrollable * (top / travel), null, true);
            e.Handled = true;
        };

        Grid.SetRow(listHost, 1); root.Children.Add(listHost);

        _libraryEmptyTitle = new TextBlock { FontFamily = (FontFamily)Application.Current.Resources["DisplayFontFamily"], FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = Brush("#F4F4F2"), TextAlignment = TextAlignment.Center };
        _libraryEmptyDetail = new TextBlock { FontSize = 14, Foreground = Brush("#92928C"), TextAlignment = TextAlignment.Center };
        _libraryEmptyState = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _libraryEmptyState.Children.Add(_libraryEmptyTitle);
        _libraryEmptyState.Children.Add(_libraryEmptyDetail);
        Grid.SetRow(_libraryEmptyState, 1); root.Children.Add(_libraryEmptyState);

        outer.Children.Add(root);

        // On a cold launch, reveal the library in deliberate stages instead of fading the
        // complete screen as one slab. Load the data first so notebook cards participate
        // in the final stage instead of popping in after the animation finishes.
        if (MainContent.Content is null && !_initialEntrancePlayed)
            _pendingInitialEntranceStages = [heading, actions, listHost];

        await RefreshNotebookResultsAsync(_searchQuery);
        await SetMainContentAnimatedAsync(outer, forward: false, animateExisting: returningFromEditor);
    }

    private async Task RefreshNotebookResultsAsync(string query)
    {
        var normalized = query.Trim();
        var results = await _db.GetNotebooksAsync(normalized);
        if (!string.Equals(query, _searchQuery, StringComparison.Ordinal)) return;

        IEnumerable<Notebook> filtered = results;
        if (_favoritesOnly)
            filtered = filtered.Where(n => n.IsFavorite);
        if (!string.IsNullOrWhiteSpace(_colorFilter))
            filtered = filtered.Where(n => string.Equals(n.Accent?.Trim(), _colorFilter, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(_subjectFilter))
            filtered = filtered.Where(n => string.Equals(n.Subject?.Trim(), _subjectFilter, StringComparison.CurrentCultureIgnoreCase));
        if (_notebookSortMode == NotebookSortMode.Alphabetical)
            filtered = filtered.OrderBy(n => n.Title, StringComparer.CurrentCultureIgnoreCase);
        var visibleResults = filtered.ToList();
        var imposedHeight = CalculateNotebookCardHeight();
        foreach (var notebook in visibleResults)
            notebook.CardHeight = imposedHeight;
        ApplyNotebookResults(visibleResults);
        // DataTemplate containers are recycled, and replacing an item does not reliably rerun
        // ContainerContentChanging. Re-apply per-notebook visuals after reconciliation so accent
        // colors, favorite state and the exact 3-card sizing always match the current objects.
        DispatcherQueue.TryEnqueue(() =>
        {
            _libraryList?.UpdateLayout();
            RefreshRealizedNotebookCards();
        });
        var empty = visibleResults.Count == 0;
        if (_libraryList is not null)
        {
            _libraryList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            _libraryList.CanDragItems = false;
            _libraryList.CanReorderItems = false;
            _libraryList.ReorderMode = ListViewReorderMode.Disabled;
            _libraryList.AllowDrop = false;
        }
        if (_libraryEmptyState is not null) _libraryEmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (_libraryScrollTrack is not null) _libraryScrollTrack.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (_libraryEmptyTitle is not null)
            _libraryEmptyTitle.Text = _favoritesOnly && string.IsNullOrEmpty(normalized)
                ? "No favorite notebooks yet"
                : string.IsNullOrEmpty(normalized) ? "No notebooks yet" : "No notebooks found";
        if (_libraryEmptyDetail is not null)
            _libraryEmptyDetail.Text = _favoritesOnly && string.IsNullOrEmpty(normalized)
                ? "Tap the heart on a notebook to keep it here."
                : string.IsNullOrEmpty(normalized) ? "Create your first notebook to get started." : $"Nothing matches “{normalized}”. Try another title or subject.";
    }

    private void RefreshRealizedNotebookCards()
    {
        if (_libraryList is null) return;
        for (var i = 0; i < _notebooks.Count; i++)
        {
            if (_libraryList.ContainerFromIndex(i) is not ListViewItem container ||
                container.ContentTemplateRoot is not Border card)
                continue;

            var notebook = _notebooks[i];
            card.DataContext = notebook;
            ApplyNotebookCardVisuals(card, notebook);
            ApplyNotebookCardHeight(card);
        }
    }

    private void ApplyNotebookResults(IReadOnlyList<Notebook> results)
    {
        // Most searches (especially pressing Enter / clicking the search icon with the
        // same query) return the exact same notebooks. Do not Clear()+Add() in that case:
        // it destroys/recreates every ListView container and replays its entrance visuals.
        if (_notebooks.Count == results.Count)
        {
            var identical = true;
            for (var i = 0; i < results.Count; i++)
            {
                if (!NotebookVisualStateEquals(_notebooks[i], results[i]))
                {
                    identical = false;
                    break;
                }
            }

            if (identical) return;
        }

        // Reconcile by notebook id instead of rebuilding the whole collection. Existing
        // visible cards keep their containers; only genuinely added/removed/moved/changed
        // notebooks are touched. This prevents already-visible cards from replaying their
        // intro animation when search is submitted again.
        for (var targetIndex = 0; targetIndex < results.Count; targetIndex++)
        {
            var incoming = results[targetIndex];
            var existingIndex = -1;
            for (var i = targetIndex; i < _notebooks.Count; i++)
            {
                if (string.Equals(_notebooks[i].Id, incoming.Id, StringComparison.Ordinal))
                {
                    existingIndex = i;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                _notebooks.Insert(targetIndex, incoming);
                continue;
            }

            if (existingIndex != targetIndex)
                _notebooks.Move(existingIndex, targetIndex);

            if (!NotebookVisualStateEquals(_notebooks[targetIndex], incoming))
                CopyNotebookVisualState(_notebooks[targetIndex], incoming);
        }

        while (_notebooks.Count > results.Count)
            _notebooks.RemoveAt(_notebooks.Count - 1);
    }

    private static bool NotebookVisualStateEquals(Notebook left, Notebook right) =>
        string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
        string.Equals(left.Title, right.Title, StringComparison.Ordinal) &&
        string.Equals(left.Subject, right.Subject, StringComparison.Ordinal) &&
        string.Equals(left.Accent, right.Accent, StringComparison.OrdinalIgnoreCase) &&
        left.IsFavorite == right.IsFavorite &&
        left.SortOrder == right.SortOrder &&
        left.ModifiedAt.Equals(right.ModifiedAt) &&
        left.PageCount == right.PageCount;

    private static void CopyNotebookVisualState(Notebook target, Notebook source)
    {
        // Keep the same object instance alive while filters move/reuse ListView containers.
        // This prevents a visible button from briefly pointing at an object that was replaced
        // during reconciliation. The card is refreshed explicitly after the collection settles.
        target.Title = source.Title;
        target.Subject = source.Subject;
        target.Accent = source.Accent;
        target.IsFavorite = source.IsFavorite;
        target.SortOrder = source.SortOrder;
        target.ModifiedAt = source.ModifiedAt;
        target.PageCount = source.PageCount;
        target.CardHeight = source.CardHeight;
    }

    private DataTemplate NotebookTemplate() => (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <Border Tag="CardRoot" Height="{Binding CardHeight}" Background="#111110" BorderBrush="#2D2D2A" BorderThickness="2" CornerRadius="4" Margin="0" Padding="32,18" MinHeight="0" HorizontalAlignment="Stretch">
            <Grid><Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
              <Border Tag="AccentBar" Width="8" Background="{Binding Accent}" CornerRadius="2" Margin="0,1,0,1"/>
              <StackPanel Tag="InfoPanel" Grid.Column="1" Margin="24,0,24,0" VerticalAlignment="Center" Spacing="4">
                <TextBlock Tag="TitleText" Text="{Binding Title}" FontFamily="{StaticResource DisplayFontFamily}" FontSize="28" FontWeight="Bold" Foreground="#F4F4F2" TextTrimming="CharacterEllipsis"/>
                <TextBlock Tag="SubjectText" Text="{Binding Subject}" FontSize="16" FontWeight="SemiBold" Foreground="#92928C" TextTrimming="CharacterEllipsis"/>
                <StackPanel Tag="MetaPanel" Orientation="Horizontal" Spacing="16" Margin="0,12,0,0">
                  <TextBlock Tag="PageMeta" Text="{Binding PageCountLabel}" FontSize="13" FontWeight="SemiBold" Foreground="#92928C"/>
                  <TextBlock Tag="ModifiedMeta" Text="{Binding ModifiedLabel}" FontSize="13" FontWeight="SemiBold" Foreground="#92928C"/>
                </StackPanel>
              </StackPanel>
              <StackPanel Tag="ActionsPanel" Grid.Column="2" Orientation="Horizontal" Spacing="10" VerticalAlignment="Center">
                <Button Tag="favorite" Width="62" MinWidth="0" Height="48" MinHeight="0" Padding="0" HorizontalContentAlignment="Center" VerticalContentAlignment="Center">
                  <FontIcon Glyph="♡" FontFamily="Segoe UI Symbol" FontSize="32" Foreground="#FF7A18" HorizontalAlignment="Center" VerticalAlignment="Center" RenderTransformOrigin="0.5,0.5"/>
                </Button>
                <Button Content="Edit" Tag="edit" MinWidth="82" Height="48" MinHeight="0" HorizontalContentAlignment="Center" VerticalContentAlignment="Center"/>
                <Button Content="Delete" Tag="delete" MinWidth="92" Height="48" MinHeight="0" HorizontalContentAlignment="Center" VerticalContentAlignment="Center"/>
                <Button Content="Open  ➜" Tag="open" Style="{StaticResource AccentButtonStyle}" MinWidth="124" Height="48" MinHeight="0" HorizontalContentAlignment="Center" VerticalContentAlignment="Center"/>
              </StackPanel>
            </Grid>
          </Border>
        </DataTemplate>
        """);

    private void WireNotebookCard(Border card, Notebook notebook)
    {
        card.DataContext = notebook;
        card.Height = notebook.CardHeight;
        // A recycled template can be wired before its descendants have completed layout.
        // Reapply the viewport-derived compact style at Loaded, including after filtering.
        card.Loaded -= NotebookCardLoaded;
        card.Loaded += NotebookCardLoaded;
        ApplyNotebookCardVisuals(card, notebook);
        if (notebook.CardHeight > 0)
            ApplyNotebookCardScale(card, notebook.CardHeight);
    }

    private void NotebookCardLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border card || card.DataContext is not Notebook notebook) return;
        ApplyNotebookCardVisuals(card, notebook);
        ApplyNotebookCardHeight(card);
    }

    private void ApplyNotebookCardVisuals(Border card, Notebook notebook)
    {
        var accentBar = GetAccentBar(card);
        if (accentBar is not null)
            accentBar.Background = Brush(NormalizeAccent(notebook.Accent));

        // Notebook is a lightweight POCO rather than INotifyPropertyChanged, so when a filter
        // reconciles an existing object in place, refresh the already-realized text explicitly.
        var titleText = Descendants<TextBlock>(card).FirstOrDefault(x => Equals(x.Tag, "TitleText"));
        if (titleText is not null) titleText.Text = notebook.Title;
        var subjectText = Descendants<TextBlock>(card).FirstOrDefault(x => Equals(x.Tag, "SubjectText"));
        if (subjectText is not null) subjectText.Text = notebook.Subject ?? string.Empty;
        var pageMeta = Descendants<TextBlock>(card).FirstOrDefault(x => Equals(x.Tag, "PageMeta"));
        if (pageMeta is not null) pageMeta.Text = notebook.PageCountLabel;
        var modifiedMeta = Descendants<TextBlock>(card).FirstOrDefault(x => Equals(x.Tag, "ModifiedMeta"));
        if (modifiedMeta is not null) modifiedMeta.Text = notebook.ModifiedLabel;

        foreach (var button in Descendants<Button>(card))
        {
            button.DataContext = notebook;
            // CommandParameter is a direct, non-inherited reference to the notebook currently
            // represented by this recycled visual. It is more reliable than waiting for
            // DataContext inheritance to settle after a filter changes the visible indices.
            button.CommandParameter = notebook;
            button.IsEnabled = true;
            button.IsHitTestVisible = true;

            // If the filter flyout is still open, hide it on press so its light-dismiss layer
            // cannot cancel the Button.Click that follows on release.
            button.PointerPressed -= NotebookCardButtonPointerPressed;
            button.PointerPressed += NotebookCardButtonPointerPressed;

            // ListView recycles card visuals. Rebind the Click handler every time a card is
            // realized, detaching first so recycled buttons never accumulate duplicate handlers.
            button.Click -= NotebookCardButtonClick;
            button.Click += NotebookCardButtonClick;

            if (Equals(button.Tag, "favorite"))
            {
                SetFavoriteButtonVisual(button, notebook.IsFavorite);
                ToolTipService.SetToolTip(button, notebook.IsFavorite ? "Remove from favorites" : "Add to favorites");
            }
        }
    }

    private void ApplyNotebookCardHeight(Border card)
    {
        var height = CalculateNotebookCardHeight();
        if (height <= 0) return;
        if (card.DataContext is Notebook notebook)
            notebook.CardHeight = height;
        card.Height = height;
        ApplyNotebookCardScale(card, height);
    }

    private double CalculateNotebookCardHeight()
    {
        var viewportHeight = _notebookViewportHeight;
        if (viewportHeight <= 0) viewportHeight = _libraryList?.ActualHeight ?? 0;
        return viewportHeight <= 0 ? 0 : Math.Max(1, (viewportHeight - (NotebookGap * 2)) / 3d);
    }

    private void ApplyNotebookCardScale(Border card, double height)
    {
        // Container widths are transient while ListView recycles/reconciles filtered items.
        // Scale from the stable outer viewport instead so filtering cannot resize card content.
        var width = _notebookViewportWidth > 0
            ? _notebookViewportWidth
            : (_libraryList?.ActualWidth > 0 ? _libraryList.ActualWidth : 1200d);
        var heightScale = Math.Clamp(height / 158d, 0.58, 1.05);
        var widthScale = Math.Clamp(width / 1180d, 0.62, 1.05);
        var scale = Math.Min(heightScale, widthScale);

        var horizontalPadding = Math.Clamp(28 * scale, 12, 32);
        var verticalPadding = Math.Clamp(15 * scale, 6, 18);
        card.Padding = new Thickness(horizontalPadding, verticalPadding, horizontalPadding, verticalPadding);

        var accentBar = GetAccentBar(card);
        if (accentBar is not null) accentBar.Width = Math.Clamp(8 * scale, 4, 8);

        var infoPanel = Descendants<StackPanel>(card).FirstOrDefault(x => Equals(x.Tag, "InfoPanel"));
        if (infoPanel is not null)
        {
            var sideMargin = Math.Clamp(20 * scale, 8, 24);
            infoPanel.Margin = new Thickness(sideMargin, 0, sideMargin, 0);
            infoPanel.Spacing = Math.Clamp(4 * scale, 1, 4);
        }

        var title = Descendants<TextBlock>(card).FirstOrDefault(x => Equals(x.Tag, "TitleText"));
        if (title is not null)
        {
            title.FontSize = Math.Clamp(28 * scale, 16, 29);
            title.MaxLines = 1;
        }
        var subject = Descendants<TextBlock>(card).FirstOrDefault(x => Equals(x.Tag, "SubjectText"));
        if (subject is not null)
        {
            subject.FontSize = Math.Clamp(16 * scale, 10.5, 17);
            subject.MaxLines = 1;
        }
        foreach (var meta in Descendants<TextBlock>(card).Where(x => Equals(x.Tag, "PageMeta") || Equals(x.Tag, "ModifiedMeta")))
        {
            meta.FontSize = Math.Clamp(13 * scale, 9, 13.5);
            meta.MaxLines = 1;
        }

        var metaPanel = Descendants<StackPanel>(card).FirstOrDefault(x => Equals(x.Tag, "MetaPanel"));
        if (metaPanel is not null)
        {
            metaPanel.Spacing = Math.Clamp(14 * scale, 5, 16);
            metaPanel.Margin = new Thickness(0, Math.Clamp(8 * scale, 3, 12), 0, 0);
        }

        var actions = Descendants<StackPanel>(card).FirstOrDefault(x => Equals(x.Tag, "ActionsPanel"));
        if (actions is not null) actions.Spacing = Math.Clamp(8 * scale, 4, 10);

        foreach (var button in Descendants<Button>(card))
        {
            var h = Math.Clamp(48 * scale, 31, 50);
            button.Height = h;
            button.MinHeight = 0;
            button.Padding = Equals(button.Tag, "favorite")
                ? new Thickness(0)
                : new Thickness(
                    Math.Clamp(12 * scale, 5, 18),
                    Math.Clamp(6 * scale, 2, 10),
                    Math.Clamp(12 * scale, 5, 18),
                    Math.Clamp(6 * scale, 2, 10));
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.VerticalContentAlignment = VerticalAlignment.Center;

            if (Equals(button.Tag, "favorite"))
            {
                button.Width = Math.Clamp(62 * scale, 40, 64);
                if (button.Content is FontIcon heart)
                    heart.FontSize = Math.Clamp(32 * scale, 22, 34);
            }
            else if (Equals(button.Tag, "edit"))
            {
                button.MinWidth = 0;
                button.Width = Math.Clamp(82 * scale, 56, 84);
                button.FontSize = Math.Clamp(14 * scale, 10, 15);
            }
            else if (Equals(button.Tag, "delete"))
            {
                button.MinWidth = 0;
                button.Width = Math.Clamp(92 * scale, 62, 94);
                button.FontSize = Math.Clamp(14 * scale, 10, 15);
            }
            else if (Equals(button.Tag, "open"))
            {
                button.MinWidth = 0;
                button.Width = Math.Clamp(124 * scale, 82, 128);
                button.FontSize = Math.Clamp(14 * scale, 10, 15);
            }
        }
    }

    private static void SetFavoriteButtonVisual(Button button, bool isFavorite)
    {
        if (button.Content is not FontIcon icon)
        {
            icon = new FontIcon
            {
                FontFamily = new FontFamily("Segoe UI Symbol"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            button.Content = icon;
        }

        icon.Glyph = isFavorite ? "♥" : "♡";
        icon.Foreground = Brush("#FF7A18");
        icon.RenderTransform = new TranslateTransform { Y = -1.25 };
        button.Foreground = Brush("#FF7A18");
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;
    }

    private static Border? GetAccentBar(Border card)
    {
        // The accent strip is the first child of the card grid. Access it directly instead of
        // relying on recycled-template tag discovery, which can lag behind DataContext updates.
        if (card.Child is Grid grid && grid.Children.Count > 0 && grid.Children[0] is Border direct)
            return direct;
        return Descendants<Border>(card).FirstOrDefault(x => Equals(x.Tag, "AccentBar"));
    }

    private static string NormalizeAccent(string? accent)
    {
        var value = accent?.Trim();
        if (string.IsNullOrWhiteSpace(value)) return "#FF7A18";
        if (!value.StartsWith('#')) value = $"#{value}";
        return value.Length is 7 or 9 ? value : "#FF7A18";
    }

    private void UpdateNotebookCardHeights()
    {
        if (_libraryList is null) return;
        var height = CalculateNotebookCardHeight();
        if (height <= 0) return;
        foreach (var notebook in _notebooks)
            notebook.CardHeight = height;

        for (var i = 0; i < _notebooks.Count; i++)
            if (_libraryList.ContainerFromIndex(i) is ListViewItem container && container.ContentTemplateRoot is Border card)
                ApplyNotebookCardHeight(card);
    }

    private void UpdateFilterButtonLabel()
    {
        if (_filterButton is null) return;
        var parts = new List<string>();
        if (_notebookSortMode == NotebookSortMode.Alphabetical) parts.Add("A–Z");
        if (_favoritesOnly) parts.Add("Favorites");
        if (!string.IsNullOrWhiteSpace(_colorFilter)) parts.Add("Color");
        if (!string.IsNullOrWhiteSpace(_subjectFilter)) parts.Add(CompactFilterLabel(_subjectFilter));
        _filterButton.Content = parts.Count == 0 ? "Filter" : $"Filter · {string.Join(" · ", parts)}";

        var details = new List<string>();
        if (_notebookSortMode == NotebookSortMode.Alphabetical) details.Add("Alphabetical A–Z");
        if (_favoritesOnly) details.Add("Favorites only");
        if (!string.IsNullOrWhiteSpace(_colorFilter)) details.Add($"Color: {_colorFilter}");
        if (!string.IsNullOrWhiteSpace(_subjectFilter)) details.Add($"Subject: {_subjectFilter}");
        ToolTipService.SetToolTip(_filterButton, details.Count == 0 ? "Filter notebooks" : string.Join(" • ", details));
    }

    private async Task ExecuteNotebookActionAsync(Button button, Notebook notebook, string action)
    {
        switch (action)
        {
            case "open":
                await OpenNotebookAsync(notebook);
                break;
            case "edit":
                await EditNotebookAsync(notebook);
                break;
            case "delete":
                await DeleteNotebookAsync(notebook);
                break;
            case "favorite":
                notebook.IsFavorite = !notebook.IsFavorite;
                SetFavoriteButtonVisual(button, notebook.IsFavorite);
                ToolTipService.SetToolTip(button, notebook.IsFavorite ? "Remove from favorites" : "Add to favorites");
                QueueFavoriteSave(notebook);
                if (_favoritesOnly)
                    await RefreshNotebookResultsAsync(_searchQuery);
                break;
        }
    }

    private void NotebookCardButtonPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // A live filter Flyout can otherwise leave the button in its pressed visual state while
        // the light-dismiss popup consumes/cancels the logical click. Dismiss before release.
        _libraryFilterFlyout?.Hide();
    }

    private async void NotebookCardButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string action)
            return;

        // CommandParameter is refreshed directly whenever a card visual is realized/reused.
        // Fall back to the card/DataContext only for older already-created visuals.
        var card = FindNotebookCard(button);
        var notebook = button.CommandParameter as Notebook
            ?? card?.DataContext as Notebook
            ?? button.DataContext as Notebook;
        if (notebook is null) return;

        await ExecuteNotebookActionAsync(button, notebook, action);
    }

    private void QueueFavoriteSave(Notebook notebook)
    {
        var revision = _favoriteSaveRevisions.TryGetValue(notebook.Id, out var current) ? current + 1 : 1;
        _favoriteSaveRevisions[notebook.Id] = revision;
        _ = PersistFavoriteAfterQuietPeriodAsync(notebook.Id, notebook.IsFavorite, revision);
    }

    private async Task PersistFavoriteAfterQuietPeriodAsync(string notebookId, bool isFavorite, int revision)
    {
        await Task.Delay(120);
        if (!_favoriteSaveRevisions.TryGetValue(notebookId, out var current) || current != revision) return;
        await _db.SetNotebookFavoriteAsync(notebookId, isFavorite);
        if (_favoriteSaveRevisions.TryGetValue(notebookId, out current) && current == revision)
            _favoriteSaveRevisions.Remove(notebookId);
    }

    private static string CompactFilterLabel(string value)
    {
        const int maxLength = 12;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : $"{trimmed[..(maxLength - 1)]}…";
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }

    private static Border? FindNotebookCard(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is Border { Tag: "CardRoot" } card) return card;
        return null;
    }

    private async Task CreateNotebookAsync()
    {
        var title = DialogTextBox("Enter a notebook title", maxLength: 60);
        var subject = DialogTextBox("Enter a subject", maxLength: 60);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(title, "Notebook title");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(subject, "Notebook subject");
        var accent = AccentPicker("#FF7A18");
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(Labeled("Title", title));
        panel.Children.Add(Labeled("Subject", subject));
        panel.Children.Add(Labeled("Notebook color", accent));
        var dialog = Dialog("New notebook", panel, "Create");
        dialog.PrimaryButtonClick += (_, args) => args.Cancel = !NotebookFieldsAreValid(title.Text, subject.Text, null);

        // Native ContentDialog availability is the source of truth. The AccentButtonStyle
        // has a custom Disabled state that looks like the outlined Cancel button, so there is
        // no style swapping race on first render. Re-evaluate on every character.
        void RefreshValidation() => UpdateNotebookDialogValidation(dialog, title, subject, null);
        title.TextChanged += (_, _) => RefreshValidation();
        subject.TextChanged += (_, _) => RefreshValidation();
        RefreshValidation(); // before ShowAsync: empty Create starts unavailable immediately
        dialog.Opened += (_, _) => RefreshValidation();

        if (await ShowCenteredDialogAsync(dialog) != ContentDialogResult.Primary) return;
        if (!NotebookFieldsAreValid(title.Text, subject.Text, null)) return;

        var allNotebooks = await _db.GetNotebooksAsync();
        var notebook = new Notebook
        {
            Title = title.Text.Trim(),
            Subject = subject.Text.Trim(),
            Accent = SelectedAccent(accent),
            SortOrder = allNotebooks.Count
        };
        await _db.SaveNotebookAsync(notebook);
        await OpenNotebookAsync(notebook);
    }

    private async Task EditNotebookAsync(Notebook notebook)
    {
        var title = DialogTextBox("Enter a notebook title", notebook.Title, 60);
        var subject = DialogTextBox("Enter a subject", notebook.Subject ?? "", 60);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(title, "Notebook title");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(subject, "Notebook subject");
        var accent = AccentPicker(notebook.Accent);
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(Labeled("Title", title));
        panel.Children.Add(Labeled("Subject", subject));
        panel.Children.Add(Labeled("Notebook color", accent));
        var dialog = Dialog("Edit notebook", panel, "Save");
        dialog.PrimaryButtonClick += (_, args) => args.Cancel = !NotebookFieldsAreValid(title.Text, subject.Text, notebook);

        void RefreshValidation() => UpdateNotebookDialogValidation(dialog, title, subject, notebook);
        title.TextChanged += (_, _) => RefreshValidation();
        subject.TextChanged += (_, _) => RefreshValidation();
        RefreshValidation();
        dialog.Opened += (_, _) => RefreshValidation();

        if (await ShowCenteredDialogAsync(dialog) != ContentDialogResult.Primary) return;
        if (!NotebookFieldsAreValid(title.Text, subject.Text, notebook)) return;

        var preservedOffset = GetLibraryScrollViewer()?.VerticalOffset ?? 0;
        notebook.Title = title.Text.Trim();
        notebook.Subject = subject.Text.Trim();
        notebook.Accent = SelectedAccent(accent);
        notebook.ModifiedAt = DateTimeOffset.Now;
        await _db.SaveNotebookAsync(notebook);

        // Keep the same object/container alive so editing never changes card geometry.
        // Refresh the realized card directly; the accent bar is updated from notebook.Accent.
        var existingIndex = _notebooks.IndexOf(notebook);
        if (existingIndex >= 0 &&
            _libraryList?.ContainerFromIndex(existingIndex) is ListViewItem container &&
            container.ContentTemplateRoot is Border realizedCard)
        {
            realizedCard.DataContext = notebook;
            ApplyNotebookCardVisuals(realizedCard, notebook);
        }

        await RefreshNotebookResultsAsync(_searchQuery);
        DispatcherQueue.TryEnqueue(RefreshRealizedNotebookCards);
        RestoreLibraryScrollOffset(preservedOffset);
    }

    private ScrollViewer? GetLibraryScrollViewer() =>
        _libraryList is null ? null : Descendants<ScrollViewer>(_libraryList).FirstOrDefault();

    private void RestoreLibraryScrollOffset(double offset)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var scrollViewer = GetLibraryScrollViewer();
            if (scrollViewer is null) return;
            var maxOffset = Math.Max(0, scrollViewer.ExtentHeight - scrollViewer.ViewportHeight);
            scrollViewer.ChangeView(null, Math.Clamp(offset, 0, maxOffset), null, true);
        });
    }

    private void UpdateNotebookDialogValidation(ContentDialog dialog, TextBox title, TextBox subject, Notebook? editingNotebook)
    {
        var valid = NotebookFieldsAreValid(title.Text, subject.Text, editingNotebook);

        // Use WinUI's real enabled state. AccentButtonStyle defines the disabled visuals,
        // so the button is unavailable both functionally and visually from the very first frame.
        // TextChanged calls this after every character, making the state fully live.
        var becameAvailable = valid && !dialog.IsPrimaryButtonEnabled;
        dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
        dialog.IsPrimaryButtonEnabled = valid;

        if (!becameAvailable) return;
        dialog.DispatcherQueue.TryEnqueue(() =>
        {
            var primaryButton = Descendants<Button>(dialog).FirstOrDefault(button => button.Name == "PrimaryButton");
            if (primaryButton is not null) AnimateButtonEnabled(primaryButton);
        });
    }

    private static void AnimateButtonEnabled(Button button)
    {
        button.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        var transform = button.RenderTransform as ScaleTransform ?? new ScaleTransform { ScaleX = 0.97, ScaleY = 0.97 };
        button.RenderTransform = transform;
        button.Opacity = 0.72;

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(170));
        var storyboard = new Storyboard();
        foreach (var property in new[] { nameof(ScaleTransform.ScaleX), nameof(ScaleTransform.ScaleY) })
        {
            var scale = new DoubleAnimation { To = 1, Duration = duration, EasingFunction = easing, EnableDependentAnimation = true };
            Storyboard.SetTarget(scale, transform);
            Storyboard.SetTargetProperty(scale, property);
            storyboard.Children.Add(scale);
        }
        var opacity = new DoubleAnimation { To = 1, Duration = duration, EasingFunction = easing, EnableDependentAnimation = true };
        Storyboard.SetTarget(opacity, button);
        Storyboard.SetTargetProperty(opacity, nameof(UIElement.Opacity));
        storyboard.Children.Add(opacity);
        storyboard.Begin();
    }

    private bool NotebookFieldsAreValid(string titleText, string subjectText, Notebook? editingNotebook)
    {
        var title = titleText.Trim();
        var subject = subjectText.Trim();
        if (title.Length == 0 || title.Length > 60) return false;
        if (subject.Length == 0 || subject.Length > 60) return false;

        return !_notebooks.Any(n =>
            !ReferenceEquals(n, editingNotebook) &&
            string.Equals(n.Title.Trim(), title, StringComparison.OrdinalIgnoreCase));
    }

    private async Task DeleteNotebookAsync(Notebook notebook)
    {
        var dialog = Dialog("Delete notebook?", new TextBlock { Text = $"“{notebook.Title}” and all of its pages will be permanently deleted.", TextWrapping = TextWrapping.Wrap }, "Delete", destructive: true);
        if (await ShowCenteredDialogAsync(dialog) != ContentDialogResult.Primary) return;
        await _db.DeleteNotebookAsync(notebook.Id); await ShowLibraryAsync();
    }

    private async Task OpenNotebookAsync(Notebook notebook)
    {
        // Every notebook starts from a predictable input state. Tool choices are deliberately
        // per-open-session rather than leaking from whichever notebook was used previously.
        _tool = InkTool.Pen;
        _eraseTarget = InkTool.Pen;
        _eraserEnabled = false;
        _penScrollMode = false;

        _currentNotebook = notebook;
        _editorAccent = NormalizeAccent(notebook.Accent);
        _settings.LastNotebookId = notebook.Id;
        TitleContext.Text = _currentNotebook?.Title ?? "";
        TitleContext.Foreground = Brush(_editorAccent);
        if (TitleBarDragTarget.Children.OfType<Border>().FirstOrDefault() is Border editorBrandSquare)
            editorBrandSquare.Background = Brush(_editorAccent);
        _pageViews.Clear();
        _activeInk = null;
        _pageTypeButton = null;
        // Show the editor from page metadata first. Ink is read only for realized pages;
        // deserializing every page before navigation made large notebooks feel frozen.
        var pages = await _db.GetPagesAsync(notebook.Id, loadInk: false);
        if (pages.Count == 0)
        {
            var first = new NotePage { NotebookId = notebook.Id, SortOrder = 0 };
            await _db.SavePageAsync(first); pages.Add(first);
        }
        foreach (var page in pages)
        {
            // Dotted paper was removed from the UI. Existing dotted pages are migrated to Blank
            // so old notebooks never surface an orphaned paper style label.
            if (page.PaperStyle == PaperStyle.Dotted)
            {
                page.PaperStyle = PaperStyle.Blank;
                await _db.SavePageAsync(page, saveInk: false);
            }
            _pageViews.Add(CreatePageView(page, inkLoaded: false));
        }
        await SetMainContentAnimatedAsync(BuildEditor(), forward: true);
        // The full title bar is a Windows drag region in the library. In the editor only
        // the small brand is draggable; the central top strip can receive touch gestures.
        SetTitleBar(TitleBarDragTarget);
        TitleBarGestureTarget.Visibility = Visibility.Visible;
    }

    private UIElement BuildEditor()
    {
        var editorAccent = Brush(_editorAccent);
        var root = new Grid { Background = Brush("#070706") };
        // The retracted drawer belongs to the editor, never to the title-bar row.
        var editorClip = new RectangleGeometry();
        root.Clip = editorClip;
        root.SizeChanged += (_, e) => editorClip.Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        _pageList = new ListView
        {
            ItemsSource = _pageViews,
            SelectionMode = ListViewSelectionMode.None,
            Background = Brush("#070706"),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            // The editor root owns navigation animation. Disable ListView's built-in entrance
            // transitions so pages do not rise from the bottom independently.
            ItemContainerTransitions = new TransitionCollection(),
            Transitions = new TransitionCollection()
        };
        _pageList.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        _pageList.SetValue(ScrollViewer.VerticalScrollModeProperty, ScrollMode.Enabled);
        _pageList.SetValue(ScrollViewer.IsDeferredScrollingEnabledProperty, false);
        _pageList.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        _pageList.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
          <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><ContentPresenter Content="{Binding Root}"/></DataTemplate>
        """);
        _pageList.ItemContainerStyle = (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
          <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ListViewItem">
            <Setter Property="Padding" Value="0"/><Setter Property="Margin" Value="0"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/><Setter Property="VerticalContentAlignment" Value="Stretch"/>
            <Setter Property="Background" Value="Transparent"/><Setter Property="IsTabStop" Value="False"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListViewItem"><ContentPresenter Content="{TemplateBinding Content}" ContentTemplate="{TemplateBinding ContentTemplate}" HorizontalAlignment="Stretch" VerticalAlignment="Stretch"/></ControlTemplate></Setter.Value></Setter>
          </Style>
        """);
        _pageList.ItemsPanel = (ItemsPanelTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
          <ItemsPanelTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><ItemsStackPanel Orientation="Vertical" AreStickyGroupHeadersEnabled="False"/></ItemsPanelTemplate>
        """);
        var pageHost = new Grid { ColumnSpacing = 14 };
        pageHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pageHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        pageHost.Children.Add(_pageList);
        var scrollTrack = new Grid { Width = 12, HorizontalAlignment = HorizontalAlignment.Center, Background = new SolidColorBrush(Colors.Transparent) };
        Grid.SetColumn(scrollTrack, 1);
        var trackBackground = new Border
        {
            Width = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brush("#141413"), BorderBrush = Brush("#2D2D2A"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), IsHitTestVisible = false
        };
        var scrollThumb = new Border
        {
            Width = 8, Height = 48, Background = editorAccent, CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var thumbLayer = new Canvas { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        thumbLayer.Children.Add(scrollThumb);
        scrollTrack.Children.Add(trackBackground);
        scrollTrack.Children.Add(thumbLayer);
        pageHost.Children.Add(scrollTrack);
        root.Children.Insert(0, pageHost);
        ScrollViewer? pageScrollViewer = null;
        bool thumbDragging = false;
        uint thumbPointerId = 0;
        double thumbDragStartY = 0;
        double thumbDragStartTop = 0;
        void SyncPageScrollThumb()
        {
            if (pageScrollViewer is null || scrollTrack.ActualHeight <= 0) return;
            var viewport = Math.Max(1, pageScrollViewer.ViewportHeight);
            var extent = Math.Max(viewport, pageScrollViewer.ExtentHeight);
            var scrollable = Math.Max(0, extent - viewport);
            var trackHeight = scrollTrack.ActualHeight;
            var thumbHeight = scrollable <= 0 ? trackHeight : Math.Min(trackHeight, Math.Max(44, trackHeight * viewport / extent));
            if (Math.Abs(scrollThumb.Height - thumbHeight) > 0.25) scrollThumb.Height = thumbHeight;
            var visibility = _pageViews.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            scrollThumb.Visibility = visibility;
            trackBackground.Visibility = visibility;
            var travel = Math.Max(0, trackHeight - thumbHeight);
            var top = scrollable <= 0 ? 0 : Math.Clamp(pageScrollViewer.VerticalOffset / scrollable * travel, 0, travel);
            Canvas.SetLeft(scrollThumb, 2);
            if (double.IsNaN(Canvas.GetTop(scrollThumb)) || Math.Abs(Canvas.GetTop(scrollThumb) - top) > 0.25)
                Canvas.SetTop(scrollThumb, top);
        }
        _pageList.Loaded += (_, _) =>
        {
            pageScrollViewer = Descendants<ScrollViewer>(_pageList).FirstOrDefault();
            if (pageScrollViewer is null) return;
            pageScrollViewer.ViewChanged += (_, _) =>
            {
                if (_pageViews.Count == 0) return;
                var index = Math.Clamp((int)Math.Round(pageScrollViewer.VerticalOffset / Math.Max(1, pageScrollViewer.ViewportHeight)), 0, _pageViews.Count - 1);
                _activeInk = _pageViews[index].Ink;
                if (_pageTypeButton is not null) _pageTypeButton.Content = _activeInk.Page.PaperStyle.ToString();
                RefreshHistoryActions();
                SyncPageScrollThumb();
            };
            SyncPageScrollThumb();
        };
        scrollTrack.SizeChanged += (_, _) => SyncPageScrollThumb();

        bool penPageScrolling = false;
        uint penScrollPointerId = 0;
        double penScrollLastY = 0;
        DateTimeOffset penScrollLastTime = DateTimeOffset.MinValue;
        double penScrollVelocity = 0; // scroll offset DIPs per millisecond
        double pendingPenScrollDelta = 0;
        var penScrollFrame = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        penScrollFrame.Tick += (_, _) =>
        {
            if (pageScrollViewer is null) { pendingPenScrollDelta = 0; return; }
            if (Math.Abs(pendingPenScrollDelta) < .01) return;
            var target = Math.Clamp(pageScrollViewer.VerticalOffset + pendingPenScrollDelta, 0, pageScrollViewer.ScrollableHeight);
            pendingPenScrollDelta = 0;
            pageScrollViewer.ChangeView(null, target, null, true);
        };
        var penScrollInertia = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        penScrollInertia.Tick += (_, _) =>
        {
            if (pageScrollViewer is null || penPageScrolling) { penScrollInertia.Stop(); return; }
            if (Math.Abs(penScrollVelocity) < 0.02) { penScrollInertia.Stop(); return; }
            var delta = penScrollVelocity * 16;
            var next = Math.Clamp(pageScrollViewer.VerticalOffset + delta, 0, pageScrollViewer.ScrollableHeight);
            var hitEdge = Math.Abs(next - pageScrollViewer.VerticalOffset) < 0.01;
            pageScrollViewer.ChangeView(null, next, null, true);
            penScrollVelocity *= 0.90;
            if (hitEdge) penScrollInertia.Stop();
        };
        _pageList.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            // Photo manipulation/floating controls own their pointer stream. Because this handler
            // listens to handled events too, explicitly respect that ownership before pen-scroll.
            if (e.Handled) return;
            if (!_penScrollMode || e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Pen || pageScrollViewer is null) return;
            penScrollInertia.Stop();
            pendingPenScrollDelta = 0;
            penScrollVelocity = 0;
            if (!_pageList.CapturePointer(e.Pointer)) return;
            penPageScrolling = true;
            penScrollPointerId = e.Pointer.PointerId;
            penScrollLastY = e.GetCurrentPoint(_pageList).Position.Y;
            penScrollLastTime = DateTimeOffset.UtcNow;
            penScrollFrame.Start();
            e.Handled = true;
        }), true);
        _pageList.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) =>
        {
            if (e.Handled && !penPageScrolling) return;
            if (!penPageScrolling || e.Pointer.PointerId != penScrollPointerId || pageScrollViewer is null) return;
            var now = DateTimeOffset.UtcNow;
            var y = e.GetCurrentPoint(_pageList).Position.Y;
            var dy = y - penScrollLastY;
            var dt = Math.Max(1, (now - penScrollLastTime).TotalMilliseconds);
            var deltaOffset = -dy;
            pendingPenScrollDelta += deltaOffset;
            var instantaneous = deltaOffset / dt;
            penScrollVelocity = penScrollVelocity * 0.62 + instantaneous * 0.38;
            penScrollLastY = y;
            penScrollLastTime = now;
            e.Handled = true;
        }), true);
        void EndPenPageScroll(PointerRoutedEventArgs e)
        {
            if (!penPageScrolling || e.Pointer.PointerId != penScrollPointerId) return;
            penPageScrolling = false;
            _pageList.ReleasePointerCapture(e.Pointer);
            penScrollFrame.Stop();
            if (pageScrollViewer is not null && Math.Abs(pendingPenScrollDelta) >= .01)
            {
                var target = Math.Clamp(pageScrollViewer.VerticalOffset + pendingPenScrollDelta, 0, pageScrollViewer.ScrollableHeight);
                pageScrollViewer.ChangeView(null, target, null, true);
            }
            pendingPenScrollDelta = 0;
            if (Math.Abs(penScrollVelocity) >= 0.02) penScrollInertia.Start();
            e.Handled = true;
        }
        _pageList.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) => EndPenPageScroll(e)), true);
        _pageList.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, e) => EndPenPageScroll(e)), true);
        _pageList.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, e) =>
        {
            if (e.Pointer.PointerId != penScrollPointerId) return;
            penPageScrolling = false;
            penScrollFrame.Stop();
            pendingPenScrollDelta = 0;
            if (Math.Abs(penScrollVelocity) >= 0.02) penScrollInertia.Start();
        }), true);
        root.Unloaded += (_, _) => { penScrollFrame.Stop(); penScrollInertia.Stop(); };
        scrollThumb.PointerPressed += (_, e) =>
        {
            if (pageScrollViewer is null || !scrollThumb.CapturePointer(e.Pointer)) return;
            thumbDragging = true;
            thumbPointerId = e.Pointer.PointerId;
            thumbDragStartY = e.GetCurrentPoint(scrollTrack).Position.Y;
            thumbDragStartTop = Canvas.GetTop(scrollThumb);
            if (double.IsNaN(thumbDragStartTop)) thumbDragStartTop = 0;
            e.Handled = true;
        };
        scrollThumb.PointerMoved += (_, e) =>
        {
            if (!thumbDragging || e.Pointer.PointerId != thumbPointerId || pageScrollViewer is null) return;
            var travel = Math.Max(0, scrollTrack.ActualHeight - scrollThumb.ActualHeight);
            if (travel <= 0) return;
            var top = Math.Clamp(thumbDragStartTop + e.GetCurrentPoint(scrollTrack).Position.Y - thumbDragStartY, 0, travel);
            var scrollable = Math.Max(0, pageScrollViewer.ExtentHeight - pageScrollViewer.ViewportHeight);
            pageScrollViewer.ChangeView(null, scrollable * top / travel, null, true);
            e.Handled = true;
        };
        void EndThumbDrag(PointerRoutedEventArgs e)
        {
            if (!thumbDragging || e.Pointer.PointerId != thumbPointerId) return;
            thumbDragging = false;
            scrollThumb.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
        scrollThumb.PointerReleased += (_, e) => EndThumbDrag(e);
        scrollThumb.PointerCanceled += (_, e) => EndThumbDrag(e);
        scrollThumb.PointerCaptureLost += (_, _) => thumbDragging = false;
        scrollTrack.PointerPressed += (_, e) =>
        {
            if (pageScrollViewer is null || FindAncestor<Border>(e.OriginalSource as DependencyObject) == scrollThumb) return;
            var travel = Math.Max(0, scrollTrack.ActualHeight - scrollThumb.ActualHeight);
            var scrollable = Math.Max(0, pageScrollViewer.ExtentHeight - pageScrollViewer.ViewportHeight);
            if (travel <= 0 || scrollable <= 0) return;
            var top = Math.Clamp(e.GetCurrentPoint(scrollTrack).Position.Y - scrollThumb.ActualHeight / 2, 0, travel);
            pageScrollViewer.ChangeView(null, scrollable * top / travel, null, true);
            e.Handled = true;
        };
        _pageList.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Height <= 0) return;
            foreach (var page in _pageViews)
                if (page.Root is FrameworkElement element) element.Height = e.NewSize.Height;
        };

        // The page chrome is fully retracted when idle. A wide, invisible top-edge gesture
        // target opens it without requiring the user to find a narrow grab handle.
        const double drawerPanelHeight = 62;
        var drawerTransform = new TranslateTransform { Y = -drawerPanelHeight };
        var drawer = new Grid
        {
            Height = drawerPanelHeight,
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransform = drawerTransform,
            Translation = new System.Numerics.Vector3(0, 0, 30),
            ManipulationMode = ManipulationModes.None
        };
        drawer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(drawerPanelHeight) });
        var edgeTarget = new Grid
        {
            // Visual/automation marker only. Do not put a hit-testable panel over the first
            // rows of the paper: that steals pen input. Touch reveal is handled at root level.
            Height = 126,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brush("#01070706"),
            Translation = new System.Numerics.Vector3(0, 0, 25),
            ManipulationMode = ManipulationModes.None,
            IsHitTestVisible = false
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(edgeTarget, "Pull down from the top to show page controls");
        var closeGestureTarget = new Grid
        {
            // Match the same 126 px gesture range while the drawer is open, so the
            // upward hide gesture is just as forgiving as the downward reveal gesture.
            Height = 126,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brush("#01070706"),
            Translation = new System.Numerics.Vector3(0, 0, 24),
            ManipulationMode = ManipulationModes.None,
            Visibility = Visibility.Collapsed,
            // Visual/semantic marker only. It must never sit on top of the paper because that
            // blocks pen hit-testing in the first few ruled rows. Upward touch gestures are
            // handled from the editor root instead.
            IsHitTestVisible = false
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(closeGestureTarget, "Push up to hide page controls");

        var drawerOpen = false;
        void SyncDrawerGestureHitTesting()
        {
            // In scroll mode the top 126 px remains a dedicated drawer gesture zone.
            // Everywhere below it stays owned by the page ScrollViewer. This makes the
            // interaction unambiguous: top band = drawer priority, rest of page = scroll.
            edgeTarget.IsHitTestVisible = _penScrollMode && !drawerOpen;
            closeGestureTarget.IsHitTestVisible = _penScrollMode && drawerOpen;
        }
        Action syncThisDrawerGestureHitTesting = SyncDrawerGestureHitTesting;
        _syncEditorDrawerGestureHitTesting = syncThisDrawerGestureHitTesting;

        var draggingDrawer = false;
        UIElement? gestureCapture = null;
        uint drawerPointerId = 0;
        double drawerStartY = 0;
        double drawerStartOffset = 0;
        double drawerTravel = 0;
        Storyboard? drawerAnimation = null;
        var drawerIdleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        drawerIdleTimer.Tick += (_, _) =>
        {
            drawerIdleTimer.Stop();
            if (drawerOpen && !draggingDrawer) SetDrawerOpen(false);
        };

        void StopDrawerAnimationAtCurrentPosition()
        {
            if (drawerAnimation is null) return;
            var current = drawerTransform.Y;
            drawerAnimation.Stop();
            drawerAnimation = null;
            drawerTransform.Y = current;
        }

        void SetDrawerOpen(bool open, bool animate = true)
        {
            StopDrawerAnimationAtCurrentPosition();
            drawerOpen = open;
            edgeTarget.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
            closeGestureTarget.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            SyncDrawerGestureHitTesting();
            drawerIdleTimer.Stop();
            if (open) drawerIdleTimer.Start();
            var target = open ? 0d : -drawerPanelHeight;
            if (!animate || Math.Abs(drawerTransform.Y - target) < 0.5)
            {
                drawerTransform.Y = target;
                return;
            }

            var start = drawerTransform.Y;
            var movement = new DoubleAnimation
            {
                From = start,
                To = target,
                Duration = new Duration(TimeSpan.FromMilliseconds(170)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(movement, drawerTransform);
            Storyboard.SetTargetProperty(movement, "Y");
            var storyboard = new Storyboard();
            storyboard.Children.Add(movement);
            drawerAnimation = storyboard;
            storyboard.Completed += (_, _) =>
            {
                if (!ReferenceEquals(drawerAnimation, storyboard)) return;
                drawerTransform.Y = target;
                drawerAnimation = null;
                storyboard.Stop();
            };
            storyboard.Begin();
        }
        Action toggleThisDrawer = () => SetDrawerOpen(!drawerOpen);
        Action hideThisDrawer = () => SetDrawerOpen(false);
        _toggleEditorDrawer = toggleThisDrawer;
        _hideEditorDrawer = hideThisDrawer;
        root.Unloaded += (_, _) =>
        {
            drawerIdleTimer.Stop();
            drawerAnimation?.Stop();
            if (ReferenceEquals(_toggleEditorDrawer, toggleThisDrawer)) _toggleEditorDrawer = null;
            if (ReferenceEquals(_hideEditorDrawer, hideThisDrawer)) _hideEditorDrawer = null;
            if (ReferenceEquals(_syncEditorDrawerGestureHitTesting, syncThisDrawerGestureHitTesting))
                _syncEditorDrawerGestureHitTesting = null;
        };

        var header = new Grid
        {
            Padding = new Thickness(18, 10, 18, 10),
            Background = Brush("#F2111110")
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headerEdge = new Border
        {
            Height = 1,
            Background = Brush("#2D2D2A"),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(-18, 0, -18, -10),
            IsHitTestVisible = false
        };
        Grid.SetColumnSpan(headerEdge, 3);
        header.Children.Add(headerEdge);

        var notebooksLabel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        notebooksLabel.Children.Add(new TextBlock
        {
            Text = "←",
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -1, 0, 0)
        });
        notebooksLabel.Children.Add(new TextBlock
        {
            Text = "Notebooks",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.ExtraBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -1, 0, 0)
        });
        var notebooks = new Button
        {
            Content = notebooksLabel,
            Height = 40,
            MinHeight = 0,
            MinWidth = 132,
            Padding = new Thickness(14, 0, 14, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["OutlineButtonStyle"]
        };
        notebooks.Foreground = editorAccent;
        notebooks.BorderBrush = editorAccent;
        notebooks.Click += async (_, _) => await ShowLibraryAsync();
        header.Children.Add(notebooks);

        Button IconPageButton(string glyph, string label, RoutedEventHandler action)
        {
            var button = new Button
            {
                Content = new FontIcon { Glyph = glyph, FontSize = 17, Foreground = editorAccent },
                Width = 42,
                Height = 40,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                Style = (Style)Application.Current.Resources["OutlineButtonStyle"],
                Foreground = editorAccent,
                BorderBrush = editorAccent
            };
            button.Click += action;
            ToolTipService.SetToolTip(button, label);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
            return button;
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        _pageTypeButton = ActionButton(_activeInk?.Page.PaperStyle.ToString() ?? PaperStyle.Ruled.ToString(), (_, _) => { });
        _pageTypeButton.Height = 40;
        _pageTypeButton.MinHeight = 0;
        _pageTypeButton.MinWidth = 118;
        _pageTypeButton.Padding = new Thickness(14, 5, 14, 5);
        _pageTypeButton.Background = Brush("#111110");
        _pageTypeButton.BorderBrush = Brush("#2D2D2A");
        _pageTypeButton.BorderThickness = new Thickness(2);
        _pageTypeButton.Foreground = editorAccent;
        _pageTypeButton.CornerRadius = new CornerRadius(3);
        // Same clean scale-only hover language as the rest of the app.
        _pageTypeButton.Style = (Style)Application.Current.Resources["OutlineButtonStyle"];

        var typeFlyout = new Flyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            ShouldConstrainToRootBounds = true,
            FlyoutPresenterStyle = (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
                <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="FlyoutPresenter">
                    <Setter Property="Padding" Value="0"/>
                    <Setter Property="Background" Value="Transparent"/>
                    <Setter Property="BorderThickness" Value="0"/>
                    <Setter Property="CornerRadius" Value="0"/>
                </Style>
                """)
        };
        typeFlyout.Opening += (_, _) => drawerIdleTimer.Stop();
        typeFlyout.Closed += (_, _) =>
        {
            if (drawerOpen) { drawerIdleTimer.Stop(); drawerIdleTimer.Start(); }
        };

        var typeChoices = new StackPanel { Spacing = 2, MinWidth = 210 };
        var typeContent = new Border
        {
            Background = Brush("#111110"),
            BorderBrush = Brush("#2D2D2A"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(9),
            Child = typeChoices
        };
        typeChoices.Children.Add(new TextBlock
        {
            Text = "PAGE TYPE",
            Foreground = Brush("#92928C"),
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            CharacterSpacing = 110,
            Margin = new Thickness(12, 6, 12, 7)
        });

        void RefreshPageTypeChoices()
        {
            foreach (var button in typeChoices.Children.OfType<Button>())
            {
                var active = _activeInk is not null && button.Tag is PaperStyle style && style == _activeInk.Page.PaperStyle;
                button.Background = active ? Brush("#1D1D1B") : new SolidColorBrush(Colors.Transparent);
                button.Foreground = active ? editorAccent : Brush("#F4F4F2");
            }
        }

        foreach (var paperStyle in Enum.GetValues<PaperStyle>().Where(style => style != PaperStyle.Dotted))
        {
            var choice = new Button
            {
                Content = paperStyle.ToString(),
                Tag = paperStyle,
                Height = 36,
                MinHeight = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(14, 0, 14, 0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Foreground = Brush("#F4F4F2"),
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                IsTabStop = false,
                CornerRadius = new CornerRadius(3),
                Style = FlatButtonStyle()
            };
            choice.PointerEntered += (_, _) => choice.Foreground = editorAccent;
            choice.PointerExited += (_, _) => RefreshPageTypeChoices();
            choice.Click += (_, _) =>
            {
                if (_activeInk is null) return;
                _activeInk.SetPaperStyle((PaperStyle)choice.Tag);
                _pageTypeButton.Content = choice.Content;
                RefreshPageTypeChoices();
                typeFlyout.Hide();
                // Changing paper type is a page action just like duplicate/new/delete: keep
                // the controls visible and restart the normal idle timeout instead of hiding them.
                SetDrawerOpen(true);
                drawerIdleTimer.Stop();
                drawerIdleTimer.Start();
            };
            typeChoices.Children.Add(choice);
        }
        typeFlyout.Opening += (_, _) => RefreshPageTypeChoices();
        typeFlyout.Content = typeContent;
        _pageTypeButton.Flyout = typeFlyout;
        actions.Children.Add(_pageTypeButton);
        actions.Children.Add(IconPageButton("\uE8C8", "Duplicate page", async (_, _) =>
        {
            if (_activeInk is not null) await DuplicatePageAsync(_activeInk.Page);
            // Keep the controls visible after page actions; let the normal idle timer
            // retract them instead of snapping them away immediately.
            SetDrawerOpen(true);
        }));
        actions.Children.Add(IconPageButton("\uE74D", "Delete page", async (_, _) =>
        {
            if (_activeInk is null) return;
            var activeView = _pageViews.FirstOrDefault(view => ReferenceEquals(view.Ink, _activeInk));

            // A notebook must always keep one physical page. When this is the final page,
            // the delete action becomes "clear page": remove all handwriting but keep the page.
            if (_pageViews.Count == 1 && activeView is not null)
            {
                drawerIdleTimer.Stop();
                var cleared = await ClearOnlyPageAsync(activeView);
                if (cleared)
                {
                    SetDrawerOpen(true, animate: false);
                    drawerIdleTimer.Stop();
                    drawerIdleTimer.Start();
                }
                else if (drawerOpen)
                {
                    drawerIdleTimer.Start();
                }
                return;
            }

            var deletedIndex = activeView is null ? 0 : _pageViews.IndexOf(activeView);

            // While the confirmation is open, freeze the editor behind it: no page scrolling,
            // no pull-down gesture and no idle drawer animation.
            drawerIdleTimer.Stop();
            var pageListWasEnabled = _pageList?.IsEnabled ?? true;
            if (_pageList is not null) _pageList.IsEnabled = false;
            var titleBarWasHitTestVisible = TitleBar.IsHitTestVisible;
            var gestureWasHitTestVisible = TitleBarGestureTarget.IsHitTestVisible;
            TitleBar.IsHitTestVisible = false;
            TitleBarGestureTarget.IsHitTestVisible = false;
            bool deleted;
            try
            {
                deleted = await DeletePageAsync(
                    _activeInk.Page,
                    activeView is null ? null : () => AnimatePageDeleteExitAsync(activeView.Root));
            }
            finally
            {
                if (_pageList is not null) _pageList.IsEnabled = pageListWasEnabled;
                TitleBar.IsHitTestVisible = titleBarWasHitTestVisible;
                TitleBarGestureTarget.IsHitTestVisible = gestureWasHitTestVisible;
            }

            if (!deleted)
            {
                if (drawerOpen) drawerIdleTimer.Start();
                return;
            }

            if (_pageViews.Count > 0)
            {
                // After deleting the current page, go to the previous page when possible.
                // Animate the viewport there so repeated deletes feel continuous instead of jumping.
                var nextIndex = Math.Clamp(deletedIndex - 1, 0, _pageViews.Count - 1);
                var nextView = _pageViews[nextIndex];
                _activeInk = nextView.Ink;
                if (_pageTypeButton is not null) _pageTypeButton.Content = _activeInk.Page.PaperStyle.ToString();
                await AnimatePageDeleteReplacementAsync(nextView);
            }
            // Keep the page controls visible after a delete so several pages can be removed
            // without repeatedly pulling the drawer down.
            SetDrawerOpen(true, animate: false);
            drawerIdleTimer.Stop();
            drawerIdleTimer.Start();
        }));
        var newPage = ActionButton("✚  New page", async (_, _) =>
        {
            var view = await AddPageAsync();
            if (view is not null)
            {
                _activeInk = view.Ink;
                if (_pageTypeButton is not null) _pageTypeButton.Content = view.Page.PaperStyle.ToString();
            }
            SetDrawerOpen(true);
        }, true);
        newPage.Height = 40;
        newPage.MinHeight = 0;
        newPage.Padding = new Thickness(16, 5, 16, 5);
        newPage.Background = editorAccent;
        newPage.BorderBrush = editorAccent;
        actions.Children.Add(newPage);
        Grid.SetColumn(actions, 2);
        header.Children.Add(actions);
        drawer.Children.Add(header);

        void StartDrawerGesture(UIElement surface, PointerRoutedEventArgs e)
        {
            // Never reinterpret a pointer stream already claimed by a photo or floating control.
            if (e.Handled) return;
            var device = e.Pointer.PointerDeviceType;
            if (device == Microsoft.UI.Input.PointerDeviceType.Pen)
            {
                _lastPenActivity = DateTimeOffset.UtcNow;
                // While writing, the pen must pass through the top band untouched. In explicit
                // scroll mode writing is disabled, so the same top band can safely reserve
                // pen drags for the drawer just like touch.
                if (!_penScrollMode) return;
            }
            // Palm rejection: while a pen is hovering/being used, ignore touch input in the
            // top gesture strip so the supporting hand cannot pull the drawer over the page.
            if (device == Microsoft.UI.Input.PointerDeviceType.Touch &&
                DateTimeOffset.UtcNow - _lastPenActivity < TimeSpan.FromMilliseconds(1400))
            {
                e.Handled = true;
                return;
            }
            if (draggingDrawer) return;
            StopDrawerAnimationAtCurrentPosition();
            draggingDrawer = surface.CapturePointer(e.Pointer);
            if (!draggingDrawer) return;
            gestureCapture = surface;
            drawerIdleTimer.Stop();
            drawerPointerId = e.Pointer.PointerId;
            drawerStartY = e.GetCurrentPoint(root).Position.Y;
            drawerStartOffset = drawerTransform.Y;
            drawerTravel = 0;
            e.Handled = true;
        }
        void MoveDrawerGesture(PointerRoutedEventArgs e)
        {
            if (!draggingDrawer || e.Pointer.PointerId != drawerPointerId) return;
            var delta = e.GetCurrentPoint(root).Position.Y - drawerStartY;
            drawerTravel = Math.Max(drawerTravel, Math.Abs(delta));
            drawerTransform.Y = Math.Clamp(drawerStartOffset + delta, -drawerPanelHeight, 0);
            e.Handled = true;
        }
        void FinishDrawerDrag(PointerRoutedEventArgs e, bool canceled)
        {
            if (!draggingDrawer || e.Pointer.PointerId != drawerPointerId) return;
            draggingDrawer = false;
            gestureCapture?.ReleasePointerCapture(e.Pointer);
            gestureCapture = null;
            if (canceled) SetDrawerOpen(drawerOpen);
            else if (drawerTravel < 5) SetDrawerOpen(!drawerOpen);
            else SetDrawerOpen(drawerTransform.Y > -drawerPanelHeight * .55);
            e.Handled = true;
        }
        void CaptureLost(PointerRoutedEventArgs e)
        {
            if (!draggingDrawer || e.Pointer.PointerId != drawerPointerId) return;
            draggingDrawer = false;
            gestureCapture = null;
            SetDrawerOpen(drawerTransform.Y > -drawerPanelHeight * .55);
        }
        // Only dedicated gesture zones may capture the pointer. The drawer/header itself is
        // deliberately excluded: capturing a touch/pen press on the drawer used to steal the
        // release from child Buttons, producing the "pressed animation but no action" bug.
        foreach (var surface in new[] { TitleBarGestureTarget, edgeTarget, closeGestureTarget })
        {
            surface.PointerPressed += (_, e) => StartDrawerGesture(surface, e);
            surface.PointerMoved += (_, e) => MoveDrawerGesture(e);
            surface.PointerReleased += (_, e) => FinishDrawerDrag(e, false);
            surface.PointerCanceled += (_, e) => FinishDrawerDrag(e, true);
            surface.PointerCaptureLost += (_, e) => CaptureLost(e);
        }

        // Touch in the top band starts only as a *candidate*. We intentionally do NOT capture
        // or mark the press handled yet: ordinary touch scrolling must fall through to the
        // ListView. The drawer takes ownership only after a clear vertical gesture in the
        // appropriate direction, at which point it has priority.
        bool touchDrawerCandidate = false;
        uint touchDrawerCandidateId = 0;
        double touchDrawerCandidateStartY = 0;
        bool touchDrawerCandidateWantsOpen = false;
        root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            // Observe handled touch presses too. InkPageControl/ScrollViewer can mark the event
            // handled before it reaches the root, but the top-edge drawer recognizer still needs
            // to see the gesture when normal pen-writing mode is active. Only a real photo
            // manipulation owns the stream strongly enough to suppress the drawer candidate.
            if (_activeInk?.IsPhotoInteractionActive == true) return;
            if (draggingDrawer || e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Touch) return;
            // Scroll mode uses the dedicated 126 px overlay above. Do not also run the
            // fall-through candidate recognizer or it can fight native touch scrolling.
            if (_penScrollMode) return;
            if (DateTimeOffset.UtcNow - _lastPenActivity < TimeSpan.FromMilliseconds(2500))
            {
                // Palm rejection while the pen is hovering/active. This touch is genuinely
                // ignored; unlike a normal top-edge touch, it must not become page scrolling.
                e.Handled = true;
                return;
            }

            var y = e.GetCurrentPoint(root).Position.Y;
            if (y > 126) return;
            if (drawerOpen && y <= drawerPanelHeight) return; // actual toolbar buttons win

            touchDrawerCandidate = true;
            touchDrawerCandidateId = e.Pointer.PointerId;
            touchDrawerCandidateStartY = y;
            touchDrawerCandidateWantsOpen = !drawerOpen;
            // No capture, no Handled here: native touch scroll remains fully available.
        }), true);
        root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) =>
        {
            if (gestureCapture == root) { MoveDrawerGesture(e); return; }
            if (!touchDrawerCandidate || e.Pointer.PointerId != touchDrawerCandidateId) return;

            var y = e.GetCurrentPoint(root).Position.Y;
            var dy = y - touchDrawerCandidateStartY;
            const double activationDistance = 14;
            var qualifies = touchDrawerCandidateWantsOpen ? dy >= activationDistance : dy <= -activationDistance;
            var opposite = touchDrawerCandidateWantsOpen ? dy <= -activationDistance : dy >= activationDistance;
            if (opposite)
            {
                // The user is scrolling in the non-drawer direction. Abandon the candidate and
                // leave the whole gesture to ScrollViewer.
                touchDrawerCandidate = false;
                return;
            }
            if (!qualifies) return;

            touchDrawerCandidate = false;
            // Now that intent is clear, the drawer wins and captures the remainder of the drag.
            StopDrawerAnimationAtCurrentPosition();
            if (!root.CapturePointer(e.Pointer)) return;
            draggingDrawer = true;
            gestureCapture = root;
            drawerIdleTimer.Stop();
            drawerPointerId = e.Pointer.PointerId;
            drawerStartY = touchDrawerCandidateStartY;
            drawerStartOffset = drawerTransform.Y;
            drawerTravel = Math.Abs(dy);
            drawerTransform.Y = Math.Clamp(drawerStartOffset + dy, -drawerPanelHeight, 0);
            e.Handled = true;
        }), true);
        root.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) =>
        {
            if (touchDrawerCandidate && e.Pointer.PointerId == touchDrawerCandidateId)
                touchDrawerCandidate = false;
            if (gestureCapture == root) FinishDrawerDrag(e, false);
        }), true);
        root.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, e) =>
        {
            if (touchDrawerCandidate && e.Pointer.PointerId == touchDrawerCandidateId)
                touchDrawerCandidate = false;
            if (gestureCapture == root) FinishDrawerDrag(e, true);
        }), true);
        root.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, e) =>
        {
            if (touchDrawerCandidate && e.Pointer.PointerId == touchDrawerCandidateId)
                touchDrawerCandidate = false;
            if (gestureCapture == root) CaptureLost(e);
        }), true);

        // Any interaction with the visible toolbar keeps it alive, but never captures or marks
        // the pointer handled. AddHandler(..., true) also sees pen/touch events already handled
        // by the Button control itself.
        void KeepDrawerAliveOnPress(object _, PointerRoutedEventArgs __)
        {
            if (!drawerOpen) return;
            drawerIdleTimer.Stop();
        }
        void ResumeDrawerIdleAfterInteraction(object _, PointerRoutedEventArgs __)
        {
            if (!drawerOpen) return;
            drawerIdleTimer.Stop();
            drawerIdleTimer.Start();
        }
        header.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(KeepDrawerAliveOnPress), true);
        header.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(ResumeDrawerIdleAfterInteraction), true);
        header.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(ResumeDrawerIdleAfterInteraction), true);
        edgeTarget.PointerWheelChanged += (_, e) =>
        {
            if (e.GetCurrentPoint(edgeTarget).Properties.MouseWheelDelta > 0)
            {
                SetDrawerOpen(true);
                e.Handled = true;
            }
        };
        TitleBarGestureTarget.PointerWheelChanged += (_, e) =>
        {
            if (e.GetCurrentPoint(TitleBarGestureTarget).Properties.MouseWheelDelta > 0)
            {
                SetDrawerOpen(true);
                e.Handled = true;
            }
        };
        drawer.PointerMoved += (_, _) =>
        {
            if (drawerOpen && !draggingDrawer) { drawerIdleTimer.Stop(); drawerIdleTimer.Start(); }
        };
        SyncDrawerGestureHitTesting();
        root.Children.Add(closeGestureTarget);
        root.Children.Add(drawer);
        root.Children.Add(edgeTarget);

        // Track pen hover/contact anywhere in the editor, not only on the canvas. This gives
        // the top-edge gesture a short palm-rejection window before a supporting hand lands.
        root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) =>
        {
            if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Pen)
                _lastPenActivity = DateTimeOffset.UtcNow;
        }), true);
        root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Pen)
                _lastPenActivity = DateTimeOffset.UtcNow;
        }), true);

        _floatingToolbar = BuildToolbar();
        root.Children.Add(_floatingToolbar);
        return root;
    }

    private Border BuildToolbar()
    {
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        tools.Children.Add(ToolButton("\uED63", "Pen", InkTool.Pen));
        tools.Children.Add(ToolButton("\uE7E6", "Highlighter", InkTool.Highlighter));
        tools.Children.Add(EraserToggleButton("\uED60", "Eraser", iconOffsetY: 1.1f));
        tools.Children.Add(ToolButton("\uF407", "Select", InkTool.Lasso, iconOffsetY: 0.9f));
        _scrollToolButton = ScrollToolButton("\uECE9", "Scroll with pen", iconOffsetX: 0f, iconOffsetY: 0.9f);
        tools.Children.Add(_scrollToolButton);
        var addPhotoButton = IconToolAction("\uEB9F", "Add photo", async (_, _) => await AddPhotoToActivePageAsync());
        addPhotoButton.Style = (Style)Application.Current.Resources["OutlineButtonStyle"];
        addPhotoButton.Background = Brush("#00111110");
        addPhotoButton.Foreground = Brush(_editorAccent);
        addPhotoButton.BorderBrush = Brush(_editorAccent);
        addPhotoButton.BorderThickness = new Thickness(2);
        tools.Children.Add(addPhotoButton);

        _undoButton = IconToolAction("\uE7A7", "Undo", (_, _) =>
        {
            if (_activeInk?.CanUndo == true) _activeInk.Undo();
            RefreshHistoryActions();
        });
        _redoButton = IconToolAction("\uE7A6", "Redo", (_, _) =>
        {
            if (_activeInk?.CanRedo == true) _activeInk.Redo();
            RefreshHistoryActions();
        });
        tools.Children.Add(_undoButton);
        tools.Children.Add(_redoButton);
        RefreshHistoryActions();

        return new Border
        {
            Child = tools,
            Background = Brush("#F0111110"),
            BorderBrush = Brush(_editorAccent),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 14),
            Translation = new System.Numerics.Vector3(0, 0, 24)
        };

        Button IconToolAction(string glyph, string label, RoutedEventHandler action, bool accent = false)
        {
            var icon = new FontIcon
            {
                Glyph = glyph,
                FontSize = 17,
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var iconHost = new Grid { Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            iconHost.Children.Add(icon);
            var button = ActionButton("", action, accent);
            button.Content = iconHost;
            button.Width = 38;
            button.Height = 36;
            button.MinHeight = 0;
            button.MinWidth = 0;
            button.Padding = new Thickness(0);
            ToolTipService.SetToolTip(button, label);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
            return button;
        }
    }

    private void RefreshToolButtonVisuals()
    {
        if (_floatingToolbar is null) return;
        foreach (var candidate in Descendants<Button>(_floatingToolbar).Where(x => x.Tag is InkTool || Equals(x.Tag, "eraser-toggle") || Equals(x.Tag, "scroll-toggle")))
        {
            bool selected;
            if (Equals(candidate.Tag, "eraser-toggle"))
                selected = _eraserEnabled;
            else if (Equals(candidate.Tag, "scroll-toggle"))
                selected = _penScrollMode;
            else
            {
                var candidateTool = (InkTool)candidate.Tag;
                selected = candidateTool switch
                {
                    InkTool.Pen => _eraseTarget == InkTool.Pen && _tool != InkTool.Lasso,
                    InkTool.Highlighter => _eraseTarget == InkTool.Highlighter && _tool != InkTool.Lasso,
                    InkTool.Lasso => _tool == InkTool.Lasso,
                    _ => false
                };
            }
            candidate.Style = (Style)Application.Current.Resources[selected ? "AccentButtonStyle" : "OutlineButtonStyle"];
            candidate.Background = selected ? Brush(_editorAccent) : Brush("#00111110");
            candidate.Foreground = selected ? Brush("#070706") : Brush(_editorAccent);
            candidate.BorderBrush = Brush(_editorAccent);
            candidate.BorderThickness = selected ? new Thickness(0) : new Thickness(2);
        }
    }

    private void RefreshSelectionAction()
    {
        // Selection actions are rendered beside the selection on the page itself.
    }

    private void RefreshHistoryActions()
    {
        ApplyHistoryButtonVisual(_undoButton, _activeInk?.CanUndo == true);
        ApplyHistoryButtonVisual(_redoButton, _activeInk?.CanRedo == true);
    }

    private void ApplyHistoryButtonVisual(Button? button, bool available)
    {
        if (button is null) return;
        button.Style = (Style)Application.Current.Resources[available ? "AccentButtonStyle" : "OutlineButtonStyle"];
        button.Background = available ? Brush(_editorAccent) : Brush("#00111110");
        button.Foreground = available ? Brush("#070706") : Brush(_editorAccent);
        button.BorderBrush = Brush(_editorAccent);
        button.BorderThickness = available ? new Thickness(0) : new Thickness(2);
    }

    private Button ToolButton(string glyph, string label, InkTool tool, float iconOffsetY = -0.5f)
    {
        var button = ActionButton("", (_, _) =>
        {
            _penScrollMode = false;
            if (tool == InkTool.Lasso)
            {
                _eraserEnabled = false;
                _tool = InkTool.Lasso;
            }
            else
            {
                _tool = tool;
                _eraseTarget = tool;
                // Keep the eraser toggle active when switching between pen and highlighter.
            }
            ApplyInputSettings();
            RefreshToolButtonVisuals();
            RefreshSelectionAction();
            RefreshHistoryActions();
        });
        button.Tag = tool;
        button.Style = (Style)Application.Current.Resources["OutlineButtonStyle"];
        button.Content = CenteredToolIcon(glyph, iconOffsetY);
        button.Width = 38;
        button.Height = 36;
        button.MinHeight = 0;
        button.MinWidth = 0;
        button.Padding = new Thickness(0);
        ToolTipService.SetToolTip(button, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        button.Loaded += (_, _) => RefreshToolButtonVisuals();
        return button;
    }

    private Button EraserToggleButton(string glyph, string label, float iconOffsetY = -0.5f)
    {
        var button = ActionButton("", (_, _) =>
        {
            _penScrollMode = false;
            if (_tool == InkTool.Lasso)
            {
                _tool = _eraseTarget;
                _eraserEnabled = true;
            }
            else
                _eraserEnabled = !_eraserEnabled;
            ApplyInputSettings();
            RefreshToolButtonVisuals();
            RefreshSelectionAction();
        });
        button.Tag = "eraser-toggle";
        button.Style = (Style)Application.Current.Resources["OutlineButtonStyle"];
        button.Content = CenteredToolIcon(glyph, offsetY: iconOffsetY);
        button.Width = 38;
        button.Height = 36;
        button.MinHeight = 0;
        button.MinWidth = 0;
        button.Padding = new Thickness(0);
        ToolTipService.SetToolTip(button, "Toggle eraser for the selected ink layer");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        button.Loaded += (_, _) => RefreshToolButtonVisuals();
        return button;
    }

    private Button ScrollToolButton(string glyph, string label, float iconOffsetX = 0, float iconOffsetY = -0.5f)
    {
        var button = ActionButton("", (_, _) =>
        {
            _penScrollMode = !_penScrollMode;
            if (_penScrollMode)
            {
                _eraserEnabled = false;
                _activeInk?.SetTool(_tool);
            }
            ApplyInputSettings();
            RefreshToolButtonVisuals();
        });
        button.Tag = "scroll-toggle";
        button.Style = (Style)Application.Current.Resources["OutlineButtonStyle"];
        button.Content = CenteredToolIcon(glyph, iconOffsetY, iconOffsetX);
        button.Width = 38;
        button.Height = 36;
        button.MinHeight = 0;
        button.MinWidth = 0;
        button.Padding = new Thickness(0);
        ToolTipService.SetToolTip(button, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        button.Loaded += (_, _) => RefreshToolButtonVisuals();
        return button;
    }

    private static Grid CenteredToolIcon(string glyph, float offsetY = -0.5f, float offsetX = 0)
    {
        var host = new Grid
        {
            Width = 24,
            Height = 24,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        host.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 17,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Translation = new Vector3(offsetX, offsetY, 0)
        });
        return host;
    }

    private PageView CreatePageView(NotePage page, bool inkLoaded = true)
    {
        var ink = new InkPageControl(page)
        {
            MouseDrawingEnabled = false,
            PenInputEnabled = !_penScrollMode,
            HighlighterColor = _editorAccent,
            EraserTarget = _eraseTarget
        };
        _activeInk ??= ink;
        ink.SetAccentColor(_editorAccent);
        ink.SetTool(_eraserEnabled ? InkTool.Eraser : _tool);
        ink.InkChanged += (_, _) => { ink.TagAsDirty(); _activeInk = ink; };
        ink.PenActivity += (_, _) => _lastPenActivity = DateTimeOffset.UtcNow;
        ink.SelectionChanged += (_, _) =>
        {
            if (_activeInk == ink) RefreshSelectionAction();
        };
        ink.HistoryChanged += (_, _) =>
        {
            if (_activeInk == ink) RefreshHistoryActions();
        };
        ink.WritingStateChanged += (_, writing) =>
        {
            if (writing) _lastPenActivity = DateTimeOffset.UtcNow;
            _activeInk = ink;
            if (_floatingToolbar is not null) _floatingToolbar.Visibility = writing ? Visibility.Collapsed : Visibility.Visible;
        };
        ink.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) =>
        {
            _activeInk = ink;
            RefreshSelectionAction();
            RefreshHistoryActions();
            if (_pageTypeButton is not null) _pageTypeButton.Content = ink.Page.PaperStyle.ToString();
        }), true);
        var viewbox = new Viewbox { Child = ink, Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        var scaled = new Border { Child = viewbox, Background = Brush("#0A0A09"), BorderBrush = Brush("#282825"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2) };
        // Cache the composed page as a texture. Filled pages otherwise force WinUI to re-rasterize
        // hundreds/thousands of vector stroke segments every frame while the notebook scrolls.
        // The cache is invalidated automatically when ink changes, but scrolling becomes a cheap
        // compositor transform instead of a full redraw of every stroke.
        scaled.CacheMode = new BitmapCache();
        // A page occupies exactly one viewport. The PAGE x label is an overlay rather than
        // an extra row, so it never extends the scroll extent beyond the physical page.
        var pageRoot = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        pageRoot.Children.Add(scaled);
        var pageNumber = new TextBlock
        {
            Text = $"PAGE {page.SortOrder + 1}",
            FontSize = 11,
            CharacterSpacing = 120,
            Foreground = Brush("#888884"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 10, 8),
            IsHitTestVisible = false,
            Translation = new System.Numerics.Vector3(0, 0, 8)
        };
        pageRoot.Children.Add(pageNumber);
        // Coalesce the many intermediate sizes emitted while a window is being dragged.
        // Only the final viewport dimensions need a newly generated paper pattern.
        var paperResizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        double paperWidth = 0;
        double paperHeight = 0;
        scaled.SizeChanged += (_, e) =>
        {
            paperWidth = e.NewSize.Width;
            paperHeight = e.NewSize.Height;
            paperResizeTimer.Stop();
            paperResizeTimer.Start();
        };
        paperResizeTimer.Tick += (_, _) =>
        {
            paperResizeTimer.Stop();
            ApplyPhysicalPaperMetrics(ink, paperWidth, paperHeight);
        };
        pageRoot.Unloaded += (_, _) => paperResizeTimer.Stop();
        var view = new PageView(page, ink, scaled, pageRoot, pageNumber, inkLoaded);
        pageRoot.Loaded += async (_, _) => await view.EnsureInkLoadedAsync();
        ink.RegisterDirty(view.Dirty);
        return view;
    }

    private void ApplyPhysicalPaperMetrics(InkPageControl ink, double renderedWidth, double renderedHeight)
    {
        if (renderedWidth <= 0 || renderedHeight <= 0) return;

        var physicalPpi = 96d;
        var rasterScale = Content.XamlRoot?.RasterizationScale ?? 1d;
        var window = WindowNative.GetWindowHandle(this);
        var deviceContext = GetDC(window);
        if (deviceContext != IntPtr.Zero)
        {
            try
            {
                const int HorzSize = 4;
                const int VertSize = 6;
                const int HorzRes = 8;
                const int VertRes = 10;
                var widthMillimeters = GetDeviceCaps(deviceContext, HorzSize);
                var heightMillimeters = GetDeviceCaps(deviceContext, VertSize);
                var widthPixels = GetDeviceCaps(deviceContext, HorzRes);
                var heightPixels = GetDeviceCaps(deviceContext, VertRes);
                var horizontalPpi = widthMillimeters > 0 ? widthPixels * 25.4 / widthMillimeters : 0;
                var verticalPpi = heightMillimeters > 0 ? heightPixels * 25.4 / heightMillimeters : 0;
                if (horizontalPpi > 40 && verticalPpi > 40)
                    physicalPpi = (horizontalPpi + verticalPpi) / 2d;
            }
            finally
            {
                ReleaseDC(window, deviceContext);
            }
        }

        ink.SetPhysicalPaperMetrics(renderedWidth, renderedHeight, physicalPpi, rasterScale);
    }

    private async Task AddPhotoToActivePageAsync()
    {
        if (_activeInk is null) return;
        var view = _pageViews.FirstOrDefault(item => ReferenceEquals(item.Ink, _activeInk));
        if (view is null) return;
        await view.EnsureInkLoadedAsync();

        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.Thumbnail,
            SuggestedStartLocation = PickerLocationId.PicturesLibrary
        };
        // Common raster/vector formats supported by Windows imaging / WinUI.
        foreach (var extension in new[]
        {
            ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".bmp", ".dib",
            ".gif", ".webp", ".tif", ".tiff", ".ico", ".svg", ".svgz",
            ".heic", ".heif", ".avif"
        })
            picker.FileTypeFilter.Add(extension);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        await _activeInk.AddPhotoAsync(file);
        view.Dirty();
    }

    private async Task<PageView?> AddPageAsync()
    {
        if (_currentNotebook is null) return null;
        var page = new NotePage { NotebookId = _currentNotebook.Id, SortOrder = _pageViews.Count };
        await _db.SavePageAsync(page);
        var view = CreatePageView(page);
        if (view.Root is FrameworkElement element && _pageList is { ActualHeight: > 0 }) element.Height = _pageList.ActualHeight;
        _pageViews.Add(view);
        ScrollToPage(view);
        return view;
    }

    private async Task DuplicatePageAsync(NotePage source)
    {
        var sourceView = _pageViews.FirstOrDefault(x => ReferenceEquals(x.Page, source) || x.Page.Id == source.Id);
        if (sourceView is not null)
        {
            await sourceView.EnsureInkLoadedAsync();
            // Page.Strokes is the live model used by InkPageControl, so copy from the realized
            // source view after loading to include every stroke currently visible on the page.
            source = sourceView.Page;
        }
        else
        {
            source.Strokes = await InkFileService.LoadAsync(source.Id);
            source.Photos = await PhotoFileService.LoadAsync(source.Id);
        }

        static InkStrokeData CloneStroke(InkStrokeData stroke) => new()
        {
            Tool = stroke.Tool,
            Color = stroke.Color,
            Width = stroke.Width,
            Points = stroke.Points.Select(point => new InkPointData
            {
                X = point.X,
                Y = point.Y,
                Pressure = point.Pressure
            }).ToList()
        };

        var page = new NotePage
        {
            NotebookId = source.NotebookId,
            SortOrder = source.SortOrder + 1,
            PaperStyle = source.PaperStyle,
            Strokes = source.Strokes.Select(CloneStroke).ToList()
        };
        page.Photos = await PhotoFileService.CloneForPageAsync(source.Id, page.Id, source.Photos);

        // Persist the cloned ink before it is inserted into the virtualized editor. This makes
        // duplication independent of realization/autosave timing and guarantees a true copy.
        await _db.SavePageAsync(page, saveInk: true);
        var view = CreatePageView(page, inkLoaded: true);
        // inkLoaded=true means the model already contains the clone; render it immediately
        // instead of waiting for the control to be recreated on the next notebook open.
        await view.Ink.RefreshInkAsync();
        if (view.Root is FrameworkElement element && _pageList is { ActualHeight: > 0 }) element.Height = _pageList.ActualHeight;
        _pageViews.Insert(Math.Min(page.SortOrder, _pageViews.Count), view);
        await NormalizeAndSavePagesAsync();
        _activeInk = view.Ink;
        if (_pageTypeButton is not null) _pageTypeButton.Content = view.Page.PaperStyle.ToString();
        ScrollToPage(view);
    }

    private void ScrollToPage(PageView view)
    {
        if (_pageList is null) return;

        // Do not call ScrollIntoView here: it jumps immediately and makes the following
        // animated ChangeView invisible. Let layout realize the new page first, then animate
        // the internal ScrollViewer to the page's exact viewport-sized offset.
        _pageList.DispatcherQueue.TryEnqueue(() =>
        {
            if (_pageList is null) return;
            _pageList.UpdateLayout();

            _pageList.DispatcherQueue.TryEnqueue(() =>
            {
                if (_pageList is null) return;
                var scroll = Descendants<ScrollViewer>(_pageList).FirstOrDefault();
                if (scroll is null || scroll.ViewportHeight <= 0) return;

                var index = _pageViews.IndexOf(view);
                if (index < 0) return;
                var target = Math.Clamp(index * scroll.ViewportHeight, 0, scroll.ScrollableHeight);
                // disableAnimation:false gives the native smooth WinUI scrolling transition.
                scroll.ChangeView(null, target, null, false);
            });
        });
    }

    private async Task<bool> ClearOnlyPageAsync(PageView view)
    {
        var confirmation = Dialog(
            "Clear page?",
            new TextBlock
            {
                Text = "This is the only page. Its handwriting will be permanently cleared.",
                TextWrapping = TextWrapping.Wrap
            },
            "Clear",
            destructive: true,
            compact: true);

        if (await ShowCenteredDialogAsync(confirmation) != ContentDialogResult.Primary)
            return false;

        // Lazy pages may not have their ink in memory yet. Load first so clearing cannot be
        // followed by an old on-disk stroke set appearing again later.
        await view.EnsureInkLoadedAsync();
        view.Ink.ClearAllInk();

        // InkChanged marks the view dirty; also persist immediately because this is destructive.
        await _db.SavePageAsync(view.Page, saveInk: true);
        return true;
    }

    private async Task<bool> DeletePageAsync(NotePage page, Func<Task>? beforeDelete = null)
    {
        if (_pageViews.Count == 1) return false;
        var confirmation = Dialog(
            "Delete page?",
            new TextBlock { Text = "This page and its handwriting will be permanently deleted.", TextWrapping = TextWrapping.Wrap },
            "Delete",
            destructive: true,
            compact: true);
        if (await ShowCenteredDialogAsync(confirmation) != ContentDialogResult.Primary) return false;

        if (beforeDelete is not null)
            await beforeDelete();

        var view = _pageViews.First(x => x.Page == page);
        _pageViews.Remove(view);
        view.Dispose();
        await _db.DeletePageAsync(page);
        await NormalizeAndSavePagesAsync();
        return true;
    }

    private static Task AnimatePageDeleteExitAsync(UIElement element)
    {
        var completion = new TaskCompletionSource<bool>();
        var transform = new CompositeTransform { ScaleX = 1, ScaleY = 1, TranslateY = 0 };
        element.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        element.RenderTransform = transform;

        var duration = new Duration(TimeSpan.FromMilliseconds(165));
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var opacity = new DoubleAnimation { From = 1, To = 0, Duration = duration, EasingFunction = easing, EnableDependentAnimation = true };
        var move = new DoubleAnimation { From = 0, To = -24, Duration = duration, EasingFunction = easing, EnableDependentAnimation = true };
        var scaleX = new DoubleAnimation { From = 1, To = 0.985, Duration = duration, EasingFunction = easing, EnableDependentAnimation = true };
        var scaleY = new DoubleAnimation { From = 1, To = 0.985, Duration = duration, EasingFunction = easing, EnableDependentAnimation = true };

        Storyboard.SetTarget(opacity, element);
        Storyboard.SetTargetProperty(opacity, nameof(UIElement.Opacity));
        Storyboard.SetTarget(move, transform);
        Storyboard.SetTargetProperty(move, nameof(CompositeTransform.TranslateY));
        Storyboard.SetTarget(scaleX, transform);
        Storyboard.SetTargetProperty(scaleX, nameof(CompositeTransform.ScaleX));
        Storyboard.SetTarget(scaleY, transform);
        Storyboard.SetTargetProperty(scaleY, nameof(CompositeTransform.ScaleY));

        var storyboard = new Storyboard();
        storyboard.Children.Add(opacity);
        storyboard.Children.Add(move);
        storyboard.Children.Add(scaleX);
        storyboard.Children.Add(scaleY);
        storyboard.Completed += (_, _) =>
        {
            storyboard.Stop();
            completion.TrySetResult(true);
        };
        storyboard.Begin();
        return completion.Task;
    }

    private async Task AnimatePageDeleteReplacementAsync(PageView view)
    {
        if (_pageList is null) return;

        _pageList.UpdateLayout();
        var scroll = Descendants<ScrollViewer>(_pageList).FirstOrDefault();
        if (scroll is null || scroll.ViewportHeight <= 0)
        {
            ScrollToPage(view);
            return;
        }

        var index = _pageViews.IndexOf(view);
        if (index < 0) return;
        var target = Math.Clamp(index * scroll.ViewportHeight, 0, scroll.ScrollableHeight);

        // Snap the viewport only while the deleted page is already fully faded out.
        // The replacement page is then visually brought in from its former direction,
        // so the layout change reads as one continuous page-removal gesture.
        scroll.ChangeView(null, target, null, true);
        _pageList.UpdateLayout();

        var element = view.Root;
        var travel = Math.Clamp(scroll.ViewportHeight * 0.12, 56, 120);
        var transform = new TranslateTransform { Y = -travel };
        element.RenderTransform = transform;
        element.Opacity = 0.82;

        await Task.Yield();

        var completion = new TaskCompletionSource<bool>();
        var duration = new Duration(TimeSpan.FromMilliseconds(280));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var move = new DoubleAnimation { From = -travel, To = 0, Duration = duration, EasingFunction = easing, EnableDependentAnimation = true };
        var opacity = new DoubleAnimation { From = 0.82, To = 1, Duration = duration, EasingFunction = easing, EnableDependentAnimation = true };
        Storyboard.SetTarget(move, transform);
        Storyboard.SetTargetProperty(move, nameof(TranslateTransform.Y));
        Storyboard.SetTarget(opacity, element);
        Storyboard.SetTargetProperty(opacity, nameof(UIElement.Opacity));

        var storyboard = new Storyboard();
        storyboard.Children.Add(move);
        storyboard.Children.Add(opacity);
        storyboard.Completed += (_, _) =>
        {
            element.Opacity = 1;
            transform.Y = 0;
            storyboard.Stop();
            completion.TrySetResult(true);
        };
        storyboard.Begin();
        await completion.Task;
    }

    private async Task NormalizeAndSavePagesAsync()
    {
        for (var i = 0; i < _pageViews.Count; i++)
        {
            _pageViews[i].Page.SortOrder = i;
            _pageViews[i].PageNumber.Text = $"PAGE {i + 1}";
            _pageViews[i].Page.ModifiedAt = DateTimeOffset.Now;
            await _db.SavePageAsync(_pageViews[i].Page, _pageViews[i].InkLoaded);
        }
    }

    private async Task FlushPagesAsync()
    {
        foreach (var view in _pageViews) await view.FlushAsync(_db);
    }

    private void ApplyInputSettings()
    {
        _syncEditorDrawerGestureHitTesting?.Invoke();
        foreach (var page in _pageViews)
        {
            page.Ink.EraserTarget = _eraseTarget;
            page.Ink.PenInputEnabled = !_penScrollMode;
            // In pen-scroll mode the page itself must be completely transparent to hit testing.
            // That lets touch/pen manipulation target the ListView/ScrollViewer directly instead
            // of getting stuck on the full-page ink Canvas. The root-level top-drawer recognizer
            // still sees routed touch first and can take ownership only when the gesture actually
            // qualifies as a drawer open/close gesture.
            page.Ink.IsHitTestVisible = !_penScrollMode;
            page.Ink.SetTool(_eraserEnabled ? InkTool.Eraser : _tool);
            page.Ink.MouseDrawingEnabled = false;
        }
    }

    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_currentNotebook is not null && e.Key == VirtualKey.F10)
        {
            _toggleEditorDrawer?.Invoke();
            e.Handled = true;
            return;
        }
        if (_currentNotebook is not null && e.Key == VirtualKey.Escape)
        {
            _hideEditorDrawer?.Invoke();
            e.Handled = true;
            return;
        }
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.Delete && _activeInk is not null) { _activeInk.DeleteSelection(); e.Handled = true; }
        if (!ctrl) return;
        if (e.Key == VirtualKey.Z && !shift) _activeInk?.Undo();
        else if (e.Key == VirtualKey.Y || (e.Key == VirtualKey.Z && shift)) _activeInk?.Redo();
        else if (e.Key == VirtualKey.N && shift && _currentNotebook is null) await CreateNotebookAsync();
        else if (e.Key == VirtualKey.N && _currentNotebook is not null) await AddPageAsync();
        else if (e.Key == VirtualKey.F && _currentNotebook is null && _searchBox is not null) _searchBox.Focus(FocusState.Keyboard);
        else return;
        e.Handled = true;
    }

    private async Task<ContentDialogResult> ShowCenteredDialogAsync(ContentDialog dialog)
    {
        // WinUI's PopupRoot placement can end up biased toward the left in a custom
        // title-bar window. Recenter from the dialog's *actual rendered bounds* every time
        // it opens, so every ContentDialog in A-Note lands in the visual center of the app.
        void CenterDialog()
        {
            if (Content is not FrameworkElement root || dialog.ActualWidth <= 0 || dialog.ActualHeight <= 0) return;
            try
            {
                // Measure from the unshifted popup position so repeated layout/SizeChanged
                // passes don't oscillate between centered and the original placement.
                dialog.Translation = Vector3.Zero;
                var origin = dialog.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, 0));
                var targetX = (root.ActualWidth - dialog.ActualWidth) / 2d;
                var targetY = (root.ActualHeight - dialog.ActualHeight) / 2d;
                dialog.Translation = new Vector3(
                    (float)(targetX - origin.X),
                    (float)(targetY - origin.Y),
                    0f);
            }
            catch
            {
                // If the popup visual isn't connected for this frame yet, the queued pass below
                // will retry after layout.
            }
        }

        dialog.Opened += (_, _) =>
        {
            CenterDialog();
            dialog.DispatcherQueue.TryEnqueue(CenterDialog);
        };
        dialog.SizeChanged += (_, _) => CenterDialog();

        return await dialog.ShowAsync(ContentDialogPlacement.Popup);
    }

    private ContentDialog Dialog(
        string title, UIElement content, string primary, bool destructive = false, bool compact = false)
    {
        // ContentDialog itself is not a reliable focus target: focusing it may make WinUI
        // immediately fall back to the first TextBox. Use a real Control as a neutral
        // programmatic focus destination instead. It is invisible, not hit-testable and
        // excluded from keyboard Tab navigation.
        var focusSink = new Button
        {
            Width = 1,
            Height = 1,
            Opacity = 0,
            IsHitTestVisible = false,
            // Programmatic focus is intentionally allowed so tapping blank dialog space can
            // truly dismiss a TextBox caret. It is visually invisible and never clicked.
            IsTabStop = true,
            UseSystemFocusVisuals = false,
            AllowFocusOnInteraction = false,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        var contentHost = new Grid
        {
            MinHeight = 0,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = compact ? new Thickness(0, 0, 0, -18) : new Thickness(0)
        };
        contentHost.Children.Add(content);
        contentHost.Children.Add(focusSink);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = new TextBlock
            {
                Text = title,
                FontFamily = (FontFamily)Application.Current.Resources["DisplayFontFamily"],
                FontSize = compact ? 25 : 28,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = Brush("#F4F4F2"),
                CharacterSpacing = -10,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                TextAlignment = TextAlignment.Center
            },
            Content = contentHost,
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.None,
            PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"],
            CloseButtonStyle = (Style)Application.Current.Resources["OutlineButtonStyle"],
            Background = Brush("#111110"),
            BorderBrush = Brush("#2D2D2A"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            Width = compact ? 460 : double.NaN,
            MinWidth = compact ? 0 : 520,
            MaxWidth = compact ? 460 : double.PositiveInfinity,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Height = double.NaN,
            MinHeight = 0,
            MaxHeight = double.PositiveInfinity
        };
        if (content is TextBlock message)
        {
            message.HorizontalAlignment = HorizontalAlignment.Stretch;
            message.TextAlignment = TextAlignment.Center;
        }

        // ContentDialog routes some pointer events through its popup chrome instead of the
        // dialog object itself, so listen on the actual content host as well. On release,
        // move focus to an invisible neutral control. Doing this on release prevents the
        // TextBox from immediately reclaiming focus later in the same pointer gesture.
        void DismissTextFocus(PointerRoutedEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && IsWithin<TextBox>(source)) return;
            contentHost.DispatcherQueue.TryEnqueue(() =>
            {
                focusSink.Focus(FocusState.Programmatic);
            });
        }
        contentHost.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => DismissTextFocus(e)), true);
        contentHost.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) => DismissTextFocus(e)), true);
        dialog.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) => DismissTextFocus(e)), true);

        dialog.Loaded += (_, _) =>
        {
            var titleHost = Descendants<ContentControl>(dialog).FirstOrDefault(control => control.Name == "Title");
            if (titleHost is not null)
            {
                titleHost.HorizontalAlignment = HorizontalAlignment.Stretch;
                titleHost.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            }

            var primaryButton = Descendants<Button>(dialog).FirstOrDefault(button => button.Name == "PrimaryButton");
            var closeButton = Descendants<Button>(dialog).FirstOrDefault(button => button.Name == "CloseButton");
            if (primaryButton is not null) primaryButton.Margin = new Thickness(0, 0, 10, 0);
            if (closeButton is not null) closeButton.Margin = new Thickness(10, 0, 0, 0);

            // Editor dialogs inherit the notebook accent instead of falling back to A-Note orange.
            if (_currentNotebook is not null)
            {
                dialog.BorderBrush = Brush(_editorAccent);
                if (primaryButton is not null)
                {
                    primaryButton.Background = Brush(_editorAccent);
                    primaryButton.BorderBrush = Brush(_editorAccent);
                    primaryButton.Foreground = Brush("#070706");
                }
                if (closeButton is not null)
                {
                    closeButton.Background = Brush("#00111110");
                    closeButton.BorderBrush = Brush(_editorAccent);
                    closeButton.Foreground = Brush(_editorAccent);
                }
            }
        };
        return dialog;
    }
    private static Style FlatButtonStyle() => (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
          <Setter Property="Background" Value="Transparent"/>
          <Setter Property="BorderBrush" Value="Transparent"/>
          <Setter Property="BorderThickness" Value="0"/>
          <Setter Property="HorizontalContentAlignment" Value="Center"/>
          <Setter Property="VerticalContentAlignment" Value="Center"/>
          <Setter Property="Template">
            <Setter.Value>
              <ControlTemplate TargetType="Button">
                <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="{TemplateBinding CornerRadius}" Padding="{TemplateBinding Padding}">
                  <ContentPresenter Content="{TemplateBinding Content}" HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}" Margin="0,-1,0,1"/>
                </Border>
              </ControlTemplate>
            </Setter.Value>
          </Setter>
        </Style>
        """);

    private Button ActionButton(string text, RoutedEventHandler handler, bool accent = false)
    {
        var b = new Button { Content = text };
        if (accent) b.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        b.Click += handler; return b;
    }
    private static TextBox DialogTextBox(string placeholder, string text = "", int maxLength = 60)
    {
        return new NoClearTextBox
        {
            Text = text,
            PlaceholderText = placeholder,
            MaxLength = maxLength,
            Height = 44,
            MinHeight = 44,
            MaxHeight = 44,
            AcceptsReturn = false,
            TextWrapping = TextWrapping.NoWrap,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources[typeof(TextBox)]
        };
    }

    private AccentPickerControl AccentPicker(string selected)
    {
        var usedColors = _notebooks
            .OrderByDescending(n => n.ModifiedAt)
            .Select(n => n.Accent)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(value =>
            {
                try { return ColorFromHex(value); }
                catch { return Colors.Transparent; }
            })
            .Where(color => color.A != 0)
            .ToList();

        var picker = new AccentPickerControl(ColorFromHex(selected), usedColors);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, "Notebook color");
        return picker;
    }
    private static string SelectedAccent(AccentPickerControl picker) => $"#{picker.SelectedColor.R:X2}{picker.SelectedColor.G:X2}{picker.SelectedColor.B:X2}";

    private sealed class AccentPickerControl : UserControl
    {
        private readonly Border _preview;
        private readonly TextBox _hexInput;
        private readonly Grid _colorField;
        private readonly Microsoft.UI.Xaml.Shapes.Ellipse _indicator;
        private double _hue;
        private double _huePosition;
        private double _saturation;
        private double _value;
        private Color _selectedColor;
        private bool _updatingHex;
        private uint? _capturedPointerId;

        public Color SelectedColor => _selectedColor;

        public AccentPickerControl(Color initialColor, IReadOnlyList<Color> usedColors)
        {
            HorizontalAlignment = HorizontalAlignment.Stretch;
            MinWidth = 420;
            IsTabStop = false;

            _preview = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(initialColor), BorderBrush = Brush("#F4F4F2"), BorderThickness = new Thickness(2) };
            _hexInput = new NoClearTextBox
            {
                Text = $"{initialColor.R:X2}{initialColor.G:X2}{initialColor.B:X2}",
                PlaceholderText = "FF7A18",
                MaxLength = 6,
                Style = (Style)Application.Current.Resources[typeof(TextBox)],
                Width = 132,
                Height = 40,
                MinHeight = 40,
                MaxHeight = 40,
                AcceptsReturn = false,
                TextWrapping = TextWrapping.NoWrap,
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_hexInput, "Hex color");
            var summary = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            summary.Children.Add(_preview);
            summary.Children.Add(new TextBlock
            {
                Text = "#",
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = Brush("#92928C"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, -6, 0)
            });
            summary.Children.Add(_hexInput);

            var hsv = RgbToHsv(initialColor);
            _hue = hsv.Hue;
            _huePosition = _hue / 360d;
            _saturation = hsv.Saturation;
            _value = hsv.Value;

            _colorField = new Grid { Height = 154, HorizontalAlignment = HorizontalAlignment.Stretch, Background = HueGradient() };
            _colorField.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Fill = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(.5, 0),
                    EndPoint = new Windows.Foundation.Point(.5, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = Color.FromArgb(0, 0, 0, 0), Offset = 0 },
                        new GradientStop { Color = Colors.Black, Offset = 1 }
                    }
                }
            });
            _indicator = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 18, Height = 18, Stroke = Brush("#F4F4F2"), StrokeThickness = 3, Fill = Brush("#22000000"), IsHitTestVisible = false };
            var indicatorCanvas = new Canvas { IsHitTestVisible = false };
            indicatorCanvas.Children.Add(_indicator);
            _colorField.Children.Add(indicatorCanvas);
            var fieldFrame = new Border
            {
                BorderBrush = Brush("#2D2D2A"), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(3),
                Child = _colorField
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(fieldFrame, "Color field");
            fieldFrame.PointerPressed += ColorFieldPointerPressed;
            fieldFrame.PointerMoved += ColorFieldPointerMoved;
            fieldFrame.PointerReleased += ColorFieldPointerReleased;
            fieldFrame.PointerCanceled += ColorFieldPointerCanceled;
            fieldFrame.PointerCaptureLost += ColorFieldPointerCaptureLost;
            fieldFrame.SizeChanged += (_, _) => PositionIndicator();

            var layout = new StackPanel { Spacing = 12 };
            layout.Children.Add(summary);

            if (usedColors.Count > 0)
            {
                var history = new StackPanel { Spacing = 7 };
                history.Children.Add(new TextBlock
                {
                    Text = "Used colors",
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = Brush("#92928C")
                });
                var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                foreach (var color in usedColors)
                {
                    var swatch = new Button
                    {
                        Width = 30, Height = 30, MinWidth = 30, MinHeight = 30,
                        Padding = new Thickness(0),
                        Background = new SolidColorBrush(color),
                        BorderBrush = Brush("#F4F4F2"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(3),
                        IsTabStop = false
                    };
                    ToolTipService.SetToolTip(swatch, $"#{color.R:X2}{color.G:X2}{color.B:X2}");
                    swatch.Click += (_, _) => SetColorFromRgb(color, updateHex: true);
                    swatches.Children.Add(swatch);
                }
                history.Children.Add(swatches);
                layout.Children.Add(history);
            }

            layout.Children.Add(fieldFrame);
            Content = new Border
            {
                Background = Brush("#111110"),
                BorderBrush = Brush("#2D2D2A"),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(14),
                Child = layout
            };

            _hexInput.BeforeTextChanging += (_, e) =>
            {
                e.Cancel = e.NewText.Length > 6 || e.NewText.Any(c => !Uri.IsHexDigit(c));
            };
            _hexInput.TextChanged += HexInputTextChanged;
            UpdateColor();
        }

        private void ColorFieldPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            Focus(FocusState.Pointer);

            if (sender is not Border field || _capturedPointerId is not null)
                return;

            if (!field.CapturePointer(e.Pointer))
                return;

            _capturedPointerId = e.Pointer.PointerId;
            UpdateColorFromPointer(e);
            e.Handled = true;
        }

        private void ColorFieldPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_capturedPointerId != e.Pointer.PointerId)
                return;

            UpdateColorFromPointer(e);
            e.Handled = true;
        }

        private void ColorFieldPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Border field || _capturedPointerId != e.Pointer.PointerId)
                return;

            UpdateColorFromPointer(e);
            _capturedPointerId = null;
            field.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }

        private void ColorFieldPointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Border field || _capturedPointerId != e.Pointer.PointerId)
                return;

            _capturedPointerId = null;
            field.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }

        private void ColorFieldPointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            if (_capturedPointerId == e.Pointer.PointerId)
                _capturedPointerId = null;
        }

        private void UpdateColorFromPointer(PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(_colorField).Position;
            var width = Math.Max(1, _colorField.ActualWidth);
            var height = Math.Max(1, _colorField.ActualHeight);
            var x = Math.Clamp(point.X, 0, width);
            var y = Math.Clamp(point.Y, 0, height);

            _huePosition = x / width;
            _hue = _huePosition * 360d;
            _saturation = 1;
            _value = 1 - (y / height);
            UpdateColor();
        }

        private void UpdateColor()
        {
            _selectedColor = HsvToRgb(_hue, _saturation, _value);
            _preview.Background = new SolidColorBrush(_selectedColor);

            var hex = $"{_selectedColor.R:X2}{_selectedColor.G:X2}{_selectedColor.B:X2}";
            if (!string.Equals(_hexInput.Text, hex, StringComparison.OrdinalIgnoreCase))
            {
                _updatingHex = true;
                _hexInput.TextChanged -= HexInputTextChanged;
                _hexInput.Text = hex;
                _hexInput.TextChanged += HexInputTextChanged;
                _updatingHex = false;
            }

            PositionIndicator();
        }

        private void HexInputTextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_updatingHex) ApplyHexInput();
        }

        private void ApplyHexInput()
        {
            if (_updatingHex) return;

            var value = _hexInput.Text.Trim();
            if (value.Length == 0) return;

            // A partial edit is still meaningful: pad the not-yet-entered trailing digits
            // with zeroes so every keystroke immediately updates the preview and picker.
            // Once all six digits are present, this is the exact entered RGB value.
            var normalized = value.PadRight(6, '0');
            if (!int.TryParse(normalized, System.Globalization.NumberStyles.HexNumber, null, out var rgb)) return;

            var color = Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            SetColorFromRgb(color, updateHex: false);
        }

        private void SetColorFromRgb(Color color, bool updateHex)
        {
            _selectedColor = color;
            var hsv = RgbToHsv(_selectedColor);
            if (hsv.Saturation > 0)
            {
                _hue = hsv.Hue;
                _huePosition = _hue / 360d;
            }
            _saturation = hsv.Saturation;
            _value = hsv.Value;
            _preview.Background = new SolidColorBrush(_selectedColor);

            if (updateHex)
            {
                var hex = $"{_selectedColor.R:X2}{_selectedColor.G:X2}{_selectedColor.B:X2}";
                _updatingHex = true;
                _hexInput.TextChanged -= HexInputTextChanged;
                _hexInput.Text = hex;
                _hexInput.SelectionStart = _hexInput.Text.Length;
                _hexInput.TextChanged += HexInputTextChanged;
                _updatingHex = false;
            }

            PositionIndicator();
        }

        private void PositionIndicator()
        {
            if (_colorField.ActualWidth <= 0 || _colorField.ActualHeight <= 0) return;
            var x = Math.Clamp(_huePosition, 0, 1) * _colorField.ActualWidth;
            var y = Math.Clamp(1 - _value, 0, 1) * _colorField.ActualHeight;
            Canvas.SetLeft(_indicator, x - _indicator.Width / 2);
            Canvas.SetTop(_indicator, y - _indicator.Height / 2);
        }

        private static LinearGradientBrush HueGradient()
        {
            var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, .5), EndPoint = new Windows.Foundation.Point(1, .5) };
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 255, 0, 0), Offset = 0 });
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 255, 255, 0), Offset = 1d / 6 });
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 0, 255, 0), Offset = 2d / 6 });
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 0, 255, 255), Offset = 3d / 6 });
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 0, 0, 255), Offset = 4d / 6 });
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 255, 0, 255), Offset = 5d / 6 });
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 255, 0, 0), Offset = 1 });
            return brush;
        }

        private static (double Hue, double Saturation, double Value) RgbToHsv(Color color)
        {
            var r = color.R / 255d;
            var g = color.G / 255d;
            var b = color.B / 255d;
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var delta = max - min;
            var hue = delta == 0 ? 0 : max == r ? 60 * (((g - b) / delta) % 6) : max == g ? 60 * (((b - r) / delta) + 2) : 60 * (((r - g) / delta) + 4);
            if (hue < 0) hue += 360;
            return (hue, max == 0 ? 0 : delta / max, max);
        }

        private static Color HsvToRgb(double hue, double saturation, double value)
        {
            var chroma = value * saturation;
            var x = chroma * (1 - Math.Abs((hue / 60d % 2) - 1));
            var m = value - chroma;
            (double r, double g, double b) = hue switch
            {
                < 60 => (chroma, x, 0d),
                < 120 => (x, chroma, 0d),
                < 180 => (0d, chroma, x),
                < 240 => (0d, x, chroma),
                < 300 => (x, 0d, chroma),
                _ => (chroma, 0d, x)
            };
            return Color.FromArgb(255, (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }
    }
    private static Color ColorFromHex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 8) hex = hex[2..];
        return Color.FromArgb(255, Convert.ToByte(hex[..2], 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16));
    }
    private static StackPanel Labeled(string label, UIElement control)
    {
        var panel = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontFamily = (FontFamily)Application.Current.Resources["DisplayFontFamily"],
            Foreground = Brush("#C7C7C1"),
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            CharacterSpacing = 20
        });
        panel.Children.Add(control);
        return panel;
    }
    private static SolidColorBrush Brush(string hex)
    {
        hex = hex.TrimStart('#'); byte a = 255; if (hex.Length == 8) { a = Convert.ToByte(hex[..2], 16); hex = hex[2..]; }
        return new SolidColorBrush(Color.FromArgb(a, Convert.ToByte(hex[..2], 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16)));
    }
    private static bool IsWithin<T>(DependencyObject source) where T : DependencyObject
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T) return true;
        return false;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private IntPtr WindowSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, UIntPtr subclassId, UIntPtr referenceData)
    {
        if (message == WmGetMinMaxInfo)
        {
            var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            var dpi = GetDpiForWindow(window);
            var scale = (dpi == 0 ? 96d : dpi) / 96d;
            info.MinimumTrackSize.X = (int)Math.Ceiling(MinimumWindowWidth * scale);
            info.MinimumTrackSize.Y = (int)Math.Ceiling(MinimumWindowHeight * scale);
            Marshal.StructureToPtr(info, lParam, false);
            return IntPtr.Zero;
        }

        return DefSubclassProc(window, message, wParam, lParam);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, UIntPtr subclassId, UIntPtr referenceData);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaximumSize;
        public NativePoint MaximumPosition;
        public NativePoint MinimumTrackSize;
        public NativePoint MaximumTrackSize;
    }

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassProc callback, UIntPtr subclassId, UIntPtr referenceData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr deviceContext, int index);

    private sealed class PageView : IDisposable
    {
        private readonly DebouncedAutosave _autosave;
        private readonly DatabaseService _db;
        private int _dirtyRevision;
        private int _savedRevision;
        public NotePage Page { get; }
        public InkPageControl Ink { get; }
        public Border ScaleBorder { get; }
        public UIElement Root { get; }
        public TextBlock PageNumber { get; }
        private Task? _inkLoadTask;
        public bool InkLoaded { get; private set; }
        public PageView(NotePage page, InkPageControl ink, Border scaleBorder, UIElement root, TextBlock pageNumber, bool inkLoaded)
        {
            Page = page; Ink = ink; ScaleBorder = scaleBorder; Root = root; PageNumber = pageNumber;
            InkLoaded = inkLoaded;
            Ink.IsHitTestVisible = inkLoaded;
            _db = ((MainWindow)App.MainWindowInstance!)._db;
            _autosave = new DebouncedAutosave(() => SaveAsync());
        }
        public Task EnsureInkLoadedAsync() => InkLoaded ? Task.CompletedTask : _inkLoadTask ??= LoadInkAsync();
        private async Task LoadInkAsync()
        {
            Page.Strokes = await InkFileService.LoadAsync(Page.Id);
            Page.Photos = await PhotoFileService.LoadAsync(Page.Id);
            await Ink.RefreshInkAsync();
            InkLoaded = true;
            var window = (MainWindow)App.MainWindowInstance!;
            Ink.PenInputEnabled = !window._penScrollMode;
            Ink.IsHitTestVisible = !window._penScrollMode;
        }
        public void Dirty() { Page.ModifiedAt = DateTimeOffset.Now; ++_dirtyRevision; _autosave.Request(); }
        private async Task SaveAsync()
        {
            var revision = _dirtyRevision;
            if (revision == _savedRevision) return;
            await _db.SavePageAsync(Page, InkLoaded);
            _savedRevision = revision;
            if (_dirtyRevision != revision) _autosave.Request();
        }
        public Task FlushAsync(DatabaseService db) => SaveAsync();
        public void Dispose() => _autosave.Dispose();
    }

}

internal static class InkPageExtensions
{
    private static readonly Dictionary<InkPageControl, Action> DirtyActions = [];
    public static void RegisterDirty(this InkPageControl ink, Action action) => DirtyActions[ink] = action;
    public static void TagAsDirty(this InkPageControl ink) { if (DirtyActions.TryGetValue(ink, out var action)) action(); }
}
