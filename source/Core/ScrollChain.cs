using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace AISuitePanel;

/// <summary>
/// One wheel notch, aimed at the right box.
///
/// <para>Long pages here are a ScrollViewer that contains several more
/// ScrollViewers (the live log, the mail list, the account list). A nested
/// ScrollViewer marks the wheel handled even when it has nowhere left to scroll,
/// so the notch died there and the page behind it never moved - scrolling only
/// worked while the pointer was over a gap. This walks outwards from the box
/// under the pointer and scrolls the first one that can actually move.</para>
/// </summary>
internal static class ScrollChain
{
    /// <summary>
    /// Scrolls <paramref name="start"/> (or its nearest scrollable ancestor) by
    /// one notch. False when nothing in the chain could move at all, which
    /// leaves the event unhandled for whoever else may want it.
    /// </summary>
    internal static bool Handle(ScrollViewer? start, int delta)
    {
        for (var node = start; node is not null; node = Parent(node))
        {
            if (TryScroll(node, delta)) return true;
        }
        return false;
    }

    /// <summary>
    /// Scrolls one box. False when it is already at the end in that direction, so
    /// the caller can pass the wheel to whatever contains it.
    /// </summary>
    internal static bool TryScroll(ScrollViewer sv, int delta)
    {
        // "Can this box move?" is answered from the offset it is at and the one
        // it can reach - NOT by scrolling and reading the offset back. WPF applies
        // a new offset during the next layout pass, so a read taken immediately
        // after ScrollToVerticalOffset still returns the OLD value; that made a
        // box report "did not move" and the chain scrolled its parent as well,
        // moving two boxes for one notch.
        var max = Math.Max(0, sv.ScrollableHeight);
        var at = sv.VerticalOffset;

        if (sv.CanContentScroll)
        {
            // Items-based scrolling: one item per notch.
            if (delta > 0)
            {
                if (at <= 0.001) return false;
                sv.LineUp();
            }
            else
            {
                if (at >= max - 0.001) return false;
                sv.LineDown();
            }
            return true;
        }

        var step = Math.Clamp(sv.ViewportHeight * 0.10, 32, 110);
        if (delta > 0)
        {
            if (at <= 0.001) return false;
            sv.ScrollToVerticalOffset(Math.Max(0, at - step));
        }
        else
        {
            if (at >= max - 0.001) return false;
            sv.ScrollToVerticalOffset(Math.Min(max, at + step));
        }
        return true;
    }

    /// <summary>The next ScrollViewer above <paramref name="child"/>, if any.</summary>
    internal static ScrollViewer? Parent(DependencyObject? child) =>
        child is null ? null : Find(ParentOf(child));

    internal static ScrollViewer? Find(DependencyObject? node)
    {
        while (node is not null && node is not ScrollViewer)
        {
            node = ParentOf(node);
        }
        return node as ScrollViewer;
    }

    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is Visual or Visual3D
            ? VisualTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);
}
