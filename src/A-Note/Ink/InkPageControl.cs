using ANote.Models;
using ANote.Services;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Storage;
using Windows.UI;

namespace ANote.Ink;

public sealed class InkPageControl : UserControl
{
    public const double PaperWidth = 1280;
    public const double PaperHeight = 720;
    private readonly Canvas _paperPattern = new() { IsHitTestVisible = false };
    private readonly Canvas _photoCanvas = new() { IsHitTestVisible = false };
    private readonly Canvas _highlightCanvas = new() { IsHitTestVisible = false };
    private readonly Canvas _inkCanvas = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly Canvas _photoUiCanvas = new() { IsHitTestVisible = true };
    private readonly PathGeometry _highlightGeometry = new();
    private readonly Microsoft.UI.Xaml.Shapes.Path _highlightPath;
    private PolyLineSegment? _activeHighlightSegment;
    private readonly PathGeometry[] _penGeometries = [new(), new(), new(), new()];
    private readonly Microsoft.UI.Xaml.Shapes.Path[] _penPaths = new Microsoft.UI.Xaml.Shapes.Path[4];
    private PolyLineSegment? _activePenSegment;
    private int _activePenBucket = -1;
    private readonly Canvas _selectionInkCanvas = new() { IsHitTestVisible = false };
    private readonly TranslateTransform _selectionInkTransform = new();
    private readonly Canvas _selectionCanvas = new() { IsHitTestVisible = true };
    private readonly Border _selection = new() { BorderBrush = new SolidColorBrush(Color.FromArgb(210, 255, 122, 24)), BorderThickness = new Thickness(1), Background = new SolidColorBrush(Color.FromArgb(18, 255, 122, 24)), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Button _selectionDeleteButton = new()
    {
        Width = 34,
        Height = 34,
        MinWidth = 0,
        MinHeight = 0,
        Padding = new Thickness(0),
        Visibility = Visibility.Collapsed,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Content = new FontIcon
        {
            Glyph = "\uE74D",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }
    };
    private readonly Border _photoSelection = new()
    {
        BorderThickness = new Thickness(2),
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = true,
        Background = new SolidColorBrush(Colors.Transparent)
    };
    private readonly Button _photoDeleteButton = FloatingPhotoButton("\uE74D");
    private readonly Button _photoRotateButton = FloatingPhotoButton("\uE7AD");
    private readonly Button _photoScaleButton = FloatingPhotoButton("\uE740");
    private readonly Dictionary<string, FrameworkElement> _photoElements = [];
    private PagePhotoData? _selectedPhoto;
    private FrameworkElement? _photoDragElement;
    private uint _photoPointerId;
    private Point _photoDragStart;
    private double _photoStartX;
    private double _photoStartY;
    private bool _photoScaling;
    private bool _photoRotating;
    private double _photoScaleStartWidth;
    private double _photoScaleStartHeight;
    private double _photoScaleAspect = 1;
    private double _photoScaleStartDistance;
    private Point _photoScaleCenter;
    private double _photoRotationStartAngle;
    private double _photoRotationStartValue;
    private readonly Dictionary<uint, Point> _photoTouches = [];
    private bool _photoTouchGesture;
    private double _photoTouchStartDistance;
    private double _photoTouchStartAngle;
    private double _photoTouchStartWidth;
    private double _photoTouchStartHeight;
    private double _photoTouchStartRotation;
    private Point _photoTouchStartCenter;
    private Point _photoTouchStartMidpoint;
    private double _photoManipulationRawRotation;
    private bool _photoManipulationActive;
    private readonly Point[] _photoButtonTargets = new Point[3];
    private bool _photoButtonsHavePosition;
    private readonly Stack<InkStrokeData> _redo = new();
    private readonly Stack<string> _sessionUndoIds = new();
    private readonly HashSet<string> _selectedIds = [];
    private InkStrokeData? _activeStroke;
    private Point _lastPoint;
    private Point _lassoOrigin;
    private bool _isPointerDown;
    private bool _movingSelection;
    private double _selectionDragX;
    private double _selectionDragY;
    private Point _deleteButtonTarget;
    private bool _deleteButtonHasPosition;
    private bool _cursorHiddenForPen;
    private string _accentColor = "#FF7A18";
    private double _ruledSpacingX = 32;
    private double _ruledSpacingY = 32;
    private double _dotSpacingX = 24;
    private double _dotSpacingY = 24;
    private double _paperMargin = 72;
    private bool _patternDrawn;

    public NotePage Page { get; }
    public InkTool Tool { get; set; } = InkTool.Pen;
    public InkTool EraserTarget { get; set; } = InkTool.Pen;
    public string HighlighterColor { get; set; } = "#FF7A18";
    public bool MouseDrawingEnabled { get; set; }
    public bool PenInputEnabled { get; set; } = true;
    public event EventHandler? InkChanged;
    public event EventHandler<bool>? WritingStateChanged;
    public event EventHandler? PenActivity;
    public event EventHandler? SelectionChanged;
    public event EventHandler? HistoryChanged;

    public bool HasSelection => _selectedIds.Count > 0;
    public bool IsPhotoInteractionActive => _photoManipulationActive || _photoDragElement is not null || _photoTouches.Count > 0 || _photoRotating || _photoScaling;
    public bool CanUndo => _sessionUndoIds.Any(id => Page.Strokes.Any(stroke => stroke.Id == id));
    public bool CanRedo => _redo.Count > 0;

    [DllImport("user32.dll")]
    private static extern int ShowCursor(bool bShow);

    public InkPageControl(NotePage page)
    {
        Page = page;
        Width = PaperWidth;
        Height = PaperHeight;
        var root = new Grid
        {
            Width = PaperWidth,
            Height = PaperHeight,
            Background = new SolidColorBrush(Color.FromArgb(255, 10, 10, 9))
        };
        root.Children.Add(_paperPattern);
        root.Children.Add(_photoCanvas);
        // One retained Path is used for every highlight on the page. Appending points to the
        // active figure avoids rebuilding all previous highlights on every pen move.
        _highlightPath = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = _highlightGeometry,
            StrokeThickness = 15,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false
        };
        _highlightCanvas.Children.Add(_highlightPath);
        root.Children.Add(_highlightCanvas);
        for (var bucket = 0; bucket < 4; bucket++)
        {
            _penPaths[bucket] = new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = _penGeometries[bucket],
                Stroke = new SolidColorBrush(Color.FromArgb(255, 239, 239, 234)),
                StrokeThickness = 2.3 * (0.55 + (bucket + .5) / 4d * .8),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                IsHitTestVisible = false
            };
            _inkCanvas.Children.Add(_penPaths[bucket]);
        }
        root.Children.Add(_inkCanvas);
        _selectionInkCanvas.RenderTransform = _selectionInkTransform;
        root.Children.Add(_selectionInkCanvas);
        _selectionCanvas.Children.Add(_selection);
        _selectionCanvas.Children.Add(_selectionDeleteButton);
        root.Children.Add(_selectionCanvas);
        _photoUiCanvas.Children.Add(_photoSelection);
        _photoUiCanvas.Children.Add(_photoDeleteButton);
        _photoUiCanvas.Children.Add(_photoRotateButton);
        _photoUiCanvas.Children.Add(_photoScaleButton);
        root.Children.Add(_photoUiCanvas);
        Loaded += (_, _) =>
        {
            if (Application.Current.Resources["AccentButtonStyle"] is Style buttonStyle)
            {
                _selectionDeleteButton.Style = buttonStyle;
                _photoDeleteButton.Style = buttonStyle;
                _photoRotateButton.Style = buttonStyle;
                _photoScaleButton.Style = buttonStyle;
            }
            ApplySelectionDeleteAccent();
            ApplyPhotoAccent();
        };
        _selectionDeleteButton.Click += (_, _) => DeleteSelection();
        _photoDeleteButton.Click += async (_, _) => await DeleteSelectedPhotoAsync();
        ConfigureFloatingButtonInput(_selectionDeleteButton);
        ConfigureFloatingButtonInput(_photoDeleteButton);
        ConfigureFloatingButtonInput(_photoRotateButton);
        ConfigureFloatingButtonInput(_photoScaleButton);
        _photoRotateButton.AddHandler(PointerPressedEvent, new PointerEventHandler(StartPhotoRotate), true);
        _photoRotateButton.AddHandler(PointerMovedEvent, new PointerEventHandler(MovePhotoRotate), true);
        _photoRotateButton.AddHandler(PointerReleasedEvent, new PointerEventHandler(EndPhotoRotate), true);
        _photoRotateButton.AddHandler(PointerCanceledEvent, new PointerEventHandler(EndPhotoRotate), true);
        _photoRotateButton.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(EndPhotoRotate), true);
        _photoScaleButton.AddHandler(PointerPressedEvent, new PointerEventHandler(StartPhotoScale), true);
        _photoScaleButton.AddHandler(PointerMovedEvent, new PointerEventHandler(MovePhotoScale), true);
        _photoScaleButton.AddHandler(PointerReleasedEvent, new PointerEventHandler(EndPhotoScale), true);
        _photoScaleButton.AddHandler(PointerCanceledEvent, new PointerEventHandler(EndPhotoScale), true);
        _photoScaleButton.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(EndPhotoScale), true);
        _photoSelection.ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY | ManipulationModes.Scale | ManipulationModes.Rotate;
        // The selected photo is a direct-manipulation surface. Mark its touch stream handled
        // so the parent ScrollViewer cannot steal one finger and break the second-finger
        // pinch/twist recognizer. XAML manipulation processing still receives these pointers.
        _photoSelection.AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (e.Pointer.PointerDeviceType != PointerDeviceType.Touch) return;
            _photoSelection.CapturePointer(e.Pointer);
            e.Handled = true;
        }), true);
        _photoSelection.AddHandler(PointerMovedEvent, new PointerEventHandler((_, e) =>
        {
            if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch) e.Handled = true;
        }), true);
        _photoSelection.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, e) =>
        {
            if (e.Pointer.PointerDeviceType != PointerDeviceType.Touch) return;
            _photoSelection.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }), true);
        _photoSelection.AddHandler(PointerCanceledEvent, new PointerEventHandler((_, e) =>
        {
            if (e.Pointer.PointerDeviceType != PointerDeviceType.Touch) return;
            _photoSelection.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }), true);
        _photoSelection.ManipulationStarting += OnPhotoManipulationStarting;
        _photoSelection.ManipulationDelta += OnPhotoManipulationDelta;
        _photoSelection.ManipulationCompleted += OnPhotoManipulationCompleted;
        Content = root;
        Loaded += (_, _) => { if (!_patternDrawn) DrawPaper(); };
        _inkCanvas.ManipulationMode = ManipulationModes.None;
        _inkCanvas.IsTapEnabled = false;
        _inkCanvas.IsDoubleTapEnabled = false;
        _inkCanvas.IsRightTapEnabled = false;
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnInkPointerPressed), true);
        AddHandler(PointerMovedEvent, new PointerEventHandler(OnInkPointerMoved), true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler(OnInkPointerReleased), true);
        AddHandler(PointerCanceledEvent, new PointerEventHandler(OnInkPointerReleased), true);
        AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnInkPointerReleased), true);
        AddHandler(PointerEnteredEvent, new PointerEventHandler(OnAnyPointerActivity), true);
        AddHandler(PointerMovedEvent, new PointerEventHandler(OnAnyPointerActivity), true);
        AddHandler(PointerExitedEvent, new PointerEventHandler(OnPointerExited), true);
        Loaded += (_, _) => CompositionTarget.Rendering += OnSelectionUiRendering;
        Unloaded += (_, _) =>
        {
            CompositionTarget.Rendering -= OnSelectionUiRendering;
            RestorePointerCursor();
        };
    }

    public void SetAccentColor(string hex)
    {
        _accentColor = string.IsNullOrWhiteSpace(hex) ? "#FF7A18" : hex;
        HighlighterColor = _accentColor;
        var color = ParseColor(_accentColor, 255);
        _selection.BorderBrush = new SolidColorBrush(Color.FromArgb(220, color.R, color.G, color.B));
        _selection.Background = new SolidColorBrush(Color.FromArgb(20, color.R, color.G, color.B));
        ApplySelectionDeleteAccent();
        ApplyPhotoAccent();
        RenderAll();
    }

    private void ApplySelectionDeleteAccent()
    {
        var color = ParseColor(_accentColor, 255);
        _selectionDeleteButton.Background = new SolidColorBrush(color);
        _selectionDeleteButton.BorderBrush = new SolidColorBrush(color);
        _selectionDeleteButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 7, 7, 6));
        _selectionDeleteButton.BorderThickness = new Thickness(0);
    }

    private static Button FloatingPhotoButton(string glyph)
    {
        return new Button
        {
            Width = 34,
            Height = 34,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Visibility = Visibility.Collapsed,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new FontIcon
            {
                Glyph = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    private static void ConfigureFloatingButtonInput(Button button)
    {
        // A floating control is an interaction surface, never a scroll gesture. Capture its
        // pointer immediately so the parent ListView/ScrollViewer cannot start a manipulation
        // when the pen/finger is held and dragged on the button.
        button.ManipulationMode = ManipulationModes.None;
        button.IsHoldingEnabled = false;
        button.PointerPressed += (_, e) =>
        {
            button.CapturePointer(e.Pointer);
            e.Handled = true;
        };
        button.PointerMoved += (_, e) => e.Handled = true;
        button.PointerReleased += (_, e) =>
        {
            button.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        };
        button.PointerCanceled += (_, e) =>
        {
            button.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        };
    }

    private void ApplyPhotoAccent()
    {
        var color = ParseColor(_accentColor, 255);
        _photoSelection.BorderBrush = new SolidColorBrush(Color.FromArgb(230, color.R, color.G, color.B));
        foreach (var button in new[] { _photoDeleteButton, _photoRotateButton, _photoScaleButton })
        {
            button.Background = new SolidColorBrush(color);
            button.BorderBrush = new SolidColorBrush(color);
            button.Foreground = new SolidColorBrush(Color.FromArgb(255, 7, 7, 6));
            button.BorderThickness = new Thickness(0);
        }
    }

    public async Task AddPhotoAsync(StorageFile file)
    {
        var photo = await PhotoFileService.ImportAsync(Page.Id, file);
        Page.Photos.Add(photo);
        await AddOrRefreshPhotoElementAsync(photo);
        SelectPhoto(photo);
        await PhotoFileService.SaveAtomicAsync(Page.Id, Page.Photos);
        InkChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RefreshPhotosAsync()
    {
        _photoCanvas.Children.Clear();
        _photoElements.Clear();
        foreach (var photo in Page.Photos)
            await AddOrRefreshPhotoElementAsync(photo);
        if (_selectedPhoto is not null && Page.Photos.All(p => p.Id != _selectedPhoto.Id))
            ClearPhotoSelection();
        else
            PositionPhotoUi();
    }

    private async Task AddOrRefreshPhotoElementAsync(PagePhotoData photo)
    {
        var path = PhotoFileService.PhotoPath(photo);
        if (!File.Exists(path)) return;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            ImageSource source;
            if (string.Equals(System.IO.Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(System.IO.Path.GetExtension(path), ".svgz", StringComparison.OrdinalIgnoreCase))
            {
                var svg = new SvgImageSource();
                await svg.SetSourceAsync(stream);
                source = svg;
            }
            else
            {
                var bitmap = new BitmapImage
                {
                    // Decode close to display size so a phone photo does not become a giant
                    // full-resolution texture that makes page scrolling stutter.
                    DecodePixelWidth = (int)Math.Clamp(Math.Ceiling(photo.Width * 2), 96, 1600)
                };
                await bitmap.SetSourceAsync(stream);
                source = bitmap;
            }
            var image = new Image
            {
                Source = source,
                Stretch = Stretch.UniformToFill,
                IsHitTestVisible = false
            };
            var border = new Border
            {
                Child = image,
                Width = photo.Width,
                Height = photo.Height,
                CornerRadius = new CornerRadius(2),
                Clip = new RectangleGeometry { Rect = new Rect(0, 0, photo.Width, photo.Height) },
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform { Angle = photo.Rotation },
                IsHitTestVisible = false
            };
            Canvas.SetLeft(border, photo.X);
            Canvas.SetTop(border, photo.Y);
            _photoCanvas.Children.Add(border);
            _photoElements[photo.Id] = border;
        }
        catch { }
    }

    private static bool PhotoContainsPoint(PagePhotoData photo, Point point)
    {
        var centerX = photo.X + photo.Width / 2d;
        var centerY = photo.Y + photo.Height / 2d;
        var radians = -photo.Rotation * Math.PI / 180d;
        var dx = point.X - centerX;
        var dy = point.Y - centerY;
        var localX = dx * Math.Cos(radians) - dy * Math.Sin(radians) + centerX;
        var localY = dx * Math.Sin(radians) + dy * Math.Cos(radians) + centerY;
        return new Rect(photo.X, photo.Y, photo.Width, photo.Height).Contains(new Point(localX, localY));
    }

    private PagePhotoData? HitTestPhoto(Point point)
    {
        for (var i = Page.Photos.Count - 1; i >= 0; i--)
        {
            var photo = Page.Photos[i];
            if (PhotoContainsPoint(photo, point)) return photo;
        }
        return null;
    }

    private void SelectPhoto(PagePhotoData photo)
    {
        var hadInkSelection = _selectedIds.Count > 0;
        _selectedPhoto = photo;
        _selectedIds.Clear();
        _selection.Visibility = Visibility.Collapsed;
        _selectionDeleteButton.Visibility = Visibility.Collapsed;
        _deleteButtonHasPosition = false;
        if (hadInkSelection) RenderAll();
        PositionPhotoUi();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearPhotoSelection()
    {
        _selectedPhoto = null;
        _photoDragElement = null;
        _photoTouches.Clear();
        _photoTouchGesture = false;
        _photoManipulationActive = false;
        _photoButtonsHavePosition = false;
        _photoSelection.Visibility = Visibility.Collapsed;
        _photoDeleteButton.Visibility = Visibility.Collapsed;
        _photoRotateButton.Visibility = Visibility.Collapsed;
        _photoScaleButton.Visibility = Visibility.Collapsed;
    }

    private void PositionPhotoUi()
    {
        if (_selectedPhoto is null)
        {
            ClearPhotoSelection();
            return;
        }

        var photo = _selectedPhoto;
        Canvas.SetLeft(_photoSelection, photo.X);
        Canvas.SetTop(_photoSelection, photo.Y);
        _photoSelection.Width = photo.Width;
        _photoSelection.Height = photo.Height;
        _photoSelection.RenderTransformOrigin = new Point(.5, .5);
        _photoSelection.RenderTransform = new RotateTransform { Angle = photo.Rotation };
        _photoSelection.Visibility = Visibility.Visible;

        // Keep the controls visually attached to the rotated photo itself rather than to its
        // unrotated bounding box. The three target points are cheap to compute (one sin/cos)
        // and the actual buttons glide toward them from CompositionTarget.Rendering.
        var center = SelectedPhotoCenter();
        var radians = photo.Rotation * Math.PI / 180d;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        Point RotateLocal(double x, double y) => new(
            center.X + x * cos - y * sin,
            center.Y + x * sin + y * cos);

        var halfW = photo.Width / 2d;
        var halfH = photo.Height / 2d;
        // Match the lasso delete control exactly: 8 page units between the selection
        // edge and the nearest button edge on both local axes. For rotated photos, rotate
        // that same local offset with the image instead of pushing the button radially.
        // This keeps the visual spacing identical at 0 degrees and consistent while rotating.
        const double gap = 8d;
        Point ButtonTarget(Button button, double localX, double localY)
        {
            var buttonCenter = RotateLocal(localX, localY);
            var x = buttonCenter.X - button.Width / 2d;
            var y = buttonCenter.Y - button.Height / 2d;
            return new Point(
                Math.Clamp(x, 0, PaperWidth - button.Width),
                Math.Clamp(y, 0, PaperHeight - button.Height));
        }

        _photoButtonTargets[0] = ButtonTarget(
            _photoRotateButton,
            -halfW - gap - _photoRotateButton.Width / 2d,
            -halfH - gap - _photoRotateButton.Height / 2d);
        _photoButtonTargets[1] = ButtonTarget(
            _photoDeleteButton,
            halfW + gap + _photoDeleteButton.Width / 2d,
            -halfH - gap - _photoDeleteButton.Height / 2d);
        _photoButtonTargets[2] = ButtonTarget(
            _photoScaleButton,
            halfW + gap + _photoScaleButton.Width / 2d,
            halfH + gap + _photoScaleButton.Height / 2d);

        if (!_photoButtonsHavePosition)
        {
            var buttons = new[] { _photoRotateButton, _photoDeleteButton, _photoScaleButton };
            for (var i = 0; i < buttons.Length; i++)
            {
                Canvas.SetLeft(buttons[i], _photoButtonTargets[i].X);
                Canvas.SetTop(buttons[i], _photoButtonTargets[i].Y);
            }
            _photoButtonsHavePosition = true;
        }

        _photoRotateButton.Visibility = Visibility.Visible;
        _photoDeleteButton.Visibility = Visibility.Visible;
        _photoScaleButton.Visibility = Visibility.Visible;
    }

    private static (double HalfX, double HalfY) RotatedHalfExtents(PagePhotoData photo)
    {
        var radians = photo.Rotation * Math.PI / 180d;
        var cos = Math.Abs(Math.Cos(radians));
        var sin = Math.Abs(Math.Sin(radians));
        return (
            cos * photo.Width / 2d + sin * photo.Height / 2d,
            sin * photo.Width / 2d + cos * photo.Height / 2d);
    }

    private static void ConstrainPhotoToPage(PagePhotoData photo, bool allowScaleDown = true)
    {
        var centerX = photo.X + photo.Width / 2d;
        var centerY = photo.Y + photo.Height / 2d;
        var (halfX, halfY) = RotatedHalfExtents(photo);

        // A large photo can physically become wider than the paper when rotated. In that
        // situation moving it cannot help, so shrink just enough to keep every rotated corner in.
        if (allowScaleDown && (halfX * 2d > PaperWidth || halfY * 2d > PaperHeight))
        {
            var fit = Math.Min(PaperWidth / Math.Max(1d, halfX * 2d), PaperHeight / Math.Max(1d, halfY * 2d));
            fit = Math.Clamp(fit, .05d, 1d);
            photo.Width *= fit;
            photo.Height *= fit;
            halfX *= fit;
            halfY *= fit;
        }

        centerX = Math.Clamp(centerX, halfX, PaperWidth - halfX);
        centerY = Math.Clamp(centerY, halfY, PaperHeight - halfY);
        photo.X = centerX - photo.Width / 2d;
        photo.Y = centerY - photo.Height / 2d;
    }

    private void UpdatePhotoElement(PagePhotoData photo)
    {
        if (!_photoElements.TryGetValue(photo.Id, out var element)) return;
        element.Width = photo.Width;
        element.Height = photo.Height;
        Canvas.SetLeft(element, photo.X);
        Canvas.SetTop(element, photo.Y);
        element.RenderTransformOrigin = new Point(.5, .5);
        if (element.RenderTransform is RotateTransform rotation) rotation.Angle = photo.Rotation;
        else element.RenderTransform = new RotateTransform { Angle = photo.Rotation };
        if (element is Border border)
            border.Clip = new RectangleGeometry { Rect = new Rect(0, 0, photo.Width, photo.Height) };
        PositionPhotoUi();
    }

    private static bool IsWithin(DependencyObject? source, DependencyObject target)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, target)) return true;
        return false;
    }

    private bool TryStartPhotoInteraction(PointerRoutedEventArgs e, Point point)
    {
        var device = e.Pointer.PointerDeviceType;
        var photo = HitTestPhoto(point);

        // Once a photo is selected it owns every pointer interaction inside its rotated bounds.
        // Pen, touch and mouse all use the same direct-manipulation path.
        if (_selectedPhoto is not null && photo?.Id == _selectedPhoto.Id)
        {
            // Touch inside an already-selected photo is owned by the selection overlay's
            // native XAML Manipulation pipeline. That gives reliable one/two-finger
            // translation, pinch-to-scale and twist-to-rotate without fighting ScrollViewer.
            if (device == PointerDeviceType.Touch && e.OriginalSource is DependencyObject touchSource && IsWithin(touchSource, _photoSelection))
                return false;

            _photoDragElement = _photoElements.GetValueOrDefault(_selectedPhoto.Id);
            _photoPointerId = e.Pointer.PointerId;
            _photoDragStart = point;
            _photoStartX = _selectedPhoto.X;
            _photoStartY = _selectedPhoto.Y;
            CapturePointer(e.Pointer);
            e.Handled = true;
            return true;
        }

        // A single press outside the selected photo dismisses it.
        if (_selectedPhoto is not null && photo?.Id != _selectedPhoto.Id)
        {
            ClearPhotoSelection();
            if (photo is null)
            {
                e.Handled = true;
                return true;
            }
        }

        // Any direct pointer can re-select a photo. In particular, the pen must be able to
        // re-select a placed photo regardless of which ink tool is currently active.
        if (photo is null) return false;

        SelectPhoto(photo);
        if (device == PointerDeviceType.Touch)
        {
            // First tap selects. Subsequent touch gestures land on _photoSelection and use
            // the manipulation pipeline above.
            e.Handled = true;
            return true;
        }

        _photoDragElement = _photoElements.GetValueOrDefault(photo.Id);
        _photoPointerId = e.Pointer.PointerId;
        _photoDragStart = point;
        _photoStartX = photo.X;
        _photoStartY = photo.Y;
        CapturePointer(e.Pointer);
        e.Handled = true;
        return true;
    }

    private bool StartPhotoTouch(PointerRoutedEventArgs e, Point point)
    {
        if (_selectedPhoto is null) return false;
        _photoTouches[e.Pointer.PointerId] = point;
        CapturePointer(e.Pointer);

        if (_photoTouches.Count >= 2)
        {
            BeginPhotoTouchGesture();
            _photoDragElement = null;
        }
        else
        {
            _photoTouchGesture = false;
            _photoDragElement = _photoElements.GetValueOrDefault(_selectedPhoto.Id);
            _photoPointerId = e.Pointer.PointerId;
            _photoDragStart = point;
            _photoStartX = _selectedPhoto.X;
            _photoStartY = _selectedPhoto.Y;
        }

        e.Handled = true;
        return true;
    }

    private bool TryGetFirstTwoPhotoTouches(out Point first, out Point second)
    {
        first = default;
        second = default;
        if (_photoTouches.Count < 2) return false;
        var found = 0;
        foreach (var point in _photoTouches.Values)
        {
            if (found++ == 0) first = point;
            else { second = point; return true; }
        }
        return false;
    }

    private void BeginPhotoTouchGesture()
    {
        if (_selectedPhoto is null || _photoTouches.Count < 2) return;
        if (!TryGetFirstTwoPhotoTouches(out var first, out var second)) return;
        _photoTouchGesture = true;
        _photoTouchStartDistance = Math.Max(1, PhotoDistance(first, second));
        _photoTouchStartAngle = AngleDegrees(first, second);
        _photoTouchStartWidth = _selectedPhoto.Width;
        _photoTouchStartHeight = _selectedPhoto.Height;
        _photoTouchStartRotation = _selectedPhoto.Rotation;
        _photoTouchStartCenter = SelectedPhotoCenter();
        _photoTouchStartMidpoint = new Point((first.X + second.X) / 2d, (first.Y + second.Y) / 2d);
        _photoDragElement = null;
    }

    private bool MovePhotoInteraction(PointerRoutedEventArgs e, Point point)
    {
        if (_selectedPhoto is null) return false;

        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch && _photoTouches.ContainsKey(e.Pointer.PointerId))
        {
            _photoTouches[e.Pointer.PointerId] = point;
            if (_photoTouchGesture && _photoTouches.Count >= 2)
            {
                if (!TryGetFirstTwoPhotoTouches(out var first, out var second)) return false;
                var midpoint = new Point((first.X + second.X) / 2d, (first.Y + second.Y) / 2d);
                var currentDistance = Math.Max(1, PhotoDistance(first, second));
                var ratio = Math.Clamp(currentDistance / _photoTouchStartDistance, .15, 8d);

                var width = Math.Max(80, _photoTouchStartWidth * ratio);
                var height = Math.Max(60, _photoTouchStartHeight * ratio);
                width = Math.Min(width, PaperWidth);
                height = Math.Min(height, PaperHeight);

                var currentAngle = AngleDegrees(first, second);
                var rawRotation = NormalizePhotoAngle(_photoTouchStartRotation + NormalizeAngleDelta(currentAngle - _photoTouchStartAngle));
                _selectedPhoto.Rotation = SnapPhotoAngle(rawRotation);

                // Two-finger manipulation follows the fingers' midpoint as well as scale/rotation.
                // Rebase around the current midpoint so it behaves like a phone gallery gesture.
                if (_photoTouches.Count == 2)
                {
                    var centerDx = midpoint.X - _photoTouchStartMidpoint.X;
                    var centerDy = midpoint.Y - _photoTouchStartMidpoint.Y;
                    var targetCenterX = Math.Clamp(_photoTouchStartCenter.X + centerDx, width / 2d, PaperWidth - width / 2d);
                    var targetCenterY = Math.Clamp(_photoTouchStartCenter.Y + centerDy, height / 2d, PaperHeight - height / 2d);
                    _selectedPhoto.Width = width;
                    _selectedPhoto.Height = height;
                    _selectedPhoto.X = targetCenterX - width / 2d;
                    _selectedPhoto.Y = targetCenterY - height / 2d;
                }

                ConstrainPhotoToPage(_selectedPhoto);
                UpdatePhotoElement(_selectedPhoto);
            }
            else if (_photoTouches.Count == 1 && e.Pointer.PointerId == _photoPointerId)
            {
                var dx = point.X - _photoDragStart.X;
                var dy = point.Y - _photoDragStart.Y;
                _selectedPhoto.X = Math.Clamp(_photoStartX + dx, 0, Math.Max(0, PaperWidth - _selectedPhoto.Width));
                _selectedPhoto.Y = _photoStartY + dy;
                ConstrainPhotoToPage(_selectedPhoto, allowScaleDown: false);
                UpdatePhotoElement(_selectedPhoto);
            }
            e.Handled = true;
            return true;
        }

        if (_photoDragElement is null || e.Pointer.PointerId != _photoPointerId) return false;
        var moveX = point.X - _photoDragStart.X;
        var moveY = point.Y - _photoDragStart.Y;
        _selectedPhoto.X = Math.Clamp(_photoStartX + moveX, 0, Math.Max(0, PaperWidth - _selectedPhoto.Width));
        _selectedPhoto.Y = _photoStartY + moveY;
        ConstrainPhotoToPage(_selectedPhoto, allowScaleDown: false);
        UpdatePhotoElement(_selectedPhoto);
        e.Handled = true;
        return true;
    }

    private bool EndPhotoInteraction(PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch && _photoTouches.ContainsKey(e.Pointer.PointerId))
        {
            ReleasePointerCapture(e.Pointer);
            _photoTouches.Remove(e.Pointer.PointerId);

            if (_photoTouches.Count >= 2)
            {
                BeginPhotoTouchGesture();
            }
            else if (_photoTouches.Count == 1 && _selectedPhoto is not null)
            {
                _photoTouchGesture = false;
                var remaining = _photoTouches.First();
                _photoPointerId = remaining.Key;
                _photoDragStart = remaining.Value;
                _photoStartX = _selectedPhoto.X;
                _photoStartY = _selectedPhoto.Y;
                _photoDragElement = _photoElements.GetValueOrDefault(_selectedPhoto.Id);
            }
            else
            {
                _photoTouchGesture = false;
                _photoDragElement = null;
                InkChanged?.Invoke(this, EventArgs.Empty);
            }

            e.Handled = true;
            return true;
        }

        if (_photoDragElement is null || e.Pointer.PointerId != _photoPointerId) return false;
        ReleasePointerCapture(e.Pointer);
        _photoDragElement = null;
        InkChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
        return true;
    }

    private void OnPhotoManipulationStarting(object sender, ManipulationStartingRoutedEventArgs e)
    {
        if (_selectedPhoto is null) return;
        _photoManipulationActive = true;
        _photoManipulationRawRotation = _selectedPhoto.Rotation;
        // Keep the gesture on the photo instead of allowing the parent ScrollViewer to
        // promote it into page scrolling once a second finger arrives.
        e.Handled = true;
    }

    private void OnPhotoManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        if (!_photoManipulationActive || _selectedPhoto is null) return;

        var photo = _selectedPhoto;
        var oldCenter = SelectedPhotoCenter();

        var scale = double.IsFinite(e.Delta.Scale) && e.Delta.Scale > 0 ? e.Delta.Scale : 1d;
        var newWidth = Math.Clamp(photo.Width * scale, 80d, PaperWidth);
        var newHeight = Math.Clamp(photo.Height * scale, 60d, PaperHeight);

        _photoManipulationRawRotation = NormalizePhotoAngle(_photoManipulationRawRotation + e.Delta.Rotation);
        photo.Rotation = SnapPhotoAngle(_photoManipulationRawRotation);

        var centerX = oldCenter.X + e.Delta.Translation.X;
        var centerY = oldCenter.Y + e.Delta.Translation.Y;
        centerX = Math.Clamp(centerX, newWidth / 2d, PaperWidth - newWidth / 2d);
        centerY = Math.Clamp(centerY, newHeight / 2d, PaperHeight - newHeight / 2d);

        photo.Width = newWidth;
        photo.Height = newHeight;
        photo.X = centerX - newWidth / 2d;
        photo.Y = centerY - newHeight / 2d;
        ConstrainPhotoToPage(photo);
        UpdatePhotoElement(photo);
        e.Handled = true;
    }

    private void OnPhotoManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        if (!_photoManipulationActive) return;
        _photoManipulationActive = false;
        InkChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private async Task DeleteSelectedPhotoAsync()
    {
        if (_selectedPhoto is null) return;
        var photo = _selectedPhoto;
        ClearPhotoSelection();
        Page.Photos.RemoveAll(item => item.Id == photo.Id);
        if (_photoElements.Remove(photo.Id, out var element)) _photoCanvas.Children.Remove(element);
        await PhotoFileService.DeletePhotoAsync(photo);
        await PhotoFileService.SaveAtomicAsync(Page.Id, Page.Photos);
        InkChanged?.Invoke(this, EventArgs.Empty);
    }

    private Point SelectedPhotoCenter() => _selectedPhoto is null
        ? new Point()
        : new Point(_selectedPhoto.X + _selectedPhoto.Width / 2d, _selectedPhoto.Y + _selectedPhoto.Height / 2d);

    private static double AngleDegrees(Point center, Point point) =>
        Math.Atan2(point.Y - center.Y, point.X - center.X) * 180d / Math.PI;

    private static double PhotoDistance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double NormalizePhotoAngle(double angle)
    {
        angle %= 360d;
        if (angle < 0) angle += 360d;
        return angle;
    }

    private static double NormalizeAngleDelta(double delta)
    {
        while (delta > 180d) delta -= 360d;
        while (delta < -180d) delta += 360d;
        return delta;
    }

    private static double SnapPhotoAngle(double angle)
    {
        angle = NormalizePhotoAngle(angle);
        var snap = Math.Round(angle / 45d) * 45d;
        var distance = Math.Abs(NormalizeAngleDelta(angle - snap));
        return distance <= 6d ? NormalizePhotoAngle(snap) : angle;
    }

    private void StartPhotoRotate(object sender, PointerRoutedEventArgs e)
    {
        if (_selectedPhoto is null) return;
        _photoRotating = true;
        _photoPointerId = e.Pointer.PointerId;
        var current = e.GetCurrentPoint(this).Position;
        _photoRotationStartAngle = AngleDegrees(SelectedPhotoCenter(), current);
        _photoRotationStartValue = _selectedPhoto.Rotation;
        _photoRotateButton.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void MovePhotoRotate(object sender, PointerRoutedEventArgs e)
    {
        if (!_photoRotating || _selectedPhoto is null || e.Pointer.PointerId != _photoPointerId) return;
        var current = e.GetCurrentPoint(this).Position;
        var delta = AngleDegrees(SelectedPhotoCenter(), current) - _photoRotationStartAngle;
        _selectedPhoto.Rotation = SnapPhotoAngle(_photoRotationStartValue + delta);
        ConstrainPhotoToPage(_selectedPhoto);
        UpdatePhotoElement(_selectedPhoto);
        e.Handled = true;
    }

    private void EndPhotoRotate(object sender, PointerRoutedEventArgs e)
    {
        if (!_photoRotating || e.Pointer.PointerId != _photoPointerId) return;
        _photoRotating = false;
        _photoRotateButton.ReleasePointerCapture(e.Pointer);
        InkChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void StartPhotoScale(object sender, PointerRoutedEventArgs e)
    {
        if (_selectedPhoto is null) return;
        _photoScaling = true;
        _photoPointerId = e.Pointer.PointerId;
        var current = e.GetCurrentPoint(this).Position;
        _photoScaleStartWidth = _selectedPhoto.Width;
        _photoScaleStartHeight = _selectedPhoto.Height;
        _photoScaleAspect = Math.Max(.1, _photoScaleStartWidth / Math.Max(1, _photoScaleStartHeight));
        _photoScaleCenter = SelectedPhotoCenter();
        _photoScaleStartDistance = Math.Max(1, PhotoDistance(_photoScaleCenter, current));
        _photoScaleButton.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void MovePhotoScale(object sender, PointerRoutedEventArgs e)
    {
        if (!_photoScaling || _selectedPhoto is null || e.Pointer.PointerId != _photoPointerId) return;
        var current = e.GetCurrentPoint(this).Position;
        var ratio = Math.Clamp(PhotoDistance(_photoScaleCenter, current) / _photoScaleStartDistance, .15, 8d);

        // Keep the photo center stationary while scaling, then clamp the ratio so every edge
        // remains on the paper. This is both smoother and easier to control with a pen than
        // resizing from a moving top-left origin.
        var maxHalfWidth = Math.Min(_photoScaleCenter.X, PaperWidth - _photoScaleCenter.X);
        var maxHalfHeight = Math.Min(_photoScaleCenter.Y, PaperHeight - _photoScaleCenter.Y);
        var maxRatioX = (maxHalfWidth * 2d) / Math.Max(1, _photoScaleStartWidth);
        var maxRatioY = (maxHalfHeight * 2d) / Math.Max(1, _photoScaleStartHeight);
        var minRatio = Math.Max(80d / Math.Max(1, _photoScaleStartWidth), 60d / Math.Max(1, _photoScaleStartHeight));
        var maxRatio = Math.Max(.05, Math.Min(maxRatioX, maxRatioY));
        ratio = Math.Clamp(ratio, Math.Min(minRatio, maxRatio), maxRatio);

        var width = _photoScaleStartWidth * ratio;
        var height = _photoScaleStartHeight * ratio;
        _selectedPhoto.Width = width;
        _selectedPhoto.Height = height;
        _selectedPhoto.X = _photoScaleCenter.X - width / 2d;
        _selectedPhoto.Y = _photoScaleCenter.Y - height / 2d;
        ConstrainPhotoToPage(_selectedPhoto);
        UpdatePhotoElement(_selectedPhoto);
        e.Handled = true;
    }

    private void EndPhotoScale(object sender, PointerRoutedEventArgs e)
    {
        if (!_photoScaling || e.Pointer.PointerId != _photoPointerId) return;
        _photoScaling = false;
        _photoScaleButton.ReleasePointerCapture(e.Pointer);
        InkChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    public void SetPaperStyle(PaperStyle style)
    {
        Page.PaperStyle = style;
        DrawPaper();
        InkChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetPhysicalPaperMetrics(double renderedWidth, double renderedHeight, double physicalPpi, double rasterScale)
    {
        if (renderedWidth <= 0 || renderedHeight <= 0 || physicalPpi <= 0 || rasterScale <= 0) return;

        // Standard ruled paper is approximately 7 mm apart; grid/dot paper commonly uses
        // 5 mm. Convert those physical distances to DIPs for the current monitor, then into
        // this control's stable 1280x720 drawing coordinate space.
        double PaperUnitsForMillimetersX(double millimeters) =>
            millimeters * physicalPpi / 25.4 / rasterScale * PaperWidth / renderedWidth;
        double PaperUnitsForMillimetersY(double millimeters) =>
            millimeters * physicalPpi / 25.4 / rasterScale * PaperHeight / renderedHeight;

        var ruledX = Math.Clamp(PaperUnitsForMillimetersX(7), 12, 96);
        var ruledY = Math.Clamp(PaperUnitsForMillimetersY(7), 12, 96);
        var dottedX = Math.Clamp(PaperUnitsForMillimetersX(5), 10, 72);
        var dottedY = Math.Clamp(PaperUnitsForMillimetersY(5), 10, 72);
        var margin = Math.Clamp(PaperUnitsForMillimetersX(25), 36, 180);
        if (_patternDrawn && Math.Abs(_ruledSpacingX - ruledX) < .5 &&
            Math.Abs(_ruledSpacingY - ruledY) < .5 &&
            Math.Abs(_dotSpacingX - dottedX) < .5 &&
            Math.Abs(_dotSpacingY - dottedY) < .5 &&
            Math.Abs(_paperMargin - margin) < .5) return;
        _ruledSpacingX = ruledX;
        _ruledSpacingY = ruledY;
        _dotSpacingX = dottedX;
        _dotSpacingY = dottedY;
        _paperMargin = margin;
        DrawPaper();
    }

    private void DrawPaper()
    {
        _patternDrawn = true;
        _paperPattern.Children.Clear();
        var lineBrush = new SolidColorBrush(Color.FromArgb(38, 160, 160, 154));
        var patternGeometry = new PathGeometry();
        void AddPatternLine(double x1, double y1, double x2, double y2)
        {
            var figure = new PathFigure { StartPoint = new Point(x1, y1), IsClosed = false };
            figure.Segments.Add(new LineSegment { Point = new Point(x2, y2) });
            patternGeometry.Figures.Add(figure);
        }
        if (Page.PaperStyle == PaperStyle.Ruled || Page.PaperStyle == PaperStyle.Grid)
            for (double y = _ruledSpacingY; y < PaperHeight; y += _ruledSpacingY) AddPatternLine(0, y, PaperWidth, y);
        if (Page.PaperStyle == PaperStyle.Grid)
            for (double x = _ruledSpacingX; x < PaperWidth; x += _ruledSpacingX) AddPatternLine(x, 0, x, PaperHeight);
        if (Page.PaperStyle == PaperStyle.Dotted)
            for (double y = _dotSpacingY; y < PaperHeight; y += _dotSpacingY)
                for (double x = _dotSpacingX; x < PaperWidth; x += _dotSpacingX)
                    AddPatternLine(x, y, x + .01, y);
        if (patternGeometry.Figures.Count > 0)
            _paperPattern.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = patternGeometry,
                Stroke = lineBrush,
                StrokeThickness = Page.PaperStyle == PaperStyle.Dotted ? 1.6 : 1
            });
        // Keep ruled/grid paper visually neutral; no extra left margin guide.
    }

    public async Task RefreshInkAsync()
    {
        // Dense pages used to create thousands of XAML Shapes here. Render retained ink into
        // a handful of aggregate geometries instead, then yield once before loading photos.
        RenderAll();
        await Task.Yield();
        await RefreshPhotosAsync();
        _sessionUndoIds.Clear();
        _redo.Clear();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AddLine(double x1, double y1, double x2, double y2, Brush brush, double width) =>
        _paperPattern.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = width });

    private bool CanDraw(PointerRoutedEventArgs e) => PenInputEnabled && e.Pointer.PointerDeviceType == PointerDeviceType.Pen;

    private void OnAnyPointerActivity(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Pen)
        {
            PenActivity?.Invoke(this, EventArgs.Empty);
            // Keep the OS pointer hidden for the entire time the pen is hovering over the page,
            // not only during contact. Restoring it between pen-up and the next pen-down caused
            // the visible cursor flicker the user was seeing while handwriting.
            HidePointerCursor();
        }
        else if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
        {
            RestorePointerCursor();
        }
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Pen) RestorePointerCursor();
    }

    private void HidePointerCursor()
    {
        if (_cursorHiddenForPen) return;
        // ShowCursor is counter based. Drive the counter negative once for the duration of
        // pen contact, then restore it on release. Unlike SetCursor(NULL), Windows does not
        // immediately repaint the arrow on every pen move, so there is no visible flicker.
        while (ShowCursor(false) >= 0) { }
        _cursorHiddenForPen = true;
    }

    private void RestorePointerCursor()
    {
        if (!_cursorHiddenForPen) return;
        while (ShowCursor(true) < 0) { }
        _cursorHiddenForPen = false;
    }

    public void SetTool(InkTool tool)
    {
        Tool = tool;
        if (tool != InkTool.Lasso && _selectedIds.Count > 0)
        {
            _selectedIds.Clear();
            _selection.Visibility = Visibility.Collapsed;
            _selectionDeleteButton.Visibility = Visibility.Collapsed;
            _deleteButtonHasPosition = false;
            _movingSelection = false;
            RenderAll();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnInkPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_inkCanvas);
        if (e.OriginalSource is DependencyObject source &&
            (IsWithin(source, _photoDeleteButton) || IsWithin(source, _photoRotateButton) || IsWithin(source, _photoScaleButton) || IsWithin(source, _selectionDeleteButton)))
            return;
        if (TryStartPhotoInteraction(e, point.Position)) return;
        if (!CanDraw(e)) return;
        PenActivity?.Invoke(this, EventArgs.Empty);
        HidePointerCursor();
        if (Tool == InkTool.Lasso && _selectionDeleteButton.Visibility == Visibility.Visible)
        {
            var buttonLeft = Canvas.GetLeft(_selectionDeleteButton);
            var buttonTop = Canvas.GetTop(_selectionDeleteButton);
            if (!double.IsNaN(buttonLeft) && !double.IsNaN(buttonTop) &&
                new Rect(buttonLeft, buttonTop, _selectionDeleteButton.Width, _selectionDeleteButton.Height).Contains(point.Position))
                return;
        }
        _isPointerDown = true;
        _lastPoint = point.Position;
        _inkCanvas.CapturePointer(e.Pointer);
        WritingStateChanged?.Invoke(this, true);
        if (Tool == InkTool.Eraser) EraseAt(_lastPoint);
        else if (Tool == InkTool.Lasso)
        {
            var selectionRect = _selection.Visibility == Visibility.Visible
                ? new Rect(Canvas.GetLeft(_selection), Canvas.GetTop(_selection), _selection.Width, _selection.Height)
                : Rect.Empty;

            // Tapping/dragging inside the current selection moves it immediately.
            // Pressing outside cancels the current selection and starts a fresh drag-select.
            _movingSelection = _selectedIds.Count > 0 && selectionRect.Contains(_lastPoint);
            if (!_movingSelection)
            {
                _selectedIds.Clear();
                _selectionDeleteButton.Visibility = Visibility.Collapsed;
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                _selectionDragX = 0;
                _selectionDragY = 0;
                _selectionInkTransform.X = 0;
                _selectionInkTransform.Y = 0;
                _lassoOrigin = _lastPoint;
                _selection.Visibility = Visibility.Visible;
                Canvas.SetLeft(_selection, _lastPoint.X);
                Canvas.SetTop(_selection, _lastPoint.Y);
                _selection.Width = 0;
                _selection.Height = 0;
            }
        }
        else
        {
            _activeStroke = new InkStrokeData
            {
                Tool = Tool == InkTool.Highlighter ? "highlighter" : "pen",
                Color = Tool == InkTool.Highlighter ? HighlighterColor : "#EFEFEA",
                Width = Tool == InkTool.Highlighter ? 15 : 2.3
            };
            _activeStroke.Points.Add(ToData(point));
            if (_activeStroke.Tool == "highlighter")
            {
                var figure = new PathFigure { StartPoint = point.Position };
                _activeHighlightSegment = new PolyLineSegment();
                figure.Segments.Add(_activeHighlightSegment);
                _highlightGeometry.Figures.Add(figure);
                _highlightPath.Stroke = new SolidColorBrush(ParseColor(HighlighterColor, 220));
            }
            else
            {
                _activePenSegment = null;
                _activePenBucket = -1;
            }
            Page.Strokes.Add(_activeStroke);
            _sessionUndoIds.Push(_activeStroke.Id);
            _redo.Clear();
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }
        e.Handled = true;
    }

    private void OnInkPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_photoDragElement is not null && MovePhotoInteraction(e, e.GetCurrentPoint(_inkCanvas).Position)) return;
        if (!_isPointerDown || !CanDraw(e)) return;
        PenActivity?.Invoke(this, EventArgs.Empty);
        HidePointerCursor();
        var points = e.GetIntermediatePoints(_inkCanvas).Reverse();
        foreach (var point in points)
        {
            var p = point.Position;
            if (Distance(_lastPoint, p) < 0.9) continue;
            if (Tool == InkTool.Eraser) EraseAt(p);
            else if (Tool == InkTool.Lasso)
            {
                if (_movingSelection) MoveSelectionTo(p);
                else UpdateLasso(p);
            }
            else if (_activeStroke is not null)
            {
                var smoothed = new Point(_lastPoint.X * .28 + p.X * .72, _lastPoint.Y * .28 + p.Y * .72);
                var pressure = Math.Clamp(point.Properties.Pressure, 0.15f, 1f);
                _activeStroke.Points.Add(new InkPointData { X = (float)smoothed.X, Y = (float)smoothed.Y, Pressure = pressure });
                if (_activeStroke.Tool == "highlighter")
                    _activeHighlightSegment?.Points.Add(smoothed);
                else
                {
                    var bucket = Math.Clamp((int)Math.Round(pressure * 3), 0, 3);
                    if (_activePenSegment is null || bucket != _activePenBucket)
                    {
                        var figure = new PathFigure { StartPoint = _lastPoint };
                        _activePenSegment = new PolyLineSegment();
                        figure.Segments.Add(_activePenSegment);
                        _penGeometries[bucket].Figures.Add(figure);
                        _activePenBucket = bucket;
                    }
                    _activePenSegment.Points.Add(smoothed);
                }
                _lastPoint = smoothed;
            }
        }
        e.Handled = true;
    }

    private void OnInkPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (EndPhotoInteraction(e)) return;
        if (!_isPointerDown) return;
        _isPointerDown = false;
        // Do not restore the cursor on pen-up; the pen is usually still hovering and Windows
        // would flash its cursor between strokes. PointerExited or mouse activity restores it.
        _activeStroke = null;
        _activeHighlightSegment = null;
        _activePenSegment = null;
        _activePenBucket = -1;
        _inkCanvas.ReleasePointerCapture(e.Pointer);
        if (Tool == InkTool.Lasso)
        {
            if (_movingSelection)
            {
                CommitSelectionMove();
                _movingSelection = false;
                InkChanged?.Invoke(this, EventArgs.Empty);
                HistoryChanged?.Invoke(this, EventArgs.Empty);
            }
            else SelectInLasso();
        }
        else
        {
            // Pen and highlighter both append directly into retained geometries while drawing,
            // so pen-up performs no full-page rebuild even on extremely dense pages.
            InkChanged?.Invoke(this, EventArgs.Empty);
        }
        WritingStateChanged?.Invoke(this, false);
        e.Handled = true;
    }

    private void MoveSelectionTo(Point current)
    {
        var dx = current.X - _lastPoint.X;
        var dy = current.Y - _lastPoint.Y;
        if (Math.Abs(dx) < .01 && Math.Abs(dy) < .01) return;

        _selectionDragX += dx;
        _selectionDragY += dy;
        _selectionInkTransform.X = _selectionDragX;
        _selectionInkTransform.Y = _selectionDragY;
        Canvas.SetLeft(_selection, Canvas.GetLeft(_selection) + dx);
        Canvas.SetTop(_selection, Canvas.GetTop(_selection) + dy);
        _lastPoint = current;
        PositionSelectionDeleteButton();
    }

    private void CommitSelectionMove()
    {
        if (Math.Abs(_selectionDragX) < .01 && Math.Abs(_selectionDragY) < .01) return;
        foreach (var stroke in Page.Strokes.Where(s => _selectedIds.Contains(s.Id)))
            foreach (var point in stroke.Points)
            {
                point.X += (float)_selectionDragX;
                point.Y += (float)_selectionDragY;
            }
        _selectionDragX = 0;
        _selectionDragY = 0;
        _selectionInkTransform.X = 0;
        _selectionInkTransform.Y = 0;
        RenderAll();
        PositionSelectionDeleteButton();
    }

    private void UpdateLasso(Point current)
    {
        var left = Math.Min(_lassoOrigin.X, current.X); var top = Math.Min(_lassoOrigin.Y, current.Y);
        Canvas.SetLeft(_selection, left); Canvas.SetTop(_selection, top);
        _selection.Width = Math.Abs(current.X - _lassoOrigin.X); _selection.Height = Math.Abs(current.Y - _lassoOrigin.Y);
        _selectionDeleteButton.Visibility = Visibility.Collapsed;
    }

    private void SelectInLasso()
    {
        _selectedIds.Clear();
        var rect = new Rect(Canvas.GetLeft(_selection), Canvas.GetTop(_selection), _selection.Width, _selection.Height);
        foreach (var stroke in Page.Strokes)
            if (stroke.Points.Any(p => rect.Contains(new Point(p.X, p.Y)))) _selectedIds.Add(stroke.Id);

        if (_selectedIds.Count == 0)
        {
            _selection.Visibility = Visibility.Collapsed;
            _selectionDeleteButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            _selection.Visibility = Visibility.Visible;
            PositionSelectionDeleteButton();
        }
        RenderAll();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PositionSelectionDeleteButton()
    {
        if (_selectedIds.Count == 0 || _selection.Visibility != Visibility.Visible)
        {
            _selectionDeleteButton.Visibility = Visibility.Collapsed;
            return;
        }

        const double gap = 8;
        var left = Canvas.GetLeft(_selection);
        var top = Canvas.GetTop(_selection);
        var desiredLeft = left + _selection.Width + gap;
        var desiredTop = top - _selectionDeleteButton.Height - gap;

        if (desiredLeft + _selectionDeleteButton.Width > PaperWidth)
            desiredLeft = Math.Max(0, left + _selection.Width - _selectionDeleteButton.Width);
        if (desiredTop < 0)
            desiredTop = Math.Min(PaperHeight - _selectionDeleteButton.Height, top + gap);

        desiredLeft = Math.Clamp(desiredLeft, 0, PaperWidth - _selectionDeleteButton.Width);
        desiredTop = Math.Clamp(desiredTop, 0, PaperHeight - _selectionDeleteButton.Height);
        _deleteButtonTarget = new Point(desiredLeft, desiredTop);
        if (!_deleteButtonHasPosition)
        {
            Canvas.SetLeft(_selectionDeleteButton, desiredLeft);
            Canvas.SetTop(_selectionDeleteButton, desiredTop);
            _deleteButtonHasPosition = true;
        }
        _selectionDeleteButton.Visibility = Visibility.Visible;
    }

    private void OnSelectionUiRendering(object? sender, object e)
    {
        if (_selectionDeleteButton.Visibility == Visibility.Visible && _deleteButtonHasPosition)
        {
            var left = Canvas.GetLeft(_selectionDeleteButton);
            var top = Canvas.GetTop(_selectionDeleteButton);
            if (double.IsNaN(left)) left = _deleteButtonTarget.X;
            if (double.IsNaN(top)) top = _deleteButtonTarget.Y;
            var nextLeft = left + (_deleteButtonTarget.X - left) * 0.28;
            var nextTop = top + (_deleteButtonTarget.Y - top) * 0.28;
            if (Math.Abs(_deleteButtonTarget.X - nextLeft) < .15) nextLeft = _deleteButtonTarget.X;
            if (Math.Abs(_deleteButtonTarget.Y - nextTop) < .15) nextTop = _deleteButtonTarget.Y;
            Canvas.SetLeft(_selectionDeleteButton, nextLeft);
            Canvas.SetTop(_selectionDeleteButton, nextTop);
        }

        if (!_photoButtonsHavePosition || _selectedPhoto is null) return;
        // No temporary arrays/animation objects per frame: three tiny arithmetic updates only.
        LerpFloatingButton(_photoRotateButton, _photoButtonTargets[0]);
        LerpFloatingButton(_photoDeleteButton, _photoButtonTargets[1]);
        LerpFloatingButton(_photoScaleButton, _photoButtonTargets[2]);
    }

    private static void LerpFloatingButton(Button button, Point target)
    {
        if (button.Visibility != Visibility.Visible) return;
        var left = Canvas.GetLeft(button);
        var top = Canvas.GetTop(button);
        if (double.IsNaN(left)) left = target.X;
        if (double.IsNaN(top)) top = target.Y;

        var dx = target.X - left;
        var dy = target.Y - top;
        if (Math.Abs(dx) < .15 && Math.Abs(dy) < .15)
        {
            Canvas.SetLeft(button, target.X);
            Canvas.SetTop(button, target.Y);
            return;
        }
        Canvas.SetLeft(button, left + dx * 0.28);
        Canvas.SetTop(button, top + dy * 0.28);
    }

    public void DeleteSelection()
    {
        if (_selectedIds.Count == 0) return;
        Page.Strokes.RemoveAll(s => _selectedIds.Contains(s.Id));
        _selectedIds.Clear(); _selection.Visibility = Visibility.Collapsed;
        _selectionDeleteButton.Visibility = Visibility.Collapsed;
        _deleteButtonHasPosition = false;
        RenderAll(); InkChanged?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearAllInk()
    {
        Page.Strokes.Clear();
        _redo.Clear();
        _sessionUndoIds.Clear();
        _selectedIds.Clear();
        _selection.Visibility = Visibility.Collapsed;
        _selectionDeleteButton.Visibility = Visibility.Collapsed;
        _activeStroke = null;
        _isPointerDown = false;
        _movingSelection = false;
        RestorePointerCursor();
        RenderAll();
        InkChanged?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EraseAt(Point p)
    {
        var eraseHighlight = EraserTarget == InkTool.Highlighter;
        var removed = Page.Strokes.RemoveAll(stroke =>
            (eraseHighlight ? stroke.Tool == "highlighter" : stroke.Tool != "highlighter") &&
            stroke.Points.Any(q => Distance(p, new Point(q.X, q.Y)) < 14));
        if (removed > 0) { RenderAll(); InkChanged?.Invoke(this, EventArgs.Empty); HistoryChanged?.Invoke(this, EventArgs.Empty); }
    }

    public void Undo()
    {
        while (_sessionUndoIds.Count > 0)
        {
            var id = _sessionUndoIds.Pop();
            var index = Page.Strokes.FindLastIndex(stroke => stroke.Id == id);
            if (index < 0) continue;
            var stroke = Page.Strokes[index];
            Page.Strokes.RemoveAt(index);
            _redo.Push(stroke);
            RenderAll();
            InkChanged?.Invoke(this, EventArgs.Empty);
            HistoryChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var stroke = _redo.Pop();
        Page.Strokes.Add(stroke);
        _sessionUndoIds.Push(stroke.Id);
        RenderAll();
        InkChanged?.Invoke(this, EventArgs.Empty);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RenderAll()
    {
        _selectionInkCanvas.Children.Clear();

        RebuildNormalHighlights(Page.Strokes.Where(stroke => stroke.Tool == "highlighter" && !_selectedIds.Contains(stroke.Id)));
        RebuildNormalPenGeometry(Page.Strokes.Where(stroke => stroke.Tool != "highlighter" && !_selectedIds.Contains(stroke.Id)));

        if (_selectedIds.Count > 0)
        {
            RenderHighlights(Page.Strokes.Where(stroke => stroke.Tool == "highlighter" && _selectedIds.Contains(stroke.Id)), _selectionInkCanvas, selected: true);
            RenderPenStrokes(Page.Strokes.Where(stroke => stroke.Tool != "highlighter" && _selectedIds.Contains(stroke.Id)), _selectionInkCanvas, selected: true);
        }
    }

    private void RebuildNormalHighlights(IEnumerable<InkStrokeData> strokes)
    {
        _highlightGeometry.Figures.Clear();
        _highlightPath.Stroke = new SolidColorBrush(ParseColor(HighlighterColor, 220));
        foreach (var stroke in strokes.Where(stroke => stroke.Points.Count >= 2))
        {
            var figure = new PathFigure { StartPoint = new Point(stroke.Points[0].X, stroke.Points[0].Y) };
            var segment = new PolyLineSegment();
            for (var i = 1; i < stroke.Points.Count; i++)
                segment.Points.Add(new Point(stroke.Points[i].X, stroke.Points[i].Y));
            figure.Segments.Add(segment);
            _highlightGeometry.Figures.Add(figure);
        }
    }

    private void RebuildNormalPenGeometry(IEnumerable<InkStrokeData> strokes)
    {
        for (var bucket = 0; bucket < 4; bucket++) _penGeometries[bucket].Figures.Clear();
        foreach (var stroke in strokes.Where(stroke => stroke.Points.Count >= 2))
        {
            PolyLineSegment? segment = null;
            var previousBucket = -1;
            for (var i = 1; i < stroke.Points.Count; i++)
            {
                var bucket = Math.Clamp((int)Math.Round(stroke.Points[i].Pressure * 3), 0, 3);
                if (bucket != previousBucket)
                {
                    var preceding = stroke.Points[i - 1];
                    var figure = new PathFigure { StartPoint = new Point(preceding.X, preceding.Y) };
                    segment = new PolyLineSegment();
                    figure.Segments.Add(segment);
                    _penGeometries[bucket].Figures.Add(figure);
                    previousBucket = bucket;
                }
                segment!.Points.Add(new Point(stroke.Points[i].X, stroke.Points[i].Y));
            }
        }
    }

    private void RenderPenStrokes(IEnumerable<InkStrokeData> strokes, Canvas targetCanvas, bool selected)
    {
        var geometries = new PathGeometry[4];
        for (var bucket = 0; bucket < 4; bucket++) geometries[bucket] = new PathGeometry();
        foreach (var stroke in strokes.Where(stroke => stroke.Points.Count >= 2))
        {
            PolyLineSegment? segment = null;
            var previousBucket = -1;
            for (var i = 1; i < stroke.Points.Count; i++)
            {
                var bucket = Math.Clamp((int)Math.Round(stroke.Points[i].Pressure * 3), 0, 3);
                if (bucket != previousBucket)
                {
                    var preceding = stroke.Points[i - 1];
                    var figure = new PathFigure { StartPoint = new Point(preceding.X, preceding.Y) };
                    segment = new PolyLineSegment();
                    figure.Segments.Add(segment);
                    geometries[bucket].Figures.Add(figure);
                    previousBucket = bucket;
                }
                segment!.Points.Add(new Point(stroke.Points[i].X, stroke.Points[i].Y));
            }
        }
        var color = selected ? SelectedInkColor() : Color.FromArgb(255, 239, 239, 234);
        for (var bucket = 0; bucket < 4; bucket++)
        {
            if (geometries[bucket].Figures.Count == 0) continue;
            targetCanvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = geometries[bucket],
                Stroke = new SolidColorBrush(color),
                StrokeThickness = 2.3 * (0.55 + (bucket + .5) / 4d * .8),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            });
        }
    }

    private void RenderHighlights(IEnumerable<InkStrokeData> strokes, Canvas target, bool selected)
    {
        var geometry = new PathGeometry();
        foreach (var stroke in strokes.Where(stroke => stroke.Points.Count >= 2))
        {
            var figure = new PathFigure { StartPoint = new Point(stroke.Points[0].X, stroke.Points[0].Y) };
            var segment = new PolyLineSegment();
            for (var i = 1; i < stroke.Points.Count; i++)
                segment.Points.Add(new Point(stroke.Points[i].X, stroke.Points[i].Y));
            figure.Segments.Add(segment);
            geometry.Figures.Add(figure);
        }
        if (geometry.Figures.Count == 0) return;
        var color = selected ? ParseColor(_accentColor, 205) : ParseColor(HighlighterColor, 220);
        target.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geometry,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 15,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        });
    }

    private void AddRetainedStroke(InkStrokeData stroke, Canvas targetCanvas, bool selected)
    {
        if (stroke.Points.Count < 2 || stroke.Tool == "highlighter") return;
        var geometries = new PathGeometry[4];
        for (var bucket = 0; bucket < geometries.Length; bucket++) geometries[bucket] = new PathGeometry();
        PolyLineSegment? segment = null;
        var previousBucket = -1;
        for (var i = 1; i < stroke.Points.Count; i++)
        {
            var bucket = Math.Clamp((int)Math.Round(stroke.Points[i].Pressure * 3), 0, 3);
            if (bucket != previousBucket)
            {
                var preceding = stroke.Points[i - 1];
                var figure = new PathFigure { StartPoint = new Point(preceding.X, preceding.Y) };
                segment = new PolyLineSegment();
                figure.Segments.Add(segment);
                geometries[bucket].Figures.Add(figure);
                previousBucket = bucket;
            }
            segment!.Points.Add(new Point(stroke.Points[i].X, stroke.Points[i].Y));
        }
        var color = selected ? SelectedInkColor() : ParseColor(stroke.Color, 255);
        for (var bucket = 0; bucket < geometries.Length; bucket++)
        {
            if (geometries[bucket].Figures.Count == 0) continue;
            targetCanvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = geometries[bucket],
                Stroke = new SolidColorBrush(color),
                StrokeThickness = stroke.Width * (0.55 + (bucket + .5) / 4d * .8),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            });
        }
    }

    private void DrawSegment(InkStrokeData stroke, Point a, Point b, float pressure)
    {
        var color = ParseColor(stroke.Color, 255);
        var selected = _selectedIds.Contains(stroke.Id);
        _inkCanvas.Children.Add(new Line
        {
            X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y,
            Stroke = new SolidColorBrush(selected ? SelectedInkColor() : color),
            StrokeThickness = stroke.Width * (0.55 + pressure * 0.8),
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
        });
    }

    private Color SelectedInkColor()
    {
        // Selected handwriting should look unmistakably selected, not like a slightly tinted
        // version of the normal off-white ink. Use the hue opposite the notebook accent, then
        // force strong saturation/lightness so it stays vivid on the dark page and clearly
        // separates from the notebook-colored highlighter underneath it.
        var accent = ParseColor(_accentColor, 255);
        RgbToHsl(accent.R, accent.G, accent.B, out var hue, out _, out _);
        hue = (hue + 180d) % 360d;
        var selected = HslToRgb(hue, 0.95, 0.68);
        return Color.FromArgb(255, selected.R, selected.G, selected.B);
    }

    private static void RgbToHsl(byte red, byte green, byte blue, out double hue, out double saturation, out double lightness)
    {
        var r = red / 255d;
        var g = green / 255d;
        var b = blue / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        lightness = (max + min) / 2d;

        if (delta < 0.0001)
        {
            hue = 0;
            saturation = 0;
            return;
        }

        saturation = delta / (1d - Math.Abs(2d * lightness - 1d));
        if (max == r) hue = 60d * (((g - b) / delta) % 6d);
        else if (max == g) hue = 60d * (((b - r) / delta) + 2d);
        else hue = 60d * (((r - g) / delta) + 4d);
        if (hue < 0) hue += 360d;
    }

    private static Color HslToRgb(double hue, double saturation, double lightness)
    {
        var c = (1d - Math.Abs(2d * lightness - 1d)) * saturation;
        var x = c * (1d - Math.Abs((hue / 60d) % 2d - 1d));
        var m = lightness - c / 2d;
        double r1, g1, b1;
        if (hue < 60) (r1, g1, b1) = (c, x, 0);
        else if (hue < 120) (r1, g1, b1) = (x, c, 0);
        else if (hue < 180) (r1, g1, b1) = (0, c, x);
        else if (hue < 240) (r1, g1, b1) = (0, x, c);
        else if (hue < 300) (r1, g1, b1) = (x, 0, c);
        else (r1, g1, b1) = (c, 0, x);

        byte ToByte(double channel) => (byte)Math.Clamp(Math.Round((channel + m) * 255d), 0, 255);
        return Color.FromArgb(255, ToByte(r1), ToByte(g1), ToByte(b1));
    }

    private static InkPointData ToData(PointerPoint p) => new() { X = (float)p.Position.X, Y = (float)p.Position.Y, Pressure = Math.Clamp(p.Properties.Pressure, .15f, 1f) };
    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
    private static Color ParseColor(string value, byte alpha)
    {
        var hex = value.TrimStart('#');
        return Color.FromArgb(alpha, Convert.ToByte(hex[..2], 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16));
    }
}
