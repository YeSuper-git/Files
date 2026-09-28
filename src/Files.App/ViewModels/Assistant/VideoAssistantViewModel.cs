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
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.Storage;

namespace Files.App.ViewModels.Assistant;

public sealed class VideoAssistantContext
{
    public string LibraryPath { get; init; } = string.Empty;
    public Func<VideoAssistantCandidate, Task>? OpenLocationAsync { get; init; }
    public Func<Task>? OpenResourceManagerAsync { get; init; }
}

public enum VideoAssistantChoiceKind
{
    Actor,
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

public sealed partial class VideoAssistantMessage(
    string text,
    bool isUser,
    IReadOnlyList<VideoAssistantCandidateViewModel>? results = null,
    DateTimeOffset? timestamp = null,
    bool showTimestamp = false) : ObservableObject
{
    private int _resultIndex;

    public DateTimeOffset Timestamp { get; } = timestamp ?? DateTimeOffset.Now;
    public bool IsUser { get; } = isUser;
    public bool ShowTimestamp { get; } = showTimestamp;
    public string Text { get; } = text;
    public string Sender { get; } = isUser ? "你" : "小咪";
    public HorizontalAlignment Alignment { get; } = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public IReadOnlyList<VideoAssistantCandidateViewModel> Results { get; } = results ?? [];
    public VideoAssistantCandidateViewModel? CurrentResult
        => Results.Count == 0 ? null : Results[Math.Clamp(_resultIndex, 0, Results.Count - 1)];
    public Visibility ResultVisibility => Results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ResultNavigationVisibility => Results.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    public string ResultCounter => Results.Count == 0 ? string.Empty : $"{_resultIndex + 1} / {Results.Count}";

    public void MoveResult(int offset)
    {
        if (Results.Count < 2)
            return;

        _resultIndex = (_resultIndex + offset + Results.Count) % Results.Count;
        OnPropertyChanged(nameof(CurrentResult));
        OnPropertyChanged(nameof(ResultCounter));
    }

    public string TimeText
    {
        get
        {
            var localTime = Timestamp.ToLocalTime();
            if (localTime.Date == DateTime.Today)
                return localTime.ToString("HH:mm");
            if (localTime.Date == DateTime.Today.AddDays(-1))
                return $"昨天 {localTime:HH:mm}";
            return localTime.ToString("yyyy/M/d HH:mm");
        }
    }
    public Visibility TimestampVisibility => ShowTimestamp ? Visibility.Visible : Visibility.Collapsed;
}

public sealed partial class VideoAssistantCandidateViewModel(VideoAssistantCandidate candidate) : ObservableObject
{
    private BitmapImage? _poster;
    private ResourceVideoWatchStatus _watchStatus = candidate.WatchStatus;

    public string Name { get; } = candidate.Name;
    public string Code { get; } = candidate.Code;
    public string ActorName { get; } = candidate.ActorName;
    public string ActorPath { get; } = candidate.ActorPath;
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

    private DateTimeOffset? _lastWatchedAt = candidate.LastWatchedAt;
    public DateTimeOffset? LastWatchedAt
    {
        get => _lastWatchedAt;
        set
        {
            if (SetProperty(ref _lastWatchedAt, value))
                OnPropertyChanged(nameof(LastWatchedAtText));
        }
    }

    public string LastWatchedAtText => LastWatchedAt is DateTimeOffset watchedAt
        ? $"上次观看：{watchedAt.ToLocalTime():yyyy/M/d}"
        : "未观看";
}

internal sealed class VideoAssistantHistoryEntry
{
    public string Text { get; set; } = string.Empty;
    public bool IsUser { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public bool ShowTimestamp { get; set; }
    public List<VideoAssistantHistoryResult> Results { get; set; } = [];
}

internal sealed class VideoAssistantHistoryResult
{
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public string ActorName { get; set; } = string.Empty;
    public string ActorPath { get; set; } = string.Empty;
    public string? PosterPath { get; set; }
    public List<string> Tags { get; set; } = [];
    public ResourceVideoWatchStatus WatchStatus { get; set; }
    public DateTimeOffset? LastWatchedAt { get; set; }
    public string Reason { get; set; } = string.Empty;
    public List<string> LocationPaths { get; set; } = [];
    public List<ResourceBrowserLocationKind> LocationKinds { get; set; } = [];
    public List<string> LocationTitles { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(List<VideoAssistantHistoryEntry>))]
internal sealed partial class VideoAssistantHistoryJsonSerializerContext : JsonSerializerContext
{
}

public sealed partial class VideoAssistantViewModel : ObservableObject
{
    private const int ActorsPerPage = 6;
    private readonly VideoAssistantSearchService _searchService;
    private readonly IResourceWorkspaceService _workspace;
    private readonly string _historyFilePath;
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
    private VideoAssistantChoice? _batchAction;

