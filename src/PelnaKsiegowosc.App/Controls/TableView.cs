using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using PelnaKsiegowosc.Presentation.ViewModels;

namespace PelnaKsiegowosc.App.Controls;

/// <summary>Tabela tylko do odczytu dla <see cref="TableData"/> — kolumny tworzone dynamicznie, kwoty wyrównane do prawej, wiersze sum pogrubione.</summary>
public sealed class TableView : UserControl
{
    public static readonly StyledProperty<TableData?> TableProperty = AvaloniaProperty.Register<TableView, TableData?>(nameof(Table));
    public static readonly StyledProperty<TableRow?> SelectedRowProperty =
        AvaloniaProperty.Register<TableView, TableRow?>(nameof(SelectedRow), defaultBindingMode: BindingMode.TwoWay);

    private static readonly IValueConverter BoldConverter = new FuncValueConverter<bool, FontWeight>(b => b ? FontWeight.Bold : FontWeight.Normal);

    private readonly DataGrid _grid = new()
    {
        IsReadOnly = true, CanUserSortColumns = false, CanUserReorderColumns = false, CanUserResizeColumns = true, SelectionMode = DataGridSelectionMode.Single,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, HeadersVisibility = DataGridHeadersVisibility.Column, MinHeight = 80
    };
    private readonly TextBlock _notes = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 0) };

    public TableView()
    {
        var dock = new DockPanel();
        DockPanel.SetDock(_notes, Dock.Bottom);
        dock.Children.Add(_notes);
        dock.Children.Add(_grid);
        Content = dock;
        _grid.SelectionChanged += (_, _) => SelectedRow = _grid.SelectedItem as TableRow;
    }

    public TableData? Table
    {
        get => GetValue(TableProperty);
        set => SetValue(TableProperty, value);
    }

    public TableRow? SelectedRow
    {
        get => GetValue(SelectedRowProperty);
        set => SetValue(SelectedRowProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TableProperty) Rebuild(change.GetNewValue<TableData?>());
        else if (change.Property == SelectedRowProperty && !ReferenceEquals(_grid.SelectedItem, change.NewValue)) _grid.SelectedItem = change.NewValue;
    }

    private void Rebuild(TableData? t)
    {
        _grid.ItemsSource = null;
        _grid.Columns.Clear();
        if (t is null) return;
        for (var i = 0; i < t.Columns.Count; i++)
        {
            var c = t.Columns[i];
            var index = i;
            _grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = c.Header,
                Width = new DataGridLength(Math.Max(80, c.Width * 120)),
                CellTemplate = new FuncDataTemplate<TableRow>((_, _) =>
                {
                    var tb = new TextBlock
                    {
                        Margin = new Thickness(6, 2), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                        HorizontalAlignment = c.AlignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left
                    };
                    tb.Bind(TextBlock.TextProperty, new Binding($"{nameof(TableRow.Cells)}[{index}]"));
                    tb.Bind(TextBlock.FontWeightProperty, new Binding(nameof(TableRow.IsTotal)) { Converter = BoldConverter });
                    tb.Bind(ToolTip.TipProperty, new Binding($"{nameof(TableRow.Cells)}[{index}]"));
                    return tb;
                }, supportsRecycling: true)
            });
        }
        _grid.ItemsSource = t.Rows;
        _notes.Text = string.Join(Environment.NewLine, t.Notes);
        _notes.IsVisible = t.Notes.Count > 0;
    }
}
