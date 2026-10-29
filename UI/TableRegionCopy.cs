/*
 * 功能说明：让速卖通半托页的表格用鼠标框选并复制。
 * 主要职责：支持单元格、多格、整行、整列和矩形区域；Ctrl+C 复制为制表符文本。
 * 创建日期：2026-10-24
 * 修改记录：2026-10-24 详情 SKU 表、各国运费表、半托管查重列表均可复制
 */

using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace EcommerceWorkbench.UI;

/// <summary>表格区域选择与复制。</summary>
public static class TableRegionCopy
{
    public static void EnableDataGrid(DataGrid grid)
    {
        grid.SelectionUnit = DataGridSelectionUnit.Cell;
        grid.SelectionMode = DataGridSelectionMode.Extended;
        grid.ClipboardCopyMode = DataGridClipboardCopyMode.ExcludeHeader;
        grid.CanUserSortColumns = false;
        grid.HeadersVisibility = DataGridHeadersVisibility.All;
        grid.CellStyle = CreateCellStyle(grid.CellStyle);
        grid.RowStyle = CreateRowStyle(grid.RowStyle);

        var drag = new GridDrag(grid);
        grid.PreviewMouseLeftButtonDown += drag.OnDown;
        grid.PreviewMouseMove += drag.OnMove;
        grid.PreviewMouseLeftButtonUp += drag.OnUp;
        grid.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBox)
            {
                CopyDataGrid(grid);
                e.Handled = true;
            }
        };
        grid.ContextMenu = BuildMenu(() => CopyDataGrid(grid));
    }

    public static void EnableSemiList(FrameworkElement host)
    {
        var drag = new ListDrag(host);
        host.PreviewMouseLeftButtonDown += drag.OnDown;
        host.PreviewMouseMove += drag.OnMove;
        host.PreviewMouseLeftButtonUp += drag.OnUp;
        host.Focusable = true;
        host.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBox)
            {
                drag.Copy();
                e.Handled = true;
            }
        };
        host.ContextMenu = BuildMenu(drag.Copy);
    }

    private static bool IsEditingText(object? source)
    {
        for (var node = source as DependencyObject; node != null; node = VisualParent(node))
        {
            if (node is TextBox box)
                return box.SelectionLength > 0 || box.IsKeyboardFocusWithin;
            if (node is ComboBox or CheckBox or Button or Hyperlink)
                return true;
        }

        return false;
    }

    private static ContextMenu BuildMenu(Action copy)
    {
        var menu = new ContextMenu();
        var item = new MenuItem { Header = "复制" };
        item.Click += (_, _) => copy();
        menu.Items.Add(item);
        return menu;
    }

    private static Style CreateCellStyle(Style? basedOn)
    {
        var style = new Style(typeof(DataGridCell), basedOn);
        var selected = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        selected.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB))));
        selected.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1.5)));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
        style.Triggers.Add(selected);
        return style;
    }

    private static Style CreateRowStyle(Style? basedOn)
    {
        var style = new Style(typeof(DataGridRow), basedOn);
        var selected = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        selected.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.Black));
        style.Triggers.Add(selected);
        return style;
    }

    private static void CopyDataGrid(DataGrid grid)
    {
        if (grid.SelectedCells.Count == 0)
            return;

        var rows = new List<(int Row, int Col, string Text)>();
        foreach (var cell in grid.SelectedCells)
        {
            var row = grid.Items.IndexOf(cell.Item);
            var col = cell.Column.DisplayIndex;
            if (row < 0 || col < 0)
                continue;
            rows.Add((row, col, ReadBoundCell(cell.Item, cell.Column)));
        }

        if (rows.Count == 0)
            return;

        SetClipboard(rows);
    }

    private static string ReadBoundCell(object? item, DataGridColumn column)
    {
        if (item != null && column is DataGridBoundColumn bound && bound.Binding is Binding binding)
        {
            var probe = new FrameworkElement { DataContext = item };
            BindingOperations.SetBinding(probe, FrameworkElement.TagProperty, new Binding
            {
                Path = binding.Path,
                StringFormat = binding.StringFormat,
                Converter = binding.Converter,
                ConverterParameter = binding.ConverterParameter,
                ConverterCulture = binding.ConverterCulture
            });
            var value = probe.GetValue(FrameworkElement.TagProperty);
            BindingOperations.ClearBinding(probe, FrameworkElement.TagProperty);
            if (value == null || ReferenceEquals(value, DependencyProperty.UnsetValue))
                return "";
            if (value is IFormattable formattable && !string.IsNullOrWhiteSpace(binding.StringFormat)
                && !binding.StringFormat.Contains('{', StringComparison.Ordinal))
            {
                return formattable.ToString(binding.StringFormat, CultureInfo.CurrentCulture);
            }

            return value.ToString() ?? "";
        }

        var visual = item == null ? null : column.GetCellContent(item);
        return visual == null ? "" : ReadVisualText(visual);
    }

    private static void SetClipboard(List<(int Row, int Col, string Text)> cells)
    {
        var minRow = cells.Min(c => c.Row);
        var maxRow = cells.Max(c => c.Row);
        var minCol = cells.Min(c => c.Col);
        var maxCol = cells.Max(c => c.Col);
        var map = cells.ToDictionary(c => (c.Row, c.Col), c => c.Text);
        var builder = new StringBuilder();
        for (var row = minRow; row <= maxRow; row++)
        {
            for (var col = minCol; col <= maxCol; col++)
            {
                if (col > minCol)
                    builder.Append('\t');
                if (map.TryGetValue((row, col), out var text))
                    builder.Append(text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '));
            }

            if (row < maxRow)
                builder.Append("\r\n");
        }

        Clipboard.SetText(builder.ToString());
    }

    private static string ReadVisualText(DependencyObject root)
    {
        var parts = new List<string>();
        CollectText(root, parts);
        return string.Join(" ", parts.Where(part => part.Length > 0));
    }

    private static void CollectText(DependencyObject node, List<string> parts)
    {
        switch (node)
        {
            case TextBox box:
                parts.Add(box.Text.Trim());
                return;
            case CheckBox check:
                parts.Add((check.Content?.ToString() ?? "选项") + (check.IsChecked == true ? ":是" : ":否"));
                return;
            case ComboBox combo:
                parts.Add(combo.Text.Trim());
                return;
            case TextBlock block:
                if (!string.IsNullOrWhiteSpace(block.Text))
                    parts.Add(block.Text.Trim());
                break;
        }

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
            CollectText(VisualTreeHelper.GetChild(node, i), parts);
    }

    private static DependencyObject? VisualParent(DependencyObject node)
    {
        if (node is Visual or System.Windows.Media.Media3D.Visual3D)
            return VisualTreeHelper.GetParent(node);
        return LogicalTreeHelper.GetParent(node);
    }

    private static T? FindAncestor<T>(object? source) where T : DependencyObject
    {
        for (var node = source as DependencyObject; node != null; node = VisualParent(node))
        {
            if (node is T match)
                return match;
        }

        return null;
    }

    private static bool IsInteractive(object? source)
    {
        for (var node = source as DependencyObject; node != null; node = VisualParent(node))
        {
            if (node is TextBox or ComboBox or CheckBox or Button or Hyperlink)
                return true;
        }

        return false;
    }

    private sealed class GridDrag
    {
        private readonly DataGrid _grid;
        private Point _down;
        private DataGridCell? _startCell;
        private int _startIndex = -1;
        private bool _rowMode;
        private bool _columnMode;

        public GridDrag(DataGrid grid) => _grid = grid;

        public void OnDown(object sender, MouseButtonEventArgs e)
        {
            if (IsInteractive(e.OriginalSource))
                return;

            if (FindAncestor<DataGridColumnHeader>(e.OriginalSource) is { } columnHeader)
            {
                _columnMode = true;
                _rowMode = false;
                _startCell = null;
                _startIndex = columnHeader.DisplayIndex;
                _down = e.GetPosition(_grid);
                SelectColumns(_startIndex, _startIndex);
                _grid.CaptureMouse();
                e.Handled = true;
                return;
            }

            if (FindAncestor<DataGridRowHeader>(e.OriginalSource) is { } rowHeader
                && FindAncestor<DataGridRow>(rowHeader) is { } row)
            {
                var index = _grid.ItemContainerGenerator.IndexFromContainer(row);
                if (index < 0)
                    return;
                _rowMode = true;
                _columnMode = false;
                _startCell = null;
                _startIndex = index;
                _down = e.GetPosition(_grid);
                SelectRows(index, index);
                _grid.CaptureMouse();
                e.Handled = true;
                return;
            }

            if (FindAncestor<DataGridCell>(e.OriginalSource) is not { } cell)
                return;

            _rowMode = false;
            _columnMode = false;
            _startCell = cell;
            _startIndex = -1;
            _down = e.GetPosition(_grid);
            SelectCellRect(cell, cell);
        }

        public void OnMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
                return;
            if (_startCell == null && !_rowMode && !_columnMode)
                return;
            if ((e.GetPosition(_grid) - _down).Length < 4)
                return;

            var hit = VisualTreeHelper.HitTest(_grid, e.GetPosition(_grid))?.VisualHit;
            if (_columnMode)
            {
                if (FindAncestor<DataGridColumnHeader>(hit) is { } header)
                    SelectColumns(_startIndex, header.DisplayIndex);
                return;
            }

            if (_rowMode)
            {
                if (FindAncestor<DataGridRow>(hit) is { } row)
                {
                    var index = _grid.ItemContainerGenerator.IndexFromContainer(row);
                    if (index >= 0)
                        SelectRows(_startIndex, index);
                }

                return;
            }

            if (FindAncestor<DataGridCell>(hit) is { } cell && _startCell != null)
                SelectCellRect(_startCell, cell);
        }

        public void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (_grid.IsMouseCaptured)
                _grid.ReleaseMouseCapture();
            _startCell = null;
            _rowMode = false;
            _columnMode = false;
        }

        private void SelectCellRect(DataGridCell a, DataGridCell b)
        {
            var rowA = _grid.Items.IndexOf(a.DataContext);
            var rowB = _grid.Items.IndexOf(b.DataContext);
            if (rowA < 0 || rowB < 0)
                return;
            SelectRectangle(Math.Min(rowA, rowB), Math.Max(rowA, rowB), Math.Min(a.Column.DisplayIndex, b.Column.DisplayIndex), Math.Max(a.Column.DisplayIndex, b.Column.DisplayIndex));
        }

        private void SelectRows(int from, int to)
        {
            if (_grid.Columns.Count == 0)
                return;
            var maxCol = _grid.Columns.Max(column => column.DisplayIndex);
            SelectRectangle(Math.Min(from, to), Math.Max(from, to), 0, maxCol);
        }

        private void SelectColumns(int from, int to)
        {
            if (_grid.Items.Count == 0)
                return;
            SelectRectangle(0, _grid.Items.Count - 1, Math.Min(from, to), Math.Max(from, to));
        }

        private void SelectRectangle(int row1, int row2, int col1, int col2)
        {
            var columns = _grid.Columns.OrderBy(column => column.DisplayIndex).ToList();
            _grid.SelectedCells.Clear();
            for (var row = row1; row <= row2; row++)
            {
                for (var col = col1; col <= col2; col++)
                {
                    var column = columns.FirstOrDefault(item => item.DisplayIndex == col);
                    if (column == null)
                        continue;
                    _grid.SelectedCells.Add(new DataGridCellInfo(_grid.Items[row], column));
                }
            }
        }
    }

    private sealed class ListDrag
    {
        private readonly FrameworkElement _host;
        private readonly List<Border> _selected = [];
        private Point _down;
        private int _anchorRow = -1;
        private int _anchorCol = -1;
        private bool _header;
        private bool _armed;

        public ListDrag(FrameworkElement host) => _host = host;

        public void OnDown(object sender, MouseButtonEventArgs e)
        {
            if (IsInteractive(e.OriginalSource))
                return;
            if (!TryHitCell(e.OriginalSource, out var row, out var col))
                return;

            _anchorRow = row;
            _anchorCol = col;
            _header = row < 0;
            _down = e.GetPosition(_host);
            _armed = true;
            _host.Focus();
            if (row < 0)
                ApplySelection(0, LastRow(), col, col);
            else
                ApplySelection(row, row, col, col);
            e.Handled = true;
        }

        public void OnMove(object sender, MouseEventArgs e)
        {
            if (!_armed || e.LeftButton != MouseButtonState.Pressed)
                return;
            if ((e.GetPosition(_host) - _down).Length < 4)
                return;
            var hit = VisualTreeHelper.HitTest(_host, e.GetPosition(_host))?.VisualHit;
            if (!TryHitCell(hit, out var row, out var col))
                return;
            if (_header)
                row = LastRow();
            else if (row < 0)
                row = _anchorRow < 0 ? 0 : _anchorRow;
            var startRow = _header ? 0 : _anchorRow;
            ApplySelection(startRow, row, _anchorCol, col);
        }

        public void OnUp(object sender, MouseButtonEventArgs e) => _armed = false;

        public void Copy()
        {
            var cells = new List<(int Row, int Col, string Text)>();
            foreach (var border in _selected)
            {
                var row = RowOf(border);
                var col = Grid.GetColumn(border);
                if (row < 0 || col < 0)
                    continue;
                cells.Add((row, col, ReadVisualText(border)));
            }

            if (cells.Count == 0)
                return;
            SetClipboard(cells);
        }

        private void ApplySelection(int rowA, int rowB, int colA, int colB)
        {
            ClearSelection();
            var row1 = Math.Min(rowA, rowB);
            var row2 = Math.Max(rowA, rowB);
            var col1 = Math.Min(colA, colB);
            var col2 = Math.Max(colA, colB);
            if (row1 < 0)
                row1 = 0;
            foreach (var border in EnumerateCells())
            {
                var row = RowOf(border);
                var col = Grid.GetColumn(border);
                if (row < row1 || row > row2 || col < col1 || col > col2)
                    continue;
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
                border.BorderThickness = new Thickness(1.5);
                _selected.Add(border);
            }
        }

        private void ClearSelection()
        {
            foreach (var border in _selected)
            {
                border.ClearValue(Border.BorderBrushProperty);
                border.ClearValue(Border.BorderThicknessProperty);
            }

            _selected.Clear();
        }

        private IEnumerable<Border> EnumerateCells()
        {
            var list = FindDescendant<ItemsControl>(_host);
            if (list == null)
                yield break;

            list.UpdateLayout();
            for (var i = 0; i < list.Items.Count; i++)
            {
                if (list.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter presenter)
                    continue;
                var grid = FindDescendant<Grid>(presenter);
                if (grid == null)
                    continue;
                foreach (var child in grid.Children)
                {
                    if (child is Border border && Grid.GetColumn(border) >= 0)
                        yield return border;
                }
            }
        }

        private int LastRow()
        {
            var list = FindDescendant<ItemsControl>(_host);
            return list == null ? 0 : Math.Max(0, list.Items.Count - 1);
        }

        private int RowOf(Border border)
        {
            var presenter = FindAncestor<ContentPresenter>(border);
            var list = FindDescendant<ItemsControl>(_host);
            if (presenter == null || list == null)
                return -1;
            return list.ItemContainerGenerator.IndexFromContainer(presenter);
        }

        private bool TryHitCell(object? source, out int row, out int col)
        {
            row = -1;
            col = -1;
            var border = FindAncestor<Border>(source);
            while (border != null)
            {
                if (border.Parent is Grid grid && grid.ColumnDefinitions.Count >= 6)
                {
                    col = Grid.GetColumn(border);
                    if (col < 0)
                        return false;
                    var presenter = FindAncestor<ContentPresenter>(border);
                    var list = FindDescendant<ItemsControl>(_host);
                    row = presenter != null && list != null
                        ? list.ItemContainerGenerator.IndexFromContainer(presenter)
                        : -1;
                    return row >= 0 || presenter == null;
                }

                border = FindAncestor<Border>(VisualParent(border));
            }

            return false;
        }

        private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
        {
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match)
                    return match;
                var nested = FindDescendant<T>(child);
                if (nested != null)
                    return nested;
            }

            return null;
        }
    }
}
