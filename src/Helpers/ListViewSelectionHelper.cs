using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls.Primitives;

namespace SVNFileBox.Helpers
{
    public static class ListViewSelectionHelper
    {
        #region 附加属性：启用框选
        public static readonly DependencyProperty EnableDragSelectionProperty =
            DependencyProperty.RegisterAttached(
                "EnableDragSelection",
                typeof(bool),
                typeof(ListViewSelectionHelper),
                new PropertyMetadata(false, OnEnableDragSelectionChanged));

        public static bool GetEnableDragSelection(DependencyObject obj)
        {
            return (bool)obj.GetValue(EnableDragSelectionProperty);
        }

        public static void SetEnableDragSelection(DependencyObject obj, bool value)
        {
            obj.SetValue(EnableDragSelectionProperty, value);
        }

        private static void OnEnableDragSelectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ListView listView) return;

            if ((bool)e.NewValue)
            {
                listView.PreviewMouseLeftButtonDown += ListView_PreviewMouseLeftButtonDown;
                listView.PreviewMouseMove += ListView_PreviewMouseMove;
                listView.MouseLeftButtonUp += ListView_MouseLeftButtonUp;
                listView.MouseLeave += ListView_MouseLeftButtonUp;
                listView.SizeChanged += ListView_SizeChanged;
            }
            else
            {
                listView.PreviewMouseLeftButtonDown -= ListView_PreviewMouseLeftButtonDown;
                listView.PreviewMouseMove -= ListView_PreviewMouseMove;
                listView.MouseLeftButtonUp -= ListView_MouseLeftButtonUp;
                listView.MouseLeave -= ListView_MouseLeftButtonUp;
                listView.SizeChanged -= ListView_SizeChanged;
            }
        }

        private static void ListView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is ListView listView)
                ListView_MouseLeftButtonUp(listView, null);
        }
        #endregion

        #region 私有状态
        private static readonly DependencyProperty StartItemProperty =
            DependencyProperty.RegisterAttached("StartItem", typeof(object), typeof(ListViewSelectionHelper));
        private static readonly DependencyProperty IsDragSelectingProperty =
            DependencyProperty.RegisterAttached("IsDragSelecting", typeof(bool), typeof(ListViewSelectionHelper));
        private static readonly DependencyProperty IsSelectingProperty =
            DependencyProperty.RegisterAttached("IsSelecting", typeof(bool), typeof(ListViewSelectionHelper));
        private static readonly DependencyProperty SelectionAdornerProperty =
            DependencyProperty.RegisterAttached("SelectionAdorner", typeof(SelectionAdorner), typeof(ListViewSelectionHelper));
        private static readonly DependencyProperty DragHighlightProperty =
            DependencyProperty.RegisterAttached("DragHighlight", typeof(ListViewItem), typeof(ListViewSelectionHelper));
        #endregion

        #region 鼠标事件逻辑
        private static void ListView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListView listView) return;
            if (IsMouseOverHeaderOrThumb(e)) return;

            var item = listView.ContainerFromElement(e.OriginalSource as DependencyObject) as ListViewItem;

            // 全局监听鼠标抬起（无论在哪松开都能清理）
            var window = Window.GetWindow(listView);
            if (window != null)
                window.AddHandler(Window.MouseLeftButtonUpEvent, new MouseButtonEventHandler(GlobalMouseUp), true);

            if (item != null)
            {
                // 点击在列表项上：记录起始项，启用拖动多选模式
                // 但如果按住 Shift/Ctrl，让默认行为处理
                if (Keyboard.Modifiers == ModifierKeys.None)
                {
                    listView.SetValue(StartItemProperty, item.DataContext);
                    listView.SetValue(IsDragSelectingProperty, true);
                    listView.SetValue(IsSelectingProperty, false); // 拖动多选不用框
                }
            }
            else
            {
                // 点击在空白区域：启动框选
                ListView_MouseLeftButtonUp(listView, null);

                Point start = e.GetPosition(listView);
                listView.SetValue(StartPointProperty, start);
                listView.SetValue(IsSelectingProperty, true);
                listView.SetValue(IsDragSelectingProperty, false);
                listView.SelectedItems.Clear();

                var adorner = new SelectionAdorner(listView, start);
                AdornerLayer.GetAdornerLayer(listView)?.Add(adorner);
                listView.SetValue(SelectionAdornerProperty, adorner);
            }
        }

        private static void GlobalMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.Source is FrameworkElement fe && fe.DataContext is ListView lv)
            {
                ListView_MouseLeftButtonUp(lv, null);
            }
            else
            {
                foreach (var window in Application.Current.Windows)
                {
                    if (window is Window w)
                        w.RemoveHandler(Window.MouseLeftButtonUpEvent, new MouseButtonEventHandler(GlobalMouseUp));
                }
            }
        }

        private static bool IsMouseOverHeaderOrThumb(MouseButtonEventArgs e)
        {
            DependencyObject obj = e.OriginalSource as DependencyObject;
            while (obj != null)
            {
                if (obj is GridViewColumnHeader || obj is Thumb)
                    return true;
                obj = VisualTreeHelper.GetParent(obj);
            }
            return false;
        }

        private static void ListView_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (sender is not ListView listView) return;

            // 拖动多选模式（从列表项按下开始）
            if ((bool)listView.GetValue(IsDragSelectingProperty))
            {
                if (!listView.IsMouseCaptured)
                    listView.CaptureMouse();

                // 找到鼠标当前所在的项
                var mousePos = e.GetPosition(listView);
                var hoveredItem = listView.ContainerFromElement(e.OriginalSource as DependencyObject) as ListViewItem;

                // 清除上一次的高亮
                var lastHighlight = listView.GetValue(DragHighlightProperty) as ListViewItem;
                if (lastHighlight != null && lastHighlight != hoveredItem)
                {
                    lastHighlight.IsSelected = false;
                    listView.SetValue(DragHighlightProperty, null);
                }

                // 如果当前有项在鼠标下，且不是起始项，就选中它
                if (hoveredItem != null)
                {
                    var startItem = listView.GetValue(StartItemProperty);
                    if (hoveredItem.DataContext != startItem)
                    {
                        hoveredItem.IsSelected = true;
                        listView.SetValue(DragHighlightProperty, hoveredItem);

                        // 滚动支持：如果鼠标超出可见区域，自动滚动
                        EnsureItemVisible(listView, hoveredItem);
                    }
                }
                return;
            }

            // 框选模式
            if (!(bool)listView.GetValue(IsSelectingProperty)) return;
            if (listView.GetValue(SelectionAdornerProperty) is not SelectionAdorner adorner) return;

            Point current = e.GetPosition(listView);
            adorner.UpdateEndPoint(current);

            Rect selectRect = new Rect((Point)listView.GetValue(StartPointProperty), current);
            listView.SelectedItems.Clear();

            foreach (var dataItem in listView.Items)
            {
                if (listView.ItemContainerGenerator.ContainerFromItem(dataItem) is not ListViewItem container)
                    continue;

                Point itemTopLeft = container.TranslatePoint(new Point(0, 0), listView);
                Rect itemBounds = new Rect(itemTopLeft, container.RenderSize);

                if (selectRect.IntersectsWith(itemBounds))
                    listView.SelectedItems.Add(dataItem);
            }
        }

        private static void ListView_MouseLeftButtonUp(object sender, MouseEventArgs e)
        {
            if (sender is not ListView listView) return;

            try
            {
                var window = Window.GetWindow(listView);
                if (window != null)
                    window.RemoveHandler(Window.MouseLeftButtonUpEvent, new MouseButtonEventHandler(GlobalMouseUp));

                if (listView.IsMouseCaptured)
                    listView.ReleaseMouseCapture();

                if (listView.GetValue(SelectionAdornerProperty) is SelectionAdorner adorner)
                {
                    var layer = AdornerLayer.GetAdornerLayer(listView);
                    layer?.Remove(adorner);
                    listView.SetValue(SelectionAdornerProperty, null);
                }
            }
            catch { }

            listView.SetValue(IsSelectingProperty, false);
            listView.SetValue(IsDragSelectingProperty, false);
            listView.SetValue(StartItemProperty, null);
            listView.SetValue(DragHighlightProperty, null);
        }
        #endregion

        #region 自动滚动
        private static void EnsureItemVisible(ListView listView, ListViewItem item)
        {
            var scrollHost = FindScrollViewer(listView);
            if (scrollHost == null) return;

            var itemTop = item.TranslatePoint(new Point(0, 0), scrollHost).Y;
            var itemBottom = itemTop + item.ActualHeight;
            var viewTop = 0.0;
            var viewBottom = scrollHost.ViewportHeight;

            if (itemBottom > viewBottom)
                scrollHost.ScrollToVerticalOffset(scrollHost.VerticalOffset + (itemBottom - viewBottom));
            else if (itemTop < viewTop)
                scrollHost.ScrollToVerticalOffset(scrollHost.VerticalOffset + itemTop);
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject obj)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
            {
                var child = VisualTreeHelper.GetChild(obj, i);
                if (child is ScrollViewer sv)
                    return sv;
                var result = FindScrollViewer(child);
                if (result != null) return result;
            }
            return null;
        }
        #endregion

        #region 私有状态（框选用）
        private static readonly DependencyProperty StartPointProperty =
            DependencyProperty.RegisterAttached("StartPoint", typeof(Point), typeof(ListViewSelectionHelper));
        #endregion

        #region 自定义选区绘制层
        private class SelectionAdorner : Adorner
        {
            private Point _startPoint, _endPoint;
            public SelectionAdorner(UIElement adornedElement, Point startPoint) : base(adornedElement)
            {
                _startPoint = startPoint;
                _endPoint = startPoint;
                IsHitTestVisible = false;
            }
            public void UpdateEndPoint(Point endPoint)
            {
                _endPoint = endPoint;
                InvalidateVisual();
            }
            protected override void OnRender(DrawingContext drawingContext)
            {
                base.OnRender(drawingContext);
                Rect rect = new Rect(_startPoint, _endPoint);
                Brush fill = new SolidColorBrush(Color.FromArgb(80, 0, 120, 215));
                Pen borderPen = new Pen(new SolidColorBrush(Color.FromRgb(0, 120, 215)), 1);
                drawingContext.DrawRectangle(fill, borderPen, rect);
            }
        }
        #endregion
    }
}