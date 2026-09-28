// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Services.SizeProvider
{
	public sealed partial class NoSizeProvider : ISizeProvider
	{
		// This provider deliberately never reports size changes. Keep the interface
		// contract with a no-op explicit event implementation instead of creating
		// an event that can never be raised.
		event EventHandler<SizeChangedEventArgs> ISizeProvider.SizeChanged
		{
			add { }
			remove { }
		}

		public Task CleanAsync() => Task.CompletedTask;
		public Task ClearAsync() => Task.CompletedTask;

		public Task UpdateAsync(string path, CancellationToken cancellationToken)
			=> Task.CompletedTask;

		public bool TryGetSize(string path, out ulong size)
		{
			size = 0;
			return false;
		}

		public void Dispose() { }
	}
}
