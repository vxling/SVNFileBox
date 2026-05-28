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
        #region 附加属性
        public static readonly DependencyProperty EnableDragSelectionProperty =
            DependencyProperty.RegisterAttached(
                "EnableDragSelection",
                typeof(bool),
                typeof(ListViewSelectionHelper),
                new FrameworkPropertyMetadata(false, OnEnableDragSelectionChanged));

        public static bool GetEnableDragSelection(DependencyObject obj) => (bool)obj.GetValue(EnableDragSelectionProperty);
        public static void SetEnableDragSelection(DependencyObject obj, bool value) => obj.SetValue(EnableDragSelectionProperty, value);
        #endregion

        #region 状态
        private static readonly DependencyProperty StartPointProperty = DependencyProperty.RegisterAttached("StartPoint", typeof(Point), typeof(ListViewSelectionHelper));
        private static readonly DependencyProperty StartItemProperty = DependencyProperty.RegisterAttached("StartItem", typeof(object), typeof(ListViewSelectionHelper));
        private static readonly DependencyProperty IsSelectingProperty = DependencyProperty.RegisterAttached("IsSelecting", typeof(bool), typeof(ListViewSelectionHelper));
        private static readonly DependencyProperty SelectionAdornerProperty = DependencyProperty.RegisterAttached("SelectionAdorner", typeof(SelectionAdorner), typeof(ListViewSelectionHelper));
        #endregion

        private static void OnEnableDragSelectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ListView lv) return;
            bool enable = (bool)e.NewValue;

            if (enable)
            {
                lv.PreviewMouseLeftButtonDown += ListView_PreviewMouseLeftButtonDown;
                lv.PreviewMouseMove += ListView_PreviewMouseMove;
                lv.MouseLeftButtonUp += ListView_MouseLeftButtonUp;
                lv.MouseLeave += ListView_MouseLeftButtonUp;
                lv.SizeChanged += ListView_SizeChanged;
                lv.LostMouseCapture += ListView_LostMouseCapture;
                lv.PreviewMouseRightButtonDown += ListView_PreviewMouseRightButtonDown; // 右键绝杀
            }
            else
            {
                lv.PreviewMouseLeftButtonDown -= ListView_PreviewMouseLeftButtonDown;
                lv.PreviewMouseMove -= ListView_PreviewMouseMove;
                lv.MouseLeftButtonUp -= ListView_MouseLeftButtonUp;
                lv.MouseLeave -= ListView_MouseLeftButtonUp;
                lv.SizeChanged -= ListView_SizeChanged;
                lv.LostMouseCapture -= ListView_LostMouseCapture;
                lv.PreviewMouseRightButtonDown -= ListView_PreviewMouseRightButtonDown;
                CleanupState(lv);
            }
        }

        #region 事件
        private static void ListView_SizeChanged(object sender, SizeChangedEventArgs e) => CleanupState(sender as ListView);
        private static void ListView_LostMouseCapture(object sender, MouseEventArgs e) => CleanupState(sender as ListView);
        private static void ListView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e) => CleanupState(sender as ListView); // 右键立即清理

        private static void ListView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListView lv) return;
            if (IsMouseOverHeaderOrThumb(e)) return;

            CleanupState(lv); // 每次按下都清空残留
            lv.Focus();

            var pos = e.GetPosition(lv);
            var item = GetListViewItemAtPoint(lv, pos);

            if (item == null)
            {
                lv.SetValue(StartPointProperty, pos);
                lv.SetValue(IsSelectingProperty, true);
                lv.SelectedItems.Clear();
                lv.CaptureMouse();

                var adorner = new SelectionAdorner(lv, pos);
                AdornerLayer.GetAdornerLayer(lv)?.Add(adorner);
                lv.SetValue(SelectionAdornerProperty, adorner);
            }
            else
            {
                if (Keyboard.Modifiers == ModifierKeys.None)
                {
                    lv.SetValue(StartItemProperty, item.DataContext);
                    lv.CaptureMouse();
                }
            }
        }

        private static void ListView_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (sender is not ListView lv) return;
            if (!lv.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;

            bool isSelecting = (bool)lv.GetValue(IsSelectingProperty);
            object startItem = lv.GetValue(StartItemProperty);

            if (isSelecting)
            {
                var adorner = lv.GetValue(SelectionAdornerProperty) as SelectionAdorner;
                var current = e.GetPosition(lv);
                adorner?.UpdateEndPoint(current);

                var rect = new Rect((Point)lv.GetValue(StartPointProperty), current);
                lv.SelectedItems.Clear();

                foreach (var data in lv.Items)
                {
                    if (lv.ItemContainerGenerator.ContainerFromItem(data) is ListViewItem item)
                    {
                        var tl = item.TranslatePoint(new(0, 0), lv);
                        if (rect.IntersectsWith(new Rect(tl, item.RenderSize)))
                            lv.SelectedItems.Add(data);
                    }
                }
            }
            else if (startItem != null)
            {
                var hover = GetListViewItemAtPoint(lv, e.GetPosition(lv));
                if (hover == null) return;

                int start = lv.Items.IndexOf(startItem);
                int end = lv.Items.IndexOf(hover.DataContext);
                if (start < 0 || end < 0) return;

                lv.SelectedItems.Clear();
                int min = Math.Min(start, end);
                int max = Math.Max(start, end);
                for (int i = min; i <= max; i++)
                    lv.SelectedItems.Add(lv.Items[i]);

                AutoScroll(lv, hover);
            }
        }

        private static void ListView_MouseLeftButtonUp(object sender, MouseEventArgs e) => CleanupState(sender as ListView);
        #endregion

        #region 工具
        private static bool IsMouseOverHeaderOrThumb(MouseEventArgs e)
        {
            DependencyObject obj = e.OriginalSource as DependencyObject;
            while (obj != null)
            {
                if (obj is GridViewColumnHeader or Thumb) return true;
                obj = VisualTreeHelper.GetParent(obj);
            }
            return false;
        }

        private static ListViewItem GetListViewItemAtPoint(ListView lv, Point p)
        {
            DependencyObject elem = lv.InputHitTest(p) as DependencyObject;
            while (elem != null)
            {
                if (elem is ListViewItem item) return item;
                elem = VisualTreeHelper.GetParent(elem);
            }
            return null;
        }

        private static void AutoScroll(ListView lv, ListViewItem item)
        {
            lv.ScrollIntoView(item);
        }

        private static ScrollViewer FindScrollViewer(DependencyObject d)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                if (c is ScrollViewer sv) return sv;
                var r = FindScrollViewer(c);
                if (r != null) return r;
            }
            return null;
        }

        private static void CleanupState(ListView lv)
        {
            if (lv == null) return;
            if (lv.IsMouseCaptured) lv.ReleaseMouseCapture();

            if (lv.GetValue(SelectionAdornerProperty) is SelectionAdorner ad)
            {
                AdornerLayer.GetAdornerLayer(lv)?.Remove(ad);
                lv.SetValue(SelectionAdornerProperty, null);
            }

            lv.SetValue(IsSelectingProperty, false);
            lv.SetValue(StartPointProperty, new Point());
            lv.SetValue(StartItemProperty, null);
        }
        #endregion

        #region 框选层
        private class SelectionAdorner : Adorner
        {
            private Point _s, _e;
            public SelectionAdorner(UIElement elem, Point s) : base(elem) => _s = s;
            public void UpdateEndPoint(Point e) { _e = e; InvalidateVisual(); }
            protected override void OnRender(DrawingContext dc)
            {
                dc.DrawRectangle(
                    new SolidColorBrush(Color.FromArgb(50, 0, 122, 204)),
                    new Pen(new SolidColorBrush(Color.FromRgb(0, 122, 204)), 1),
                    new Rect(_s, _e));
            }
        }
        #endregion
    }
}

