// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.Mvvm.ComponentModel;
using Files.App.Data.Models.ResourceManager;
using Files.App.Services.ResourceManager;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Windows.Storage;

namespace Files.App.ViewModels.Assistant;

public sealed class VideoAssistantContext
{
    public string LibraryPath { get; init; } = string.Empty;
    public string? CurrentActorPath { get; init; }
    public Func<VideoAssistantCandidate, Task>? OpenLocationAsync { get; init; }
    public Func<Task>? OpenResourceManagerAsync { get; init; }
}

public enum VideoAssistantChoiceKind
{
    Actor,
    RandomActor,
    DirectRandom,
    MoreActors,
    WatchAgain,
    NotMarkedWatched,
    Any,
    ChangeActor,
    NextBatch,
    Tag,
    OpenResourceManager,
}

public sealed class VideoAssistantChoice(string label, VideoAssistantChoiceKind kind, string? value = null)
{
    public string Label { get; } = label;
    public VideoAssistantChoiceKind Kind { get; } = kind;
    public string? Value { get; } = value;
}

public sealed class VideoAssistantMessage(string text, bool isUser, IReadOnlyList<VideoAssistantCandidateViewModel>? results = null)
{
    public string Text { get; } = text;
    public string Sender { get; } = isUser ? "你" : "视频助手";
    public HorizontalAlignment Alignment { get; } = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public IReadOnlyList<VideoAssistantCandidateViewModel> Results { get; } = results ?? [];
}

public sealed partial class VideoAssistantCandidateViewModel(VideoAssistantCandidate candidate) : ObservableObject
{
    private BitmapImage? _poster;
    private ResourceVideoWatchStatus _watchStatus = candidate.WatchStatus;

    public string Name { get; } = candidate.Name;
    public string ActorName { get; } = candidate.ActorName;
    public string Path { get; } = candidate.Path;
    public string FolderPath { get; } = candidate.FolderPath;
    public string Reason { get; } = candidate.Reason;
    public IReadOnlyList<string> Tags { get; } = candidate.Tags;
    public IReadOnlyList<string> LocationPaths { get; } = candidate.LocationPaths;
    public IReadOnlyList<ResourceBrowserLocationKind> LocationKinds { get; } = candidate.LocationKinds;
    public IReadOnlyList<string> LocationTitles { get; } = candidate.LocationTitles;
    public BitmapImage? Poster
    {
        get => _poster;
        set
        {
            if (SetProperty(ref _poster, value))
            {
                OnPropertyChanged(nameof(PosterVisibility));
                OnPropertyChanged(nameof(FallbackVisibility));
            }
        }
    }

    public ResourceVideoWatchStatus WatchStatus
    {
        get => _watchStatus;
        set
        {
            if (SetProperty(ref _watchStatus, value))
            {
                OnPropertyChanged(nameof(WatchStatusText));
                OnPropertyChanged(nameof(WatchButtonText));
                OnPropertyChanged(nameof(WantToWatchButtonText));
            }
        }
    }

    public string WatchStatusText => WatchStatus switch
    {
        ResourceVideoWatchStatus.Watched => "已看过",
        ResourceVideoWatchStatus.WantToWatch => "想看",
        _ => "未确认",
    };

    public string WatchButtonText => WatchStatus == ResourceVideoWatchStatus.Watched ? "取消已看" : "标记看过";
    public string WantToWatchButtonText => WatchStatus == ResourceVideoWatchStatus.WantToWatch ? "取消想看" : "加入想看";
    public Visibility PosterVisibility => Poster is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FallbackVisibility => Poster is null ? Visibility.Visible : Visibility.Collapsed;

    public string? PosterPath { get; } = candidate.PosterPath;
}

