#nullable enable

using MajMusic.Core.Playback;
using MajMusic.Presentation.Commands;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Exposes queue preferences without replacing the active audio resource.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Gets whether future queue navigation follows a stable random order.</summary>
        public bool IsShuffle => _playback.Queue.Shuffle;
        /// <summary>Gets whether playback repeats the queue or its current entry.</summary>
        public bool IsRepeating => _playback.Queue.RepeatMode != RepeatMode.None;
        /// <summary>Gets whether natural completion repeats the current entry.</summary>
        public bool IsRepeatOne => _playback.Queue.RepeatMode == RepeatMode.One;
        /// <summary>Gets the current automatic repeat behavior.</summary>
        public string RepeatModeText => _playback.Queue.RepeatMode switch
        {
            RepeatMode.All => "列表循环",
            RepeatMode.One => "单曲循环",
            _ => "顺序播放"
        };
        /// <summary>Gets the current queue-order behavior.</summary>
        public string ShuffleModeText => IsShuffle ? "随机播放" : "顺序播放";
        /// <summary>Gets the action toggling random queue navigation.</summary>
        public AsyncCommand ToggleShuffleCommand { get; private set; } = null!;
        /// <summary>Gets the action cycling through no repeat, queue repeat and single-track repeat.</summary>
        public AsyncCommand CycleRepeatModeCommand { get; private set; } = null!;

        /// <summary>Creates observed queue-preference actions bound to the view lifetime.</summary>
        private void InitializePlaybackModeCommands()
        {
            ToggleShuffleCommand = CreateCommand(ToggleShuffleAsync, () => CanPlay);
            CycleRepeatModeCommand = CreateCommand(CycleRepeatModeAsync, () => CanPlay);
        }

        /// <summary>Changes only the future queue order while preserving the loaded resource and timeline.</summary>
        /// <returns>A task representing the serialized preference update.</returns>
        private async Task ToggleShuffleAsync()
        {
            await _playback.SetShuffleAsync(!IsShuffle, _lifetime.Token);
            NotifyPlaybackModes();
        }

        /// <summary>Cycles automatic repeat behavior without preparing or restarting audio.</summary>
        /// <returns>A task representing the serialized preference update.</returns>
        private async Task CycleRepeatModeAsync()
        {
            var next = _playback.Queue.RepeatMode switch
            {
                RepeatMode.None => RepeatMode.All,
                RepeatMode.All => RepeatMode.One,
                _ => RepeatMode.None
            };
            await _playback.SetRepeatModeAsync(next, _lifetime.Token);
            NotifyPlaybackModes();
        }

        /// <summary>Refreshes mode labels and active-state bindings after a queue preference changes.</summary>
        private void NotifyPlaybackModes()
        {
            OnPropertyChanged(nameof(IsShuffle));
            OnPropertyChanged(nameof(IsRepeating));
            OnPropertyChanged(nameof(IsRepeatOne));
            OnPropertyChanged(nameof(RepeatModeText));
            OnPropertyChanged(nameof(ShuffleModeText));
        }
    }
}
