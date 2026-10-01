// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;
using Files.App.Data.Items.ResourceManager;
using Files.App.ViewModels.Properties;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Files.App.ViewModels.Previews
{
	public abstract partial class BasePreviewModel : ObservableObject
	{
		private readonly IUserSettingsService userSettingsService = Ioc.Default.GetRequiredService<IUserSettingsService>();

		public ListedItem Item { get; }

		protected BaseStorageFile PreviewFile
			=> Item.ItemFile ?? throw new InvalidOperationException("The preview file has not been loaded.");

		private BitmapImage? fileImage;
		public BitmapImage? FileImage
		{
			get => fileImage;
			protected set => SetProperty(ref fileImage, value);
		}

		public List<FileProperty>? DetailsFromPreview { get; set; }

		/// <summary>
		/// This is cancelled when the user has selected another file or closed the pane.
		/// </summary>
		public CancellationTokenSource LoadCancelledTokenSource { get; } = new CancellationTokenSource();

		public BasePreviewModel(ListedItem item) : base()
			=> Item = item;

		public delegate void LoadedEventHandler(object? sender, EventArgs e);

		public static Task LoadDetailsOnlyAsync(ListedItem item, List<FileProperty>? details = null)
		{
			var temp = new DetailsOnlyPreviewModel(item) { DetailsFromPreview = details };
			return temp.LoadAsync();
		}

		public static Task<string> ReadFileAsTextAsync(BaseStorageFile file, int maxLength = 10 * 1024 * 1024)
			=> file.ReadTextAsync(maxLength);

		/// <summary>
		/// Call this function when you are ready to load the preview and details.
		/// Override if you need custom loading code.
		/// </summary>
		/// <returns>The task to run</returns>
		public virtual async Task LoadAsync()
		{
			List<FileProperty> detailsFull = [];

			if (Item.ItemFile is null)
			{
				var itemPath = Item.ItemPath!;
				var rootItem = await FilesystemTasks.WrapNullable(() => DriveHelpers.GetRootFromPathAsync(itemPath));
				Item.ItemFile = await StorageFileExtensions.DangerousGetFileFromPathAsync(itemPath, rootItem.Result);
			}

			await Task.Run(async () =>
			{
				DetailsFromPreview = await LoadPreviewAndDetailsAsync();
				if (userSettingsService.InfoPaneSettingsService.SelectedTab == InfoPaneTabs.Details)
				{
					// Add the details from the preview function, then the system file properties
					DetailsFromPreview?.ForEach(i => detailsFull.Add(i));
					List<FileProperty>? props = await GetSystemFilePropertiesAsync();
					if (props is not null)
						detailsFull.AddRange(props);
				}
			});

			Item.FileDetails = new System.Collections.ObjectModel.ObservableCollection<FileProperty>(detailsFull);
		}

		/// <summary>
		/// Override this and place the code to load the file preview here.
		/// You can return details that may have been obtained while loading the preview (eg. word count).
		/// This details will be displayed *before* the system file properties.
		/// If there are none, return an empty list.
		/// </summary>
		/// <returns>A list of details</returns>
		public async virtual Task<List<FileProperty>> LoadPreviewAndDetailsAsync()
		{
			var result = await FileThumbnailHelper.GetIconAsync(
				Item.ItemPath,
				Constants.ShellIconSizes.Jumbo,
				false,
				IconOptions.None);

			if (result is not null)
				await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(async () => FileImage = await result.ToBitmapAsync());
			else
				FileImage ??= await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(() => new BitmapImage());

			return [];
		}

		/// <summary>
		/// Override this if the preview control needs to handle the unloaded event.
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		public virtual void PreviewControlBase_Unloaded(object? sender, RoutedEventArgs e)
			=> LoadCancelledTokenSource.Cancel();

		protected static FileProperty GetFileProperty(string nameResource, object? value)
			=> new() { NameResource = nameResource, Value = value };

		private async Task<List<FileProperty>?> GetSystemFilePropertiesAsync()
		{
			if (Item.IsShortcut)
				return null;
			if (Item.ItemFile is null)
				throw new InvalidOperationException("The preview item could not be opened as a file.");

			var list = await FileProperty.RetrieveAndInitializePropertiesAsync(Item.ItemFile,
				Constants.ResourceFilePaths.PreviewPaneDetailsPropertiesJsonPath);

			var address = list.Find(x => x.ID is "address")
				?? throw new InvalidDataException("The preview property definition is missing the address field.");
			var latitude = list.Find(x => x.Property is "System.GPS.LatitudeDecimal")
				?? throw new InvalidDataException("The preview property definition is missing the latitude field.");
			var longitude = list.Find(x => x.Property is "System.GPS.LongitudeDecimal")
				?? throw new InvalidDataException("The preview property definition is missing the longitude field.");
			address.Value = await LocationHelpers.GetAddressFromCoordinatesAsync(
				(double?)latitude.Value,
				(double?)longitude.Value);

			// Adds the value for the file tag
			var fileTag = list.FirstOrDefault(x => x.ID is "filetag")
				?? throw new InvalidDataException("The preview property definition is missing the file tag field.");
			fileTag.Value = Item.FileTagsUI is not null
				? string.Join(',', Item.FileTagsUI.Select(x => x.Name))
				: null;

			if (Item is ResourceVideoFileListedItem)
			{
				FormatResourceVideoDetails(list);
				if (Item.FileSize is { Length: > 0 } size)
					list.Insert(0, new FileProperty { NameResource = nameof(Strings.Size), Value = size });
			}

			return list.Where(i => i.ValueText is not null).ToList();
		}

		private static void FormatResourceVideoDetails(List<FileProperty> properties)
		{
			var duration = properties.Find(property => property.Property is "System.Media.Duration");
			if (duration is not null)
				duration.NameResource = nameof(Strings.ResourceManagerVideoLength);

			var frameRate = properties.Find(property => property.Property is "System.Video.FrameRate");
			if (frameRate is not null)
				frameRate.NameResource = nameof(Strings.ResourceManagerVideoFrameRate);

			var widthProperty = properties.Find(property => property.Property is "System.Video.FrameWidth");
			var heightProperty = properties.Find(property => property.Property is "System.Video.FrameHeight");
			if (widthProperty is null || heightProperty is null)
				return;

			var insertIndex = properties.IndexOf(widthProperty);
			var width = 0;
			var height = 0;
			var hasResolution = int.TryParse(widthProperty.Value?.ToString(), out width) && width > 0 &&
				int.TryParse(heightProperty.Value?.ToString(), out height) && height > 0;
			properties.Remove(widthProperty);
			properties.Remove(heightProperty);
			if (!hasResolution)
				return;

			properties.Insert(insertIndex, new FileProperty
			{
				NameResource = nameof(Strings.ResourceManagerVideoResolution),
				Value = $"{width}x{height}",
			});
			properties.Insert(insertIndex + 1, new FileProperty
			{
				NameResource = nameof(Strings.ResourceManagerVideoQuality),
				Value = GetResourceVideoQuality(width, height),
			});
		}

		private static string GetResourceVideoQuality(int width, int height)
		{
			var longEdge = Math.Max(width, height);
			var shortEdge = Math.Min(width, height);
			var aspectRatio = (double)longEdge / shortEdge;
			var standardFormats = new (int Width, int Height, string Label)[]
			{
				(7680, 4320, Strings.ResourceManagerVideoQuality8K.GetLocalizedResource()),
				(3840, 2160, Strings.ResourceManagerVideoQuality4K.GetLocalizedResource()),
				(2560, 1440, Strings.ResourceManagerVideoQualityQhd.GetLocalizedResource()),
				(1920, 1080, Strings.ResourceManagerVideoQualityFullHd.GetLocalizedResource()),
				(1280, 720, Strings.ResourceManagerVideoQualityHd.GetLocalizedResource()),
			};

			foreach (var format in standardFormats)
			{
				if (longEdge == format.Width && shortEdge == format.Height)
					return $"{format.Height}P {format.Label}";
			}

			if (aspectRatio <= 2.5)
			{
				var nearest = standardFormats
					.Where(format => Math.Abs(shortEdge - format.Height) / (double)format.Height <= 0.08 &&
						longEdge >= format.Width * 0.8 && longEdge <= format.Width * 1.08)
						.OrderBy(format => Math.Abs(shortEdge - format.Height) / (double)format.Height +
							Math.Abs(longEdge - format.Width) / (double)format.Width)
						.FirstOrDefault();
				if (nearest.Height > 0)
					return string.Format(Strings.ResourceManagerVideoQualityNear.GetLocalizedResource(), shortEdge, nearest.Height, nearest.Label);

				foreach (var format in standardFormats)
				{
					if (Math.Abs(longEdge - format.Width) / (double)format.Width <= 0.08 &&
						shortEdge >= format.Height * 0.65 && shortEdge < format.Height * 0.92)
						return string.Format(Strings.ResourceManagerVideoQualityWide.GetLocalizedResource(), format.Label);
				}
			}

			var qualityResource = shortEdge switch
			{
				>= 4320 => Strings.ResourceManagerVideoQuality8K,
				>= 2160 => Strings.ResourceManagerVideoQuality4K,
				>= 1440 => Strings.ResourceManagerVideoQualityQhd,
				>= 1080 => Strings.ResourceManagerVideoQualityFullHd,
				>= 720 => Strings.ResourceManagerVideoQualityHd,
				_ => Strings.ResourceManagerVideoQualitySd,
			};
			return string.Format(Strings.ResourceManagerVideoQualityOther.GetLocalizedResource(), shortEdge, qualityResource.GetLocalizedResource());
		}

		private sealed partial class DetailsOnlyPreviewModel : BasePreviewModel
		{
			public DetailsOnlyPreviewModel(ListedItem item) : base(item) { }

			public override Task<List<FileProperty>> LoadPreviewAndDetailsAsync() => Task.FromResult(DetailsFromPreview ?? []);
		}
	}
}
