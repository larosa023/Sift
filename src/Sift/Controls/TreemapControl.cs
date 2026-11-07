using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Sift.Core;

namespace Sift.Controls;

/// <summary>
/// Squarified, nested treemap. The layout is drawn once into a DrawingVisual and only redrawn when
/// the root, size or theme changes; hover/selection highlights live on a separate overlay visual.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    private const int MaxDepth = 14;
    private const double HeaderHeight = 17;

    public static readonly DependencyProperty RootProperty = DependencyProperty.Register(
        nameof(Root), typeof(FileNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, (d, _) => ((TreemapControl)d).Relayout()));

    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(
        nameof(Selected), typeof(FileNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((TreemapControl)d).DrawOverlay()));

    public static readonly DependencyProperty HoveredProperty = DependencyProperty.Register(
        nameof(Hovered), typeof(FileNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty LayoutVersionProperty = DependencyProperty.Register(
        nameof(LayoutVersion), typeof(int), typeof(TreemapControl),
        new FrameworkPropertyMetadata(0, (d, _) => ((TreemapControl)d).Relayout()));

    public FileNode? Root { get => (FileNode?)GetValue(RootProperty); set => SetValue(RootProperty, value); }
    public FileNode? Selected { get => (FileNode?)GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }
    public FileNode? Hovered { get => (FileNode?)GetValue(HoveredProperty); set => SetValue(HoveredProperty, value); }
    /// <summary>Bump to force a redraw after the tree changed (e.g. items deleted).</summary>
    public int LayoutVersion { get => (int)GetValue(LayoutVersionProperty); set => SetValue(LayoutVersionProperty, value); }

    /// <summary>Raised on double-click with the folder to drill into.</summary>
    public event Action<FileNode>? DrillRequested;

    private readonly DrawingVisual _main = new();
    private readonly DrawingVisual _overlay = new();
    private readonly List<Cell> _cells = new(4096);
    private readonly DispatcherTimer _resizeTimer;
    private readonly Typeface _typeface = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private readonly Typeface _typefaceBold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private FileNode? _hover;
    private Rect _hoverRect, _selectedRect;

    private readonly record struct Cell(Rect Rect, FileNode Node);

    public TreemapControl()
    {
        AddVisualChild(_main);
        AddVisualChild(_overlay);
        ClipToBounds = true;
        Focusable = true;
        _resizeTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(70) };
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); Relayout(); };
        SizeChanged += (_, _) => { _resizeTimer.Stop(); _resizeTimer.Start(); };
        Loaded += (_, _) => { Themes.ThemeManager.ThemeChanged += OnThemeChanged; Relayout(); };
        Unloaded += (_, _) => Themes.ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged() => Relayout();

    protected override int VisualChildrenCount => 2;
    protected override Visual GetVisualChild(int index) => index == 0 ? _main : _overlay;

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent fill so the whole surface receives mouse input.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
    }

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    // ------------------------------------------------------------------ layout & drawing

    public void Relayout()
    {
        _cells.Clear();
        var w = ActualWidth;
        var h = ActualHeight;
        using (var dc = _main.RenderOpen())
        {
            if (w < 2 || h < 2) { DrawOverlay(); return; }
            var bounds = new Rect(0, 0, w, h);
            dc.DrawRectangle(Res("TreemapBgBrush", Brushes.Black), null, bounds);
            var root = Root;
            if (root is not null && root.Size > 0)
            {
                var ctx = new DrawCtx(dc,
                    Res("TreemapFolderBrush", Brushes.DimGray),
                    new Pen(Res("TreemapFolderLineBrush", Brushes.Black), 1),
                    Res("TreemapLabelBrush", Brushes.White),
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);
                ctx.FolderPen.Freeze();
                DrawNode(ctx, root, bounds, 0);
            }
        }
        DrawOverlay();
    }

    private sealed record DrawCtx(DrawingContext Dc, Brush FolderBrush, Pen FolderPen, Brush LabelBrush, double Dpi);

    private static readonly Pen CellPen = CreateCellPen();
    private static Pen CreateCellPen()
    {
        var p = new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), 0.6);
        p.Freeze();
        return p;
    }
    private static readonly Brush FileLabel = Frozen(new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush FileLabelDim = Frozen(new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)));
    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    private void DrawNode(DrawCtx ctx, FileNode node, Rect r, int depth)
    {
        if (r.Width < 1 || r.Height < 1) return;
        _cells.Add(new Cell(r, node));
        var dc = ctx.Dc;

        if (!node.IsDirectory)
        {
            dc.DrawRectangle(CategoryPalette.CushionBrush(node.Category), r.Width > 3 && r.Height > 3 ? CellPen : null, r);
            if (r.Width > 70 && r.Height > 34)
            {
                DrawText(dc, node.Name, r.X + 6, r.Y + 5, r.Width - 12, 11.5, FileLabel, _typefaceBold, ctx.Dpi);
                DrawText(dc, Format.Bytes(node.Size), r.X + 6, r.Y + 20, r.Width - 12, 10.5, FileLabelDim, _typeface, ctx.Dpi);
            }
            return;
        }

        var kids = node.Children;
        bool canNest = kids is { Count: > 0 } && r.Width >= 6 && r.Height >= 6 && depth < MaxDepth;
        if (!canNest)
        {
            dc.DrawRectangle(ctx.FolderBrush, ctx.FolderPen, r);
            return;
        }

        double header = 0;
        if (depth > 0)
        {
            dc.DrawRectangle(ctx.FolderBrush, ctx.FolderPen, r);
            if (r.Height > 44 && r.Width > 60)
            {
                header = HeaderHeight;
                DrawText(dc, $"{node.Name}  ·  {Format.Bytes(node.Size)}", r.X + 5, r.Y + 2, r.Width - 10, 10.5, ctx.LabelBrush, _typeface, ctx.Dpi);
            }
        }

        double pad = depth == 0 ? 0 : 1.5;
        var inner = new Rect(r.X + pad, r.Y + header + pad,
            Math.Max(0, r.Width - pad * 2), Math.Max(0, r.Height - header - pad * 2));
        if (inner.Width < 2 || inner.Height < 2) return;

        Squarify(kids!, node.Size, inner, (child, cr) => DrawNode(ctx, child, cr, depth + 1));
    }

    private static void DrawText(DrawingContext dc, string text, double x, double y, double maxWidth, double size, Brush brush, Typeface tf, double dpi)
    {
        if (maxWidth < 12) return;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, size, brush, dpi)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.DrawText(ft, new Point(x, y));
    }

    /// <summary>Squarified treemap (Bruls, Huizing, van Wijk). Children must be sorted by size, descending.</summary>
    private static void Squarify(List<FileNode> items, long total, Rect rect, Action<FileNode, Rect> emit)
    {
        long sum = 0;
        int n = 0;
        foreach (var c in items) { if (c.Size <= 0) break; sum += c.Size; n++; }
        if (n == 0 || sum <= 0) return;
        total = sum;

        double scale = rect.Width * rect.Height / total;
        var r = rect;
        int i = 0;
        while (i < n && r.Width > 0.5 && r.Height > 0.5)
        {
            double side = Math.Min(r.Width, r.Height);
            double rowSum = 0, worst = double.MaxValue;
            int j = i;
            double first = items[i].Size * scale;
            while (j < n)
            {
                double a = items[j].Size * scale;
                double s = rowSum + a;
                double w = Worst(first, a, s, side);
                if (j > i && w > worst) break;
                worst = w;
                rowSum = s;
                j++;
            }

            if (r.Width >= r.Height)
            {
                double colW = Math.Min(r.Width, rowSum / r.Height);
                double y = r.Y;
                for (int k = i; k < j; k++)
                {
                    double hgt = items[k].Size * scale / colW;
                    emit(items[k], new Rect(r.X, y, colW, hgt));
                    y += hgt;
                }
                r = new Rect(r.X + colW, r.Y, Math.Max(0, r.Width - colW), r.Height);
            }
            else
            {
                double rowH = Math.Min(r.Height, rowSum / r.Width);
                double x = r.X;
                for (int k = i; k < j; k++)
                {
                    double wid = items[k].Size * scale / rowH;
                    emit(items[k], new Rect(x, r.Y, wid, rowH));
                    x += wid;
                }
                r = new Rect(r.X, r.Y + rowH, r.Width, Math.Max(0, r.Height - rowH));
            }
            i = j;
        }
    }

    private static double Worst(double max, double min, double sum, double side)
    {
        double s2 = side * side, sum2 = sum * sum;
        return Math.Max(s2 * max / sum2, sum2 / (s2 * min));
    }

    // ------------------------------------------------------------------ overlay

    private void DrawOverlay()
    {
        _selectedRect = Rect.Empty;
        _hoverRect = Rect.Empty;
        var sel = Selected;
        foreach (var c in _cells)
        {
            if (sel is not null && ReferenceEquals(c.Node, sel)) _selectedRect = c.Rect;
            if (_hover is not null && ReferenceEquals(c.Node, _hover)) _hoverRect = c.Rect;
        }

        using var dc = _overlay.RenderOpen();
        if (!_hoverRect.IsEmpty)
        {
            var hp = new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), 1.5);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), hp, Deflate(_hoverRect, 0.75));
        }
        if (!_selectedRect.IsEmpty)
        {
            var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue;
            dc.DrawRectangle(null, new Pen(Brushes.Black, 4) { DashCap = PenLineCap.Flat }, Deflate(_selectedRect, 2));
            dc.DrawRectangle(null, new Pen(accent, 2.5), Deflate(_selectedRect, 2));
        }
    }

    private static Rect Deflate(Rect r, double d) =>
        r.Width > d * 2 && r.Height > d * 2 ? new Rect(r.X + d, r.Y + d, r.Width - d * 2, r.Height - d * 2) : r;

    // ------------------------------------------------------------------ input

    private Cell? HitTestCell(Point p)
    {
        // Cells are stored in pre-order; the last cell containing p is the deepest.
        for (int i = _cells.Count - 1; i >= 0; i--)
            if (_cells[i].Rect.Contains(p)) return _cells[i];
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTestCell(e.GetPosition(this));
        var node = hit?.Node;
        if (!ReferenceEquals(node, _hover))
        {
            _hover = node;
            Hovered = node;
            DrawOverlay();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        Hovered = null;
        DrawOverlay();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var hit = HitTestCell(e.GetPosition(this));
        if (hit is null) return;
        if (e.ClickCount == 2)
        {
            var target = TopLevelFolderUnder(hit.Value.Node);
            if (target is not null) DrillRequested?.Invoke(target);
            e.Handled = true;
            return;
        }
        Selected = hit.Value.Node;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        var hit = HitTestCell(e.GetPosition(this));
        if (hit is not null) Selected = hit.Value.Node;
    }

    /// <summary>For a double-click, drill into the child of the current root that contains the node.</summary>
    private FileNode? TopLevelFolderUnder(FileNode node)
    {
        var root = Root;
        if (root is null) return null;
        var n = node;
        while (n.Parent is not null && !ReferenceEquals(n.Parent, root)) n = n.Parent;
        if (!ReferenceEquals(n.Parent, root)) return null;
        return n.IsDirectory ? n : null;
    }
}
