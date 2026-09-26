// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.Mvvm.DependencyInjection;
using Files.App.ViewModels.Assistant;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Files.App.UserControls.Assistant;

public sealed partial class VideoAssistantChatView : UserControl
{
    private bool _isObservingMessages;

    public VideoAssistantViewModel ViewModel { get; } = Ioc.Default.GetRequiredService<VideoAssistantViewModel>();

    public event EventHandler? CloseRequested;

    public VideoAssistantChatView()
    {
        InitializeComponent();
    }

    public Task ConfigureAsync(VideoAssistantContext context)
        => ViewModel.ConfigureAsync(context);

    private void OnViewLoaded(object sender, RoutedEventArgs e)
    {
        if (!_isObservingMessages)
        {
            ViewModel.Messages.CollectionChanged += Messages_CollectionChanged;
            _isObservingMessages = true;
        }

        ScrollToLatestMessage();
    }

    private void OnViewUnloaded(object sender, RoutedEventArgs e)
    {
        if (_isObservingMessages)
        {
            ViewModel.Messages.CollectionChanged -= Messages_CollectionChanged;
            _isObservingMessages = false;
        }
    }

    private void Messages_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => DispatcherQueue.TryEnqueue(ScrollToLatestMessage);

    private void ScrollToLatestMessage()
    {
        MessagesItemsControl.UpdateLayout();
        MessagesScrollViewer.ChangeView(null, MessagesScrollViewer.ExtentHeight, null, true);
    }

    private async void SuggestionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: VideoAssistantChoice choice })
            await ViewModel.ChooseAsync(choice);
    }

    private async void BatchActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.BatchAction is { } choice)
            await ViewModel.ChooseAsync(choice);
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
        => await ViewModel.SubmitAsync();

    private async void MessageInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;

        e.Handled = true;
        await ViewModel.SubmitAsync();
    }

    private void NewConversationButton_Click(object sender, RoutedEventArgs e)
        => _ = ViewModel.StartConversationAsync();

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OpenVideoButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: VideoAssistantCandidateViewModel candidate })
            ViewModel.OpenVideo(candidate);
    }

    private async void OpenLocationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: VideoAssistantCandidateViewModel candidate })
            await ViewModel.OpenLocationAsync(candidate);
    }

    private void ToggleWatchedButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: VideoAssistantCandidateViewModel candidate })
            ViewModel.ToggleWatched(candidate);
    }

    private void ToggleWantToWatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: VideoAssistantCandidateViewModel candidate })
            ViewModel.ToggleWantToWatch(candidate);
    }

    private void PreviousRecommendationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: VideoAssistantMessage message })
            message.MoveResult(-1);
    }

    private void NextRecommendationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: VideoAssistantMessage message })
            message.MoveResult(1);
    }
}