public sealed partial class VideoAssistantViewModel : ObservableObject
{
    private const int ActorsPerPage = 8;
    private readonly VideoAssistantSearchService _searchService;
    private readonly IResourceWorkspaceService _workspace;
    private readonly HashSet<string> _excludedPaths = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<VideoAssistantActor> _actors = [];
    private VideoAssistantContext _context = new();
    private VideoAssistantActor? _selectedActor;
    private VideoAssistantWatchFilter _watchFilter = VideoAssistantWatchFilter.Any;
    private string? _selectedTagId;
    private string _keyword = string.Empty;
    private int _actorPage;
    private bool _hasBeenConfigured;
    private bool _hasShownResults;
    private string _draft = string.Empty;
    private bool _isBusy;

    public ObservableCollection<VideoAssistantMessage> Messages { get; } = [];
    public ObservableCollection<VideoAssistantChoice> Suggestions { get; } = [];

    public string Draft
    {
        get => _draft;
        set => SetProperty(ref _draft, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                OnPropertyChanged(nameof(CanSend));
        }
    }

    public bool CanSend => !IsBusy;

    public VideoAssistantViewModel(VideoAssistantSearchService searchService, IResourceWorkspaceService workspace)
    {
        _searchService = searchService;
        _workspace = workspace;
    }

