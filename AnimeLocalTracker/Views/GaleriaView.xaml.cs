using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AnimeLocalTracker.Core.ViewModels;

namespace AnimeLocalTracker.Views;

public partial class GaleriaView : UserControl
{
    private ScrollViewer? _scrollViewer;

    public GaleriaView()
    {
        InitializeComponent();
        Loaded += GaleriaView_Loaded;
        Unloaded += GaleriaView_Unloaded;
    }

    private void GaleriaView_Loaded(object sender, RoutedEventArgs e)
    {
        _scrollViewer = FindVisualChild<ScrollViewer>(ListaGaleria);
        if (_scrollViewer != null)
        {
            _scrollViewer.ScrollChanged += ScrollViewer_ScrollChanged;
            if (DataContext is GaleriaViewModel vm && vm.UltimoScrollOffset > 0)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    _scrollViewer?.ScrollToVerticalOffset(vm.UltimoScrollOffset);
                }));
            }
        }
    }

    private void GaleriaView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_scrollViewer != null)
        {
            _scrollViewer.ScrollChanged -= ScrollViewer_ScrollChanged;
        }
    }

    private void ScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is GaleriaViewModel vm && _scrollViewer != null)
        {
            vm.UltimoScrollOffset = _scrollViewer.VerticalOffset;
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) return null;
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
                return typedChild;

            var descendant = FindVisualChild<T>(child);
            if (descendant != null)
                return descendant;
        }
        return null;
    }
}