    public ObservableCollection<VideoAssistantMessage> Messages { get; } = [];
    public ObservableCollection<VideoAssistantChoice> Suggestions { get; } = [];

    public VideoAssistantChoice? BatchAction
    {
        get => _batchAction;
        private set
        {
            if (SetProperty(ref _batchAction, value))
            {
                OnPropertyChanged(nameof(BatchActionLabel));
                OnPropertyChanged(nameof(BatchActionVisibility));
            }
        }
    }

    public string BatchActionLabel => BatchAction?.Label ?? string.Empty;
    public Visibility BatchActionVisibility => BatchAction is null ? Visibility.Collapsed : Visibility.Visible;

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
        _historyFilePath = Path.Combine(
            ApplicationData.Current.LocalFolder.Path,
            Files.App.Constants.LocalSettings.SettingsFolderName,
            "video-assistant-history.json");
        RestoreHistory();
    }

    public async Task ConfigureAsync(VideoAssistantContext context)
    {
        var previousLibraryPath = _context.LibraryPath;
        _context = context;
        IsBusy = true;
        try
        {
            _actors = await _searchService.GetActorsAsync(context.LibraryPath);
            _ = RestoreHistoryPostersAsync();

            if (!_hasBeenConfigured)
            {
                _hasBeenConfigured = true;
                if (Messages.Count == 0)
                    await StartConversationAsync();
                else
                    ResumeRestoredConversation();
                return;
            }

            if (!PathEquals(previousLibraryPath, context.LibraryPath))
            {
                await StartConversationAsync();
                return;
            }

            AddAssistantMessage(CreateReturnGreeting());
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ResumeRestoredConversation()
    {
        if (!Directory.Exists(_context.LibraryPath))
        {
            AddAssistantMessage("欢迎回来，之前的聊天记录已恢复。目前还没有可用资源库，你可以继续查看历史记录，或先选择视频根目录。");
            SetSuggestions(new VideoAssistantChoice("去设置资源库", VideoAssistantChoiceKind.OpenResourceManager));
        }
        else if (_actors.Count == 0)
        {
            AddAssistantMessage("欢迎回来，之前的聊天记录已恢复。当前资源库里没有可选演员文件夹，请检查资源库路径。");
            SetSuggestions(new VideoAssistantChoice("打开资源管理", VideoAssistantChoiceKind.OpenResourceManager));
        }
        else
        {
            AddAssistantMessage("欢迎回来，之前的聊天记录和推荐都还在。你可以继续查看历史，也可以从下方开始新的筛选。");
            AskForActor();
        }
    }

    public async Task StartConversationAsync()
    {
        var hadHistory = Messages.Count > 0;
        Suggestions.Clear();
        BatchAction = null;
        Draft = string.Empty;
        _selectedActor = null;
        _watchFilter = VideoAssistantWatchFilter.Any;
        _selectedTagId = null;
        _keyword = string.Empty;
        _actorPage = 0;
        _excludedPaths.Clear();
        _hasShownResults = false;

        if (hadHistory)
            AddAssistantMessage("好的，我们开始新一轮提问，之前的聊天记录会保留在上方。");

        if (!Directory.Exists(_context.LibraryPath))
        {
            AddAssistantMessage(PickReply(
                "你好，我是小咪。目前还没有可用的本地资源库；先选择视频根目录，我就能帮你筛选。",
                "嗨，欢迎来找我！我还没找到本地资源库，先去资源管理中选择视频根目录吧。"));
            SetSuggestions(new VideoAssistantChoice("去设置资源库", VideoAssistantChoiceKind.OpenResourceManager));
            return;
        }

        if (_actors.Count == 0)
        {
            AddAssistantMessage(PickReply(
                "你好，我是小咪。这个资源库里暂时没有可选的演员文件夹，可以先检查一下资源库路径。",
                "嗨，我来帮你挑作品啦，不过还没找到演员文件夹；确认资源库路径后再试试吧。"));
            SetSuggestions(new VideoAssistantChoice("打开资源管理", VideoAssistantChoiceKind.OpenResourceManager));
            return;
        }

        AddAssistantMessage(CreateLibraryGreeting());
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
                case VideoAssistantChoiceKind.MoreActors:
                    _actorPage = (_actorPage + 1) % (int)Math.Ceiling(_actors.Count / (double)ActorsPerPage);
                    AskForActor();
                    break;
                case VideoAssistantChoiceKind.WatchAgain:
                    _watchFilter = VideoAssistantWatchFilter.Watched;
                    await RecommendAsync(PickReply(
                        "明白，来看看适合二刷、已标记看过的作品。",
                        "好，我按二刷路线来帮你挑，先从看过的内容里找。",
                        "收到，这次先从已标记看过的作品里挑。"), true);
                    break;
                case VideoAssistantChoiceKind.NotMarkedWatched:
                    _watchFilter = VideoAssistantWatchFilter.NotMarkedWatched;
                    await RecommendAsync(PickReply(
                        "好，我先找还没标记看过的作品；状态尚未确认的也会一起列出来。",
                        "明白，这轮就看看还没标记看过的内容，未确认状态也算在内。",
                        "收到，我会避开已标记看过的作品，优先翻翻还没确认的。"), true);
                    break;
                case VideoAssistantChoiceKind.Any:
                    _watchFilter = VideoAssistantWatchFilter.Any;
                    await RecommendAsync(PickReply(
                        "好，那就不限制观看状态，我直接从资源库里挑几部。",
                        "收到，这次先不管看过没有，看看有哪些合适的。",
                        "可以，我跳过观看状态筛选，直接给你找些选择。"), true);
                    break;
                case VideoAssistantChoiceKind.ChangeActor:
                    ResetActorSelection();
                    AddAssistantMessage(PickReply(
                        "好，我们换位演员逛逛。下面这些都是资源库里的演员，你挑一个就行。",
                        "没问题，换个方向看看。你可以从下面选一位，也可以直接输入名字。",
                        "当然可以，再挑一位吧；如果列表里没有，也可以输入演员名或别名。"));
                    AskForActor();
                    break;
                case VideoAssistantChoiceKind.NextBatch:
                    foreach (var path in Messages.LastOrDefault(message => message.Results.Count > 0)?.Results.Select(item => item.Path) ?? [])
                        _excludedPaths.Add(path);
                    await RecommendAsync(PickReply(
                        "好，我按刚才的条件再翻一批给你。",
                        "这批看完了？我继续按同样的条件找找。",
                        "收到，筛选条件不变，我换一组作品。"), false);
                    break;
                case VideoAssistantChoiceKind.Tag:
                    _selectedTagId = choice.Value;
                    var tagName = _workspace.GetResourceTagById(choice.Value ?? string.Empty)?.Name ?? "指定标签";
                    await RecommendAsync(PickReply(
                        $"好，我把范围收窄到带“{tagName}”标签的作品。",
                        $"收到，这次优先看看标了“{tagName}”的内容。",
                        $"那就按“{tagName}”这个标签来找。"), true);
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
        candidate.LastWatchedAt = _workspace.GetVideoLastWatchedAt(candidate.Path);
        PersistHistory();
    }

    public void ToggleWantToWatch(VideoAssistantCandidateViewModel candidate)
    {
        var status = candidate.WatchStatus == ResourceVideoWatchStatus.WantToWatch
            ? ResourceVideoWatchStatus.Unknown
            : ResourceVideoWatchStatus.WantToWatch;
        _workspace.SetVideoWatchStatus(candidate.Path, status);
        candidate.WatchStatus = status;
        PersistHistory();
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

        var hasExtraFilter = tag is not null || ExtractKeyword(text, _selectedActor, tag).Length > 0;
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
        if (_keyword.Length > 0 || tag is not null || ContainsAny(text, "全部", "所有", "推荐"))
        {
            _watchFilter = watchFilter ?? VideoAssistantWatchFilter.Any;
            await RecommendAsync("我按你的描述在资源库里找了一遍。", true);
            return;
        }

        if (_actors.Count > 0)
        {
            AddAssistantMessage("没有匹配到具体演员。");
            AskForActor();
            return;
        }

        AddAssistantMessage("暂时没有可筛选的演员或视频。请先确认资源库路径和其中的演员文件夹。");
        SetSuggestions(new VideoAssistantChoice("打开资源管理", VideoAssistantChoiceKind.OpenResourceManager));
    }

    private void AskForActor()
    {
        var choices = _actors
            .Skip(_actorPage * ActorsPerPage)
            .Take(ActorsPerPage)
            .Select(actor => new VideoAssistantChoice(actor.Name, VideoAssistantChoiceKind.Actor, actor.Path))
            .ToList();
        if (_actors.Count > ActorsPerPage)
            choices.Add(new VideoAssistantChoice("换一批", VideoAssistantChoiceKind.MoreActors));
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

        AddAssistantMessage(CreateActorSelectionReply(_selectedActor));
        SetSuggestions(
            new VideoAssistantChoice("二刷 · 已看过", VideoAssistantChoiceKind.WatchAgain),
            new VideoAssistantChoice("找未标记看过的", VideoAssistantChoiceKind.NotMarkedWatched),
            new VideoAssistantChoice("直接看推荐", VideoAssistantChoiceKind.Any),
            new VideoAssistantChoice("换演员", VideoAssistantChoiceKind.ChangeActor));
        return Task.CompletedTask;
    }

    private string CreateActorSelectionReply(VideoAssistantActor actor)
    {
        string[] openings =
        [
            $"好呀，选了“{actor.Name}”。我来陪你从资源库里挑挑看。",
            $"“{actor.Name}”收到，先看看她的作品里有什么合你心意的。",
            $"选中“{actor.Name}”啦，我们接着细选。",
        ];
        string[] questions =
        [
            "接下来想重温已标记看过的，找还没标记的，还是不限制状态直接推荐？",
            "你想走哪条路线：二刷看过的、找尚未标记的，或者跳过状态筛选？",
            "然后由你来定：想找看过的作品、还没标记看过的，还是先不限状态挑几部？",
        ];

        var details = _workspace.GetActorDetails(actor.Path);
        var facts = new List<string>();
        var aliases = actor.Aliases
            .Split([',', '，', '/', '、', ';', '；', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (aliases.Length > 0)
            facts.Add($"本地资料卡还登记了别名“{string.Join("、", aliases.Take(2))}”");
        if (details.BirthDate is DateTime birthDate && birthDate <= DateTime.Today)
            facts.Add($"本地资料卡记录的生日是{birthDate:yyyy年M月d日}");
        if (details.IsCurrentlyActive is bool isActive)
            facts.Add($"本地资料卡把状态标为{(isActive ? "目前活跃" : "已结束活动")}");

        var profileNote = facts.Count > 0 ? PickReply(facts.ToArray()) + "。" : string.Empty;
        return $"{PickReply(openings)}{profileNote}{PickReply(questions)}";
    }

    private string CreateLibraryGreeting()
        => PickReply(
            "你好，我是小咪。想从哪位演员开始？可以点下面的名字，也可以直接输入演员名或别名。",
            "嗨，欢迎来找我挑作品！先选一位演员吧；如果没看到想找的人，也可以直接输入名字。",
            "小咪在这儿～下面是资源库里的演员，你可以点选一位，或者告诉我演员名和想看的内容。");

    private string CreateReturnGreeting()
    {
        if (_selectedActor is not null)
            return PickReply(
                $"欢迎回来，还想接着找“{_selectedActor.Name}”的作品吗？可以继续选提示词，也可以直接告诉我偏好。",
                $"又见面啦。刚才在看“{_selectedActor.Name}”，想接着筛选还是换个方向？",
                $"我在呢，要继续挑“{_selectedActor.Name}”的作品，还是试试别的演员？");

        if (Messages.Any(message => message.Results.Count > 0))
            return PickReply(
                "欢迎回来，刚才的筛选结果还在。想继续看，还是换个条件再找？",
                "我回来啦，之前的结果和筛选条件都保留着；你想接着挑还是换个方向？",
                "欢迎回来～可以继续看这批，也可以告诉我新的演员或筛选条件。");

        return PickReply(
            "欢迎回来，想继续从演员列表里挑吗？也可以直接输入演员名或作品偏好。",
            "我在这儿，我们继续挑作品吧。点一位演员，或者直接告诉我想找什么。",
            "又见面啦！下面这些演员都可以选，也可以直接输入名字或别名。");
    }

    private static string PickReply(params string[] options)
        => options[Random.Shared.Next(options.Length)];

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
                startedNewRound ? "这一轮已经结束，我重新为你换了一批作品。" : introduction,
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
            new("换一批作品", VideoAssistantChoiceKind.NextBatch),
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
        => AddMessage(text, true);

    private void AddAssistantMessage(string text, IReadOnlyList<VideoAssistantCandidateViewModel>? results = null)
        => AddMessage(text, false, results);

    private void AddMessage(string text, bool isUser, IReadOnlyList<VideoAssistantCandidateViewModel>? results = null)
    {
        var timestamp = DateTimeOffset.Now;
        var showTimestamp = Messages.Count == 0
            || timestamp - Messages[^1].Timestamp >= TimeSpan.FromMinutes(5);
        Messages.Add(new VideoAssistantMessage(text, isUser, results, timestamp, showTimestamp));
        PersistHistory();
    }

    private void RestoreHistory()
    {
        try
        {
            if (!File.Exists(_historyFilePath))
                return;

            var entries = JsonSerializer.Deserialize(
                File.ReadAllText(_historyFilePath),
                VideoAssistantHistoryJsonSerializerContext.Default.ListVideoAssistantHistoryEntry);
            foreach (var entry in entries ?? [])
            {
                var results = (entry.Results ?? [])
                    .Select(result => new VideoAssistantCandidateViewModel(new VideoAssistantCandidate
                    {
                        Name = result.Name,
                        Code = result.Code,
                        Path = result.Path,
                        FolderPath = result.FolderPath,
                        ActorName = result.ActorName,
                        ActorPath = result.ActorPath,
                        PosterPath = result.PosterPath,
                        Tags = result.Tags ?? [],
                        WatchStatus = result.WatchStatus,
                        LastWatchedAt = result.LastWatchedAt,
                        Reason = result.Reason,
                        LocationPaths = result.LocationPaths ?? [],
                        LocationKinds = result.LocationKinds ?? [],
                        LocationTitles = result.LocationTitles ?? [],
                    }))
                    .ToArray();
                Messages.Add(new VideoAssistantMessage(
                    entry.Text ?? string.Empty,
                    entry.IsUser,
                    results,
                    entry.Timestamp,
                    entry.ShowTimestamp));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unable to restore video assistant history: {ex}");
        }
    }

    private void PersistHistory()
    {
        var temporaryPath = $"{_historyFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var directory = Path.GetDirectoryName(_historyFilePath);
            if (directory is not null)
                Directory.CreateDirectory(directory);

            var entries = Messages.Select(message => new VideoAssistantHistoryEntry
            {
                Text = message.Text,
                IsUser = message.IsUser,
                Timestamp = message.Timestamp,
                ShowTimestamp = message.ShowTimestamp,
                Results = message.Results.Select(result => new VideoAssistantHistoryResult
                {
                    Name = result.Name,
                    Code = result.Code,
                    Path = result.Path,
                    FolderPath = result.FolderPath,
                    ActorName = result.ActorName,
                    ActorPath = result.ActorPath,
                    PosterPath = result.PosterPath,
                    Tags = result.Tags.ToList(),
                    WatchStatus = result.WatchStatus,
                    LastWatchedAt = result.LastWatchedAt,
                    Reason = result.Reason,
                    LocationPaths = result.LocationPaths.ToList(),
                    LocationKinds = result.LocationKinds.ToList(),
                    LocationTitles = result.LocationTitles.ToList(),
                }).ToList(),
            }).ToList();
            var content = JsonSerializer.Serialize(
                entries,
                VideoAssistantHistoryJsonSerializerContext.Default.ListVideoAssistantHistoryEntry);
            File.WriteAllText(temporaryPath, content);
            File.Move(temporaryPath, _historyFilePath, true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unable to save video assistant history: {ex}");
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // Keep the last successfully saved conversation history.
            }
        }
    }

    private async Task RestoreHistoryPostersAsync()
    {
        foreach (var result in Messages.SelectMany(message => message.Results).Where(result => result.Poster is null).ToArray())
            result.Poster = await LoadPosterAsync(result.PosterPath);
    }

    private void SetSuggestions(params VideoAssistantChoice[] choices)
        => SetSuggestions((IEnumerable<VideoAssistantChoice>)choices);

    private void SetSuggestions(IEnumerable<VideoAssistantChoice> choices)
    {
        var allChoices = choices.ToArray();
        BatchAction = allChoices.FirstOrDefault(choice => choice.Kind is VideoAssistantChoiceKind.MoreActors or VideoAssistantChoiceKind.NextBatch);

        Suggestions.Clear();
        foreach (var choice in allChoices)
        {
            if (choice == BatchAction)
                continue;

            Suggestions.Add(choice);
        }
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