    public async Task ConfigureAsync(VideoAssistantContext context)
    {
        var previousLibraryPath = _context.LibraryPath;
        _context = context;
        IsBusy = true;
        try
        {
            _actors = await _searchService.GetActorsAsync(context.LibraryPath);

            if (!_hasBeenConfigured)
            {
                _hasBeenConfigured = true;
                await StartConversationAsync();
                return;
            }

            if (!PathEquals(previousLibraryPath, context.LibraryPath) && _actors.Count > 0)
            {
                await StartConversationAsync();
                return;
            }

            if (!string.IsNullOrWhiteSpace(context.CurrentActorPath))
            {
                var contextualActor = _actors.FirstOrDefault(actor => PathEquals(actor.Path, context.CurrentActorPath));
                if (contextualActor is not null && (_selectedActor is null || !PathEquals(_selectedActor.Path, contextualActor.Path)))
                {
                    _selectedActor = contextualActor;
                    _watchFilter = VideoAssistantWatchFilter.Any;
                    _selectedTagId = null;
                    _keyword = string.Empty;
                    _excludedPaths.Clear();
                    await AskWatchStatusAsync();
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task StartConversationAsync()
    {
        Messages.Clear();
        Suggestions.Clear();
        _selectedActor = null;
        _watchFilter = VideoAssistantWatchFilter.Any;
        _selectedTagId = null;
        _keyword = string.Empty;
        _actorPage = 0;
        _excludedPaths.Clear();
        _hasShownResults = false;

        if (!Directory.Exists(_context.LibraryPath))
        {
            AddAssistantMessage("还没有可用的本地资源库。先在资源管理中选择视频根目录，我就可以帮你筛选。 ");
            SetSuggestions(new VideoAssistantChoice("去设置资源库", VideoAssistantChoiceKind.OpenResourceManager));
            return;
        }

        if (_actors.Count == 0)
        {
            AddAssistantMessage("这个资源库里暂时没有可选的演员文件夹。你可以先检查资源库路径，或之后再来试试。");
            SetSuggestions(new VideoAssistantChoice("打开资源管理", VideoAssistantChoiceKind.OpenResourceManager));
            return;
        }

        if (!string.IsNullOrWhiteSpace(_context.CurrentActorPath))
        {
            _selectedActor = _actors.FirstOrDefault(actor => PathEquals(actor.Path, _context.CurrentActorPath));
            if (_selectedActor is not null)
            {
                await AskWatchStatusAsync();
                return;
            }
        }

        AskForActor();
    }

    public async Task SubmitAsync(string? input = null)
    {
        var text = (input ?? Draft).Trim();
        if (text.Length == 0 || IsBusy)
            return;

        Draft = string.Empty;
        AddUserMessage(text);
        IsBusy = true;
        try
        {
            await ProcessInputAsync(text);
        }
        catch (Exception ex)
        {
            AddAssistantMessage($"筛选时遇到问题：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void ShowNotice(string message)
        => AddAssistantMessage(message);

    public async Task ChooseAsync(VideoAssistantChoice choice)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            if (choice.Kind is not (VideoAssistantChoiceKind.MoreActors or VideoAssistantChoiceKind.OpenResourceManager))
                AddUserMessage(choice.Label);

            switch (choice.Kind)
            {
                case VideoAssistantChoiceKind.Actor:
                    _selectedActor = _actors.FirstOrDefault(actor => PathEquals(actor.Path, choice.Value));
                    if (_selectedActor is not null)
                    {
                        _actorPage = 0;
                        if (_watchFilter == VideoAssistantWatchFilter.Any)
                            await AskWatchStatusAsync();
                        else
                            await RecommendAsync(_watchFilter == VideoAssistantWatchFilter.Watched
                                ? $"我来找“{_selectedActor.Name}”已看过、适合二刷的作品。"
                                : $"我来找“{_selectedActor.Name}”还没看过的作品。", true);
                    }
                    break;
                case VideoAssistantChoiceKind.RandomActor:
                    _selectedActor = _actors[Random.Shared.Next(_actors.Count)];
                    _actorPage = 0;
                    await AskWatchStatusAsync();
                    break;
                case VideoAssistantChoiceKind.DirectRandom:
                    _selectedActor = null;
                    _watchFilter = VideoAssistantWatchFilter.Any;
                    _selectedTagId = null;
                    _keyword = string.Empty;
                    await RecommendAsync("我从资源库里随机挑了几部作品。", true);
                    break;
                case VideoAssistantChoiceKind.MoreActors:
                    _actorPage = (_actorPage + 1) % (int)Math.Ceiling(_actors.Count / (double)ActorsPerPage);
                    AskForActor(false);
                    break;
                case VideoAssistantChoiceKind.WatchAgain:
                    _watchFilter = VideoAssistantWatchFilter.Watched;
                    await RecommendAsync("这些是已标记看过、适合二刷的作品。", true);
                    break;
                case VideoAssistantChoiceKind.NotMarkedWatched:
                    _watchFilter = VideoAssistantWatchFilter.NotMarkedWatched;
                    await RecommendAsync("先给你找尚未标记为看过的作品；标记状态为“未确认”的内容也会列出。", true);
                    break;
                case VideoAssistantChoiceKind.Any:
                    _watchFilter = VideoAssistantWatchFilter.Any;
                    await RecommendAsync("好，我不限制看过状态，直接给你挑几部。", true);
                    break;
                case VideoAssistantChoiceKind.ChangeActor:
                    ResetActorSelection();
                    AskForActor();
                    break;
                case VideoAssistantChoiceKind.NextBatch:
                    foreach (var path in Messages.LastOrDefault(message => message.Results.Count > 0)?.Results.Select(item => item.Path) ?? [])
                        _excludedPaths.Add(path);
                    await RecommendAsync("换一批结果给你。", false);
                    break;
                case VideoAssistantChoiceKind.Tag:
                    _selectedTagId = choice.Value;
                    await RecommendAsync($"只看带“{_workspace.GetResourceTagById(choice.Value ?? string.Empty)?.Name ?? "指定标签"}”的作品。", true);
                    break;
                case VideoAssistantChoiceKind.OpenResourceManager:
                    if (_context.OpenResourceManagerAsync is not null)
                        await _context.OpenResourceManagerAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            AddAssistantMessage($"处理这个选项时遇到问题：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void OpenVideo(VideoAssistantCandidateViewModel candidate)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = candidate.Path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AddAssistantMessage($"无法打开这部视频：{ex.Message}");
        }
    }

    public async Task OpenLocationAsync(VideoAssistantCandidateViewModel candidate)
    {
        if (_context.OpenLocationAsync is not null)
        {
            await _context.OpenLocationAsync(new VideoAssistantCandidate
            {
                Name = candidate.Name,
                Path = candidate.Path,
                FolderPath = candidate.FolderPath,
                ActorName = candidate.ActorName,
                Tags = candidate.Tags,
                Reason = candidate.Reason,
                LocationPaths = candidate.LocationPaths,
                LocationKinds = candidate.LocationKinds,
                LocationTitles = candidate.LocationTitles,
            });
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = candidate.FolderPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AddAssistantMessage($"无法打开作品所在文件夹：{ex.Message}");
        }
    }

    public void ToggleWatched(VideoAssistantCandidateViewModel candidate)
    {
        var status = candidate.WatchStatus == ResourceVideoWatchStatus.Watched
            ? ResourceVideoWatchStatus.Unknown
            : ResourceVideoWatchStatus.Watched;
        _workspace.SetVideoWatchStatus(candidate.Path, status);
        candidate.WatchStatus = status;
    }

    public void ToggleWantToWatch(VideoAssistantCandidateViewModel candidate)
    {
        var status = candidate.WatchStatus == ResourceVideoWatchStatus.WantToWatch
            ? ResourceVideoWatchStatus.Unknown
            : ResourceVideoWatchStatus.WantToWatch;
        _workspace.SetVideoWatchStatus(candidate.Path, status);
        candidate.WatchStatus = status;
    }

    private async Task ProcessInputAsync(string text)
    {
        if (ContainsAny(text, "换一批", "再来一部", "再推荐", "换一部") && _hasShownResults)
        {
            foreach (var path in Messages.LastOrDefault(message => message.Results.Count > 0)?.Results.Select(item => item.Path) ?? [])
                _excludedPaths.Add(path);
            await RecommendAsync("换一批结果给你。", false);
            return;
        }

        if (ContainsAny(text, "换演员", "换一位", "重新选演员"))
        {
            ResetActorSelection();
            AskForActor();
            return;
        }

        var actor = FindActor(text);
        if (actor is not null && (_selectedActor is null || !PathEquals(_selectedActor.Path, actor.Path)))
        {
            _selectedActor = actor;
            _watchFilter = VideoAssistantWatchFilter.Any;
            _selectedTagId = null;
            _keyword = string.Empty;
            _actorPage = 0;
            _excludedPaths.Clear();
        }

        var watchFilter = ParseWatchFilter(text);
        if (watchFilter.HasValue)
            _watchFilter = watchFilter.Value;

        var tag = FindTag(text);
        if (tag is not null)
            _selectedTagId = tag.Uid;

        var randomActor = ContainsAny(text, "随机一位", "随便一位", "推荐个演员", "随机演员");
        var directRandom = ContainsAny(text, "直接随机", "随机推荐", "随便挑", "随便推荐");
        if (randomActor && _actors.Count > 0)
        {
            _selectedActor = _actors[Random.Shared.Next(_actors.Count)];
            _actorPage = 0;
            await AskWatchStatusAsync();
            return;
        }

        var hasExtraFilter = tag is not null || ExtractKeyword(text, _selectedActor, tag).Length > 0;
        if (directRandom)
        {
            _selectedActor = null;
            _selectedTagId = null;
            _keyword = string.Empty;
            _watchFilter = VideoAssistantWatchFilter.Any;
            await RecommendAsync("我从整个资源库里随机挑了几部。", true);
            return;
        }

        if (_selectedActor is not null)
        {
            _keyword = ExtractKeyword(text, _selectedActor, tag);
            if (watchFilter.HasValue || hasExtraFilter || ContainsAny(text, "直接推荐", "不限", "都可以", "随便看"))
            {
                await RecommendAsync($"按“{_selectedActor.Name}”为你筛选作品。", true);
                return;
            }

            await AskWatchStatusAsync();
            return;
        }

        _keyword = ExtractKeyword(text, null, tag);
        if (_keyword.Length > 0 || tag is not null || ContainsAny(text, "全部", "所有", "随机", "推荐", "随便"))
        {
            _watchFilter = watchFilter ?? VideoAssistantWatchFilter.Any;
            await RecommendAsync("我按你的描述在资源库里找了一遍。", true);
            return;
        }

        if (_actors.Count > 0)
        {
            AddAssistantMessage("我还没匹配到具体演员。你可以点一个演员，或直接输入演员名/别名；也可以先随机推荐。");
            AskForActor(false);
            return;
        }

        AddAssistantMessage("暂时没有可筛选的演员或视频。请先确认资源库路径和其中的演员文件夹。");
        SetSuggestions(new VideoAssistantChoice("打开资源管理", VideoAssistantChoiceKind.OpenResourceManager));
    }

    private void AskForActor(bool addMessage = true)
    {
        if (addMessage)
            AddAssistantMessage("今天想看哪位演员的作品？可以点选演员，也可以直接输入姓名或别名；你也可以跳过，随机看看。");

        var choices = _actors
            .Skip(_actorPage * ActorsPerPage)
            .Take(ActorsPerPage)
            .Select(actor => new VideoAssistantChoice(actor.Name, VideoAssistantChoiceKind.Actor, actor.Path))
            .ToList();
        if (_actors.Count > ActorsPerPage)
            choices.Add(new VideoAssistantChoice("更多演员…", VideoAssistantChoiceKind.MoreActors));
        choices.Add(new VideoAssistantChoice("随机选一位演员", VideoAssistantChoiceKind.RandomActor));
        choices.Add(new VideoAssistantChoice("直接随机推荐", VideoAssistantChoiceKind.DirectRandom));
        SetSuggestions(choices);
    }

    private void ResetActorSelection()
    {
        _selectedActor = null;
        _watchFilter = VideoAssistantWatchFilter.Any;
        _selectedTagId = null;
        _keyword = string.Empty;
        _actorPage = 0;
        _excludedPaths.Clear();
    }

    private Task AskWatchStatusAsync()
    {
        if (_selectedActor is null)
        {
            AskForActor();
            return Task.CompletedTask;
        }

        AddAssistantMessage($"想重温“{_selectedActor.Name}”已标记看过的作品，还是找还没标记看过的？也可以直接看推荐，不限制观看状态。");
        SetSuggestions(
            new VideoAssistantChoice("二刷 · 已看过", VideoAssistantChoiceKind.WatchAgain),
            new VideoAssistantChoice("找未标记看过的", VideoAssistantChoiceKind.NotMarkedWatched),
            new VideoAssistantChoice("直接看推荐", VideoAssistantChoiceKind.Any),
            new VideoAssistantChoice("换演员", VideoAssistantChoiceKind.ChangeActor));
        return Task.CompletedTask;
    }

    private async Task RecommendAsync(string introduction, bool resetExcluded)
    {
        if (resetExcluded)
            _excludedPaths.Clear();

        if (resetExcluded)
            _hasShownResults = false;

        IsBusy = true;
        try
        {
            var candidates = await _searchService.SearchAsync(
                _context.LibraryPath,
                _selectedActor?.Path,
                _watchFilter,
                _selectedTagId,
                _keyword,
                _excludedPaths);

            var startedNewRound = false;
            if (candidates.Count == 0 && !resetExcluded && _excludedPaths.Count > 0)
            {
                _excludedPaths.Clear();
                candidates = await _searchService.SearchAsync(
                    _context.LibraryPath,
                    _selectedActor?.Path,
                    _watchFilter,
                    _selectedTagId,
                    _keyword,
                    _excludedPaths);
                startedNewRound = candidates.Count > 0;
            }

            if (candidates.Count == 0)
            {
                AddAssistantMessage("没有找到符合这些条件的作品。你可以换个演员、放宽观看状态，或去掉标签条件再试。");
                SetResultSuggestions();
                return;
            }

            var viewModels = new List<VideoAssistantCandidateViewModel>(candidates.Count);
            foreach (var candidate in candidates)
            {
                var item = new VideoAssistantCandidateViewModel(candidate);
                item.Poster = await LoadPosterAsync(candidate.PosterPath);
                viewModels.Add(item);
            }

            _hasShownResults = true;
            AddAssistantMessage(
                startedNewRound ? "当前条件下已经展示完一轮，我重新抽了一轮。" : introduction,
                viewModels);
            SetResultSuggestions();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetResultSuggestions()
    {
        var choices = new List<VideoAssistantChoice>
        {
            new("换一批", VideoAssistantChoiceKind.NextBatch),
            new("换演员", VideoAssistantChoiceKind.ChangeActor),
            new("二刷已看过", VideoAssistantChoiceKind.WatchAgain),
            new("找未标记看过的", VideoAssistantChoiceKind.NotMarkedWatched),
        };
        var subtitleKeywords = _workspace.Settings.SubtitleKeywords;
        choices.AddRange(_workspace.ResourceTags
            .OrderByDescending(tag => subtitleKeywords.Any(keyword => tag.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .Take(3)
            .Select(tag => new VideoAssistantChoice($"只看：{tag.Name}", VideoAssistantChoiceKind.Tag, tag.Uid)));
        SetSuggestions(choices);
    }

    private VideoAssistantActor? FindActor(string text)
        => _actors
            .Where(actor => GetActorNames(actor).Any(name => text.Contains(name, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(actor => GetActorNames(actor).Max(name => name.Length))
            .FirstOrDefault();

    private ResourceTagDefinition? FindTag(string text)
        => _workspace.ResourceTags
            .Where(tag => text.Contains(tag.Name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(tag => tag.Name.Length)
            .FirstOrDefault();

    private static string[] GetActorNames(VideoAssistantActor actor)
        => new[] { actor.Name }
            .Concat(actor.Aliases.Split([',', '，', '/', '、', ';', '；', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static VideoAssistantWatchFilter? ParseWatchFilter(string text)
    {
        if (ContainsAny(text, "没看过", "未看过", "没看", "未标记", "没标记"))
            return VideoAssistantWatchFilter.NotMarkedWatched;
        if (ContainsAny(text, "二刷", "重看", "看过", "已看"))
            return VideoAssistantWatchFilter.Watched;
        return null;
    }

    private static string ExtractKeyword(string text, VideoAssistantActor? actor, ResourceTagDefinition? tag)
    {
        var value = text;
        if (actor is not null)
        {
            foreach (var name in GetActorNames(actor).OrderByDescending(name => name.Length))
                value = value.Replace(name, string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        if (tag is not null)
            value = value.Replace(tag.Name, string.Empty, StringComparison.OrdinalIgnoreCase);

        string[] fillerWords =
        [
            "今天想看", "我想看", "帮我找", "帮我挑", "推荐给我", "来一部", "找一部", "作品", "视频", "演员",
            "直接看推荐", "直接看", "直接", "随机推荐", "随机挑", "随便推荐", "随便挑", "随便看", "随便", "随机", "推荐", "想看", "要看", "二刷", "重看", "没看过", "未看过",
            "没看", "未标记", "没标记", "看过", "已看", "不限", "都可以", "随便看", "带", "有", "的", "一下",
        ];
        foreach (var word in fillerWords.OrderByDescending(word => word.Length))
            value = value.Replace(word, string.Empty, StringComparison.OrdinalIgnoreCase);

        return new string(value
            .Where(character => !char.IsPunctuation(character) && !char.IsWhiteSpace(character))
            .ToArray());
    }

    private static bool ContainsAny(string text, params string[] values)
        => values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private void AddUserMessage(string text)
        => Messages.Add(new VideoAssistantMessage(text, true));

    private void AddAssistantMessage(string text, IReadOnlyList<VideoAssistantCandidateViewModel>? results = null)
        => Messages.Add(new VideoAssistantMessage(text, false, results));

    private void SetSuggestions(params VideoAssistantChoice[] choices)
        => SetSuggestions((IEnumerable<VideoAssistantChoice>)choices);

    private void SetSuggestions(IEnumerable<VideoAssistantChoice> choices)
    {
        Suggestions.Clear();
        foreach (var choice in choices)
            Suggestions.Add(choice);
    }

    private static async Task<BitmapImage?> LoadPosterAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var image = new BitmapImage { DecodePixelWidth = 480 };
            await image.SetSourceAsync(stream);
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static bool PathEquals(string? left, string? right)
    {
        if (left is null || right is null)
            return false;
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
