using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using IPTVPlayer.Models;
using Button = System.Windows.Controls.Button;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using Point = System.Windows.Point;

namespace IPTVPlayer.Views;

/// <summary>
/// Window for editing channel visibility and order
/// </summary>
public partial class ChannelEditorWindow : Window
{
    private readonly ObservableCollection<Channel> _channels;
    private readonly ObservableCollection<Channel> _editableChannels;
    private ICollectionView _filteredView;
    
    // Drag-and-drop state
    private Channel? _draggedChannel;
    private Point _dragStartPoint;

    public bool SaveChanges { get; private set; }

    public ChannelEditorWindow(ObservableCollection<Channel> channels)
    {
        InitializeComponent();
        _channels = channels;
        
        // Create a copy for editing
        _editableChannels = new ObservableCollection<Channel>();
        foreach (var channel in channels)
        {
            _editableChannels.Add(channel);
        }

        // Setup filtered view
        _filteredView = CollectionViewSource.GetDefaultView(_editableChannels);
        _filteredView.Filter = FilterChannel;
        _filteredView.SortDescriptions.Add(new SortDescription(nameof(Channel.SortOrder), ListSortDirection.Ascending));
        _filteredView.SortDescriptions.Add(new SortDescription(nameof(Channel.Id), ListSortDirection.Ascending));
        
        ChannelListView.ItemsSource = _filteredView;
    }

    private bool FilterChannel(object obj)
    {
        if (obj is not Channel channel) return false;

        // Check search text
        var searchText = SearchBox.Text?.Trim() ?? "";
        if (!string.IsNullOrEmpty(searchText))
        {
            if (!channel.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase) &&
                !(channel.Group?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                return false;
            }
        }

        // Check hidden filter
        if (!ShowHiddenCheckBox.IsChecked == true && channel.IsHidden)
        {
            return false;
        }

        return true;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;
            else
                WindowState = WindowState.Maximized;
        }
        else
        {
            DragMove();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        SaveChanges = false;
        Close();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filteredView.Refresh();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        _filteredView?.Refresh();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is Channel channel)
        {
            MoveChannel(channel, -1);
        }
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is Channel channel)
        {
            MoveChannel(channel, 1);
        }
    }

    private void MoveToTop_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is Channel channel)
        {
            var sortedChannels = _editableChannels.OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToList();
            var currentIndex = sortedChannels.IndexOf(channel);
            if (currentIndex <= 0) return;

            // Set this channel's sort order to be less than the current minimum
            var minOrder = sortedChannels[0].SortOrder;
            channel.SortOrder = minOrder - 1;

            _filteredView.Refresh();
            ChannelListView.SelectedItem = channel;
            ChannelListView.ScrollIntoView(channel);
        }
    }

    private void MoveToBottom_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is Channel channel)
        {
            var sortedChannels = _editableChannels.OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToList();
            var currentIndex = sortedChannels.IndexOf(channel);
            if (currentIndex >= sortedChannels.Count - 1) return;

            // Set this channel's sort order to be greater than the current maximum
            var maxOrder = sortedChannels[^1].SortOrder;
            channel.SortOrder = maxOrder + 1;

            _filteredView.Refresh();
            ChannelListView.SelectedItem = channel;
            ChannelListView.ScrollIntoView(channel);
        }
    }

    private void MoveChannel(Channel channel, int direction)
    {
        // Get the current sorted list
        var sortedChannels = _editableChannels.OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToList();
        var currentIndex = sortedChannels.IndexOf(channel);
        var newIndex = currentIndex + direction;

        if (newIndex < 0 || newIndex >= sortedChannels.Count)
            return;

        // Swap sort orders
        var otherChannel = sortedChannels[newIndex];
        var tempOrder = channel.SortOrder;
        channel.SortOrder = otherChannel.SortOrder;
        otherChannel.SortOrder = tempOrder;

        // If they have the same sort order, adjust to make them unique
        if (channel.SortOrder == otherChannel.SortOrder)
        {
            if (direction > 0)
                channel.SortOrder++;
            else
                otherChannel.SortOrder++;
        }

        _filteredView.Refresh();
        
        // Keep the moved item selected
        ChannelListView.SelectedItem = channel;
        ChannelListView.ScrollIntoView(channel);
    }

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is Channel channel)
        {
            _draggedChannel = channel;
            _dragStartPoint = e.GetPosition(ChannelListView);
            
            // Select the item being dragged
            ChannelListView.SelectedItem = channel;
            
            var data = new DataObject(typeof(Channel), channel);
            DragDrop.DoDragDrop(element, data, DragDropEffects.Move);
            _draggedChannel = null;
        }
    }

    private void ChannelListView_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(Channel)))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void ChannelListView_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(Channel))) return;
        
        var droppedChannel = e.Data.GetData(typeof(Channel)) as Channel;
        if (droppedChannel == null) return;

        // Find the target item under the mouse
        Channel? targetChannel = null;
        var pos = e.GetPosition(ChannelListView);
        var element = ChannelListView.InputHitTest(pos) as DependencyObject;
        
        while (element != null && element != ChannelListView)
        {
            if (element is FrameworkElement fe && fe.DataContext is Channel ch && ch != droppedChannel)
            {
                targetChannel = ch;
                break;
            }
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        }

        if (targetChannel == null || targetChannel == droppedChannel) return;

        // Get sorted list and compute new sort orders
        var sortedChannels = _editableChannels.OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToList();
        var dragIndex = sortedChannels.IndexOf(droppedChannel);
        var targetIndex = sortedChannels.IndexOf(targetChannel);

        if (dragIndex < 0 || targetIndex < 0 || dragIndex == targetIndex) return;

        // Remove from current position and insert at target
        sortedChannels.RemoveAt(dragIndex);
        var insertIndex = sortedChannels.IndexOf(targetChannel);
        if (dragIndex < targetIndex)
            insertIndex++; // Drop after target when dragging down
        sortedChannels.Insert(insertIndex, droppedChannel);

        // Reassign sort orders based on new positions
        for (int i = 0; i < sortedChannels.Count; i++)
        {
            sortedChannels[i].SortOrder = i;
        }

        _filteredView.Refresh();
        ChannelListView.SelectedItem = droppedChannel;
        ChannelListView.ScrollIntoView(droppedChannel);
    }

    private void ShowAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var channel in _editableChannels)
        {
            channel.IsHidden = false;
        }
        _filteredView.Refresh();
    }

    private void HideAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var channel in _editableChannels)
        {
            channel.IsHidden = true;
        }
        _filteredView.Refresh();
    }

    private void ResetOrder_Click(object sender, RoutedEventArgs e)
    {
        foreach (var channel in _editableChannels)
        {
            channel.SortOrder = channel.Id;
        }
        _filteredView.Refresh();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        SaveChanges = false;
        Close();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveChanges = true;
        Close();
    }
}
