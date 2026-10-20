using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LocalFileAudit.Core.Models;

namespace LocalFileAudit.App.Controls;

public sealed class TreemapNode
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public double Percentage { get; set; }
    public Rect Bounds { get; set; }
    public Color Color { get; set; }
}

/// <summary>
/// High-performance squarified storage treemap control for WPF.
/// Implements Bruls, Huizing, and van Wijk (2000) squarified layout.
/// </summary>
public sealed class TreemapControl : Canvas
{
    public static readonly DependencyProperty DirectoriesProperty =
        DependencyProperty.Register(
            nameof(Directories),
            typeof(IEnumerable<DirectorySummary>),
            typeof(TreemapControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));

    public static readonly DependencyProperty TotalSizeBytesProperty =
        DependencyProperty.Register(
            nameof(TotalSizeBytes),
            typeof(long),
            typeof(TreemapControl),
            new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender, OnDataChanged));

    public IEnumerable<DirectorySummary>? Directories
    {
        get => (IEnumerable<DirectorySummary>?)GetValue(DirectoriesProperty);
        set => SetValue(DirectoriesProperty, value);
    }

    public long TotalSizeBytes
    {
        get => (long)GetValue(TotalSizeBytesProperty);
        set => SetValue(TotalSizeBytesProperty, value);
    }

    private static readonly Color[] Palette = new[]
    {
        Color.FromRgb(14, 116, 144), // Teal 700
        Color.FromRgb(3, 105, 161),  // Sky 700
        Color.FromRgb(29, 78, 216),  // Blue 700
        Color.FromRgb(67, 56, 202),  // Indigo 700
        Color.FromRgb(109, 40, 217), // Violet 700
        Color.FromRgb(126, 34, 206), // Purple 700
        Color.FromRgb(15, 118, 110), // Cyan 700
        Color.FromRgb(4, 120, 87),   // Emerald 700
        Color.FromRgb(180, 83, 9),   // Amber 700
        Color.FromRgb(185, 28, 28),  // Red 700
        Color.FromRgb(30, 41, 59),   // Slate 800
        Color.FromRgb(55, 65, 81)    // Gray 700
    };

    public TreemapControl()
    {
        ClipToBounds = true;
        Background = new SolidColorBrush(Color.FromRgb(24, 26, 32));
        SizeChanged += (s, e) => RebuildTreemap();
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TreemapControl control)
        {
            control.RebuildTreemap();
        }
    }

    public void RebuildTreemap()
    {
        Children.Clear();

        if (ActualWidth <= 10 || ActualHeight <= 10) return;
        if (Directories == null) return;

        var items = Directories
            .Where(d => d.DirectSizeBytes > 0 || d.RecursiveSizeBytes > 0)
            .OrderByDescending(d => Math.Max(d.RecursiveSizeBytes, d.DirectSizeBytes))
            .Take(60)
            .ToList();

        if (items.Count == 0) return;

        var total = TotalSizeBytes > 0 ? TotalSizeBytes : items.Sum(i => Math.Max(i.RecursiveSizeBytes, i.DirectSizeBytes));
        if (total <= 0) return;

        var nodes = new List<TreemapNode>();
        var colorIdx = 0;

        foreach (var item in items)
        {
            var size = Math.Max(item.RecursiveSizeBytes, item.DirectSizeBytes);
            var pct = (double)size / total * 100.0;
            var name = Path.GetFileName(item.DirectoryPath.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(name)) name = item.DirectoryPath;

            nodes.Add(new TreemapNode
            {
                Path = item.DirectoryPath,
                Name = name,
                SizeBytes = size,
                Percentage = pct,
                Color = Palette[colorIdx % Palette.Length]
            });
            colorIdx++;
        }

        // Run squarified layout
        Squarify(nodes, new Rect(0, 0, ActualWidth, ActualHeight));

        foreach (var node in nodes)
        {
            if (node.Bounds.Width < 2 || node.Bounds.Height < 2) continue;

            var border = new Border
            {
                Width = Math.Max(0, node.Bounds.Width - 2),
                Height = Math.Max(0, node.Bounds.Height - 2),
                Background = new SolidColorBrush(node.Color),
                BorderBrush = new SolidColorBrush(Color.FromArgb(180, 18, 19, 22)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Cursor = Cursors.Hand,
                ClipToBounds = true
            };

            // Tooltip
            var tooltipPanel = new StackPanel { Margin = new Thickness(6) };
            tooltipPanel.Children.Add(new TextBlock
            {
                Text = node.Path,
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 4)
            });
            tooltipPanel.Children.Add(new TextBlock
            {
                Text = $"Size: {FormatBytes(node.SizeBytes)} ({node.Percentage:F1}% of scanned storage)",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(200, 210, 220))
            });
            tooltipPanel.Children.Add(new TextBlock
            {
                Text = "Left-click: Reveal in Explorer • Right-click: Copy Path",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 160, 175)),
                Margin = new Thickness(0, 4, 0, 0)
            });
            border.ToolTip = new ToolTip { Content = tooltipPanel };

            // Context Menu
            var contextMenu = new ContextMenu();
            var revealItem = new MenuItem { Header = "🔍 Reveal in Explorer" };
            revealItem.Click += (s, e) => RevealInExplorer(node.Path);
            var copyItem = new MenuItem { Header = "📋 Copy Directory Path" };
            copyItem.Click += (s, e) => Clipboard.SetText(node.Path);
            contextMenu.Items.Add(revealItem);
            contextMenu.Items.Add(copyItem);
            border.ContextMenu = contextMenu;

            // Click Handler
            border.MouseLeftButtonUp += (s, e) => RevealInExplorer(node.Path);

            // Inner content if rectangle is large enough
            if (node.Bounds.Width >= 45 && node.Bounds.Height >= 28)
            {
                var textPanel = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(3)
                };

                textPanel.Children.Add(new TextBlock
                {
                    Text = node.Name,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = Math.Clamp(node.Bounds.Width / 12, 10, 13),
                    Foreground = Brushes.White,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Center
                });

                if (node.Bounds.Height >= 42)
                {
                    textPanel.Children.Add(new TextBlock
                    {
                        Text = $"{FormatBytes(node.SizeBytes)}",
                        FontSize = Math.Clamp(node.Bounds.Width / 14, 9, 11),
                        Foreground = new SolidColorBrush(Color.FromArgb(220, 240, 245, 255)),
                        HorizontalAlignment = HorizontalAlignment.Center
                    });
                }

                border.Child = textPanel;
            }

            SetLeft(border, node.Bounds.X + 1);
            SetTop(border, node.Bounds.Y + 1);
            Children.Add(border);
        }
    }

    private static void Squarify(List<TreemapNode> items, Rect rect)
    {
        if (items.Count == 0 || rect.Width <= 0 || rect.Height <= 0) return;

        var totalWeight = items.Sum(i => i.SizeBytes);
        if (totalWeight <= 0) return;

        SquarifyRecursive(items, new List<TreemapNode>(), rect, totalWeight);
    }

    private static void SquarifyRecursive(List<TreemapNode> children, List<TreemapNode> row, Rect rect, double totalWeight)
    {
        if (children.Count == 0)
        {
            if (row.Count > 0)
            {
                LayoutRow(row, rect, totalWeight);
            }
            return;
        }

        var shortestSide = Math.Min(rect.Width, rect.Height);
        var head = children[0];

        if (row.Count == 0)
        {
            row.Add(head);
            SquarifyRecursive(children.Skip(1).ToList(), row, rect, totalWeight);
            return;
        }

        var currentWorst = WorstAspect(row, shortestSide, rect.Width * rect.Height, totalWeight);
        var testRow = new List<TreemapNode>(row) { head };
        var nextWorst = WorstAspect(testRow, shortestSide, rect.Width * rect.Height, totalWeight);

        if (nextWorst <= currentWorst)
        {
            row.Add(head);
            SquarifyRecursive(children.Skip(1).ToList(), row, rect, totalWeight);
        }
        else
        {
            var remainingRect = LayoutRow(row, rect, totalWeight);
            var remainingWeight = totalWeight - row.Sum(i => i.SizeBytes);
            SquarifyRecursive(children, new List<TreemapNode>(), remainingRect, remainingWeight);
        }
    }

    private static double WorstAspect(List<TreemapNode> row, double sideLength, double totalArea, double totalWeight)
    {
        if (row.Count == 0 || sideLength <= 0 || totalWeight <= 0) return double.MaxValue;

        var rowWeight = row.Sum(i => i.SizeBytes);
        var rowArea = (rowWeight / totalWeight) * totalArea;
        var rowLength = rowArea / sideLength;

        if (rowLength <= 0) return double.MaxValue;

        double maxAspect = 0;
        foreach (var item in row)
        {
            var itemArea = (item.SizeBytes / (double)rowWeight) * rowArea;
            var itemSide = itemArea / rowLength;
            var aspect = Math.Max(rowLength / itemSide, itemSide / rowLength);
            if (aspect > maxAspect) maxAspect = aspect;
        }

        return maxAspect;
    }

    private static Rect LayoutRow(List<TreemapNode> row, Rect rect, double totalWeight)
    {
        if (row.Count == 0 || totalWeight <= 0) return rect;

        var isHorizontal = rect.Width >= rect.Height;
        var rowWeight = row.Sum(i => i.SizeBytes);
        var totalArea = rect.Width * rect.Height;
        var rowArea = (rowWeight / totalWeight) * totalArea;

        if (isHorizontal)
        {
            var rowWidth = rowArea / rect.Height;
            var yOffset = rect.Y;
            foreach (var item in row)
            {
                var itemHeight = (item.SizeBytes / (double)rowWeight) * rect.Height;
                item.Bounds = new Rect(rect.X, yOffset, Math.Max(0, rowWidth), Math.Max(0, itemHeight));
                yOffset += itemHeight;
            }
            return new Rect(rect.X + rowWidth, rect.Y, Math.Max(0, rect.Width - rowWidth), rect.Height);
        }
        else
        {
            var rowHeight = rowArea / rect.Width;
            var xOffset = rect.X;
            foreach (var item in row)
            {
                var itemWidth = (item.SizeBytes / (double)rowWeight) * rect.Width;
                item.Bounds = new Rect(xOffset, rect.Y, Math.Max(0, itemWidth), Math.Max(0, rowHeight));
                xOffset += itemWidth;
            }
            return new Rect(rect.X, rect.Y + rowHeight, rect.Width, Math.Max(0, rect.Height - rowHeight));
        }
    }

    private static void RevealInExplorer(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            else if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // Best effort
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L)
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB";
        if (bytes >= 1024L * 1024L)
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        if (bytes >= 1024L)
            return $"{bytes / 1024.0:F0} KB";
        return $"{bytes} B";
    }
}
