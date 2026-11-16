#nullable enable

using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using MajMusic.Core.Models;
using MajMusic.Presentation.Converters;
using MajMusic.Presentation.Commands;
using MajMusic.Presentation.ViewModels;
using MajMusic.Presentation.Storage;
using TKCode.Arc;

namespace MajMusic.Presentation.Views
{
    /// <summary>Builds object-specific Arc menus without redirecting actions through unrelated global selections.</summary>
    internal static class MusicContextMenus
    {
        /// <summary>Keeps at most one live menu per attached interaction target.</summary>
        private static readonly ConditionalWeakTable<Control, MenuState> Menus = new();

        /// <summary>Gets a target's open menu for native input and accessibility verification.</summary>
        /// <param name="target">The music card or playlist list that opened the menu.</param>
        /// <returns>The current menu, or null after it closes.</returns>
        internal static MenuSurface? GetOpenMenu(Control target)
        {
            return Menus.TryGetValue(target, out var state) && state.Menu is { IsOpen: true } menu ? menu : null;
        }

        /// <summary>Closes a recycled or detached target's menu before it can retain another row's data.</summary>
        /// <param name="target">The changed interaction target.</param>
        internal static void Close(Control target)
        {
            if (Menus.TryGetValue(target, out var state))
            {
                state.Menu?.Close(returnFocus: false);
                state.Menu = null;
            }
        }

        /// <summary>Opens music actions against the descriptor captured by this exact gesture.</summary>
        /// <param name="target">The actual artwork card.</param>
        /// <param name="owner">The shared music presentation.</param>
        /// <param name="track">The song underneath the pointer or keyboard focus.</param>
        /// <param name="position">The optional pointer location relative to the target.</param>
        internal static void OpenTrack(Control target, MainViewModel owner, MusicTrack track, Point? position)
        {
            var menu = Surface("歌曲操作");
            menu.Items.Add(Action("播放", owner, () => owner.PlayTrackFromMenuAsync(track), () => owner.CanPlay, ArcIcons.Play));
            var add = new MenuEntry { Header = "加入歌单", Icon = LocalIcon(ArcIcons.Plus) };
            foreach (var playlist in owner.Playlists.Where(owner.CanEditPlaylistForMenu))
            {
                var captured = playlist;
                add.Items.Add(Action(MusicSelectLabels.Playlist(captured) ?? captured.Name, owner, () => owner.AddTrackToPlaylistFromMenuAsync(track, captured),
                    () => owner.CanEditPlaylistForMenu(captured)));
            }

            add.IsEnabled = add.Items.Count > 0;
            menu.Items.Add(add);
            menu.Items.Add(Action(owner.IsTrackFavoriteForMenu(track) ? "取消喜欢" : "喜欢", owner,
                () => owner.ToggleTrackFavoriteFromMenuAsync(track), () => owner.IsIdle && owner.HasFavorites, ArcIcons.Heart));
            menu.Items.Add(Action("下载", owner, () => owner.DownloadTrackFromMenuAsync(track),
                () => owner.OnlineEnabled && track.RemoteId is not null, ArcIcons.Download));
            var removeLocal = Action("删除本地文件", owner, () => owner.RemoveTrackMediaFromMenuAsync(track),
                () => owner.CanRemoveTrackMediaForMenu(track), ArcIcons.Trash2);
            removeLocal.HasSeparatorBefore = true;
            menu.Items.Add(removeLocal);
            if (owner.BrowsedPlaylistForMenu is { } browsed)
            {
                var remove = Action("从当前歌单移除", owner, () => owner.RemoveTrackFromPlaylistFromMenuAsync(track, browsed),
                    () => owner.CanRemoveTrackForMenu(track, browsed), ArcIcons.Minus);
                remove.HasSeparatorBefore = true;
                menu.Items.Add(remove);
            }

            Open(target, menu, position);
        }

        /// <summary>Opens list-management actions for the row underneath the request rather than the previous selection.</summary>
        /// <param name="list">The scrolling playlist menu.</param>
        /// <param name="owner">The shared music presentation.</param>
        /// <param name="source">The original routed input target.</param>
        /// <param name="position">The optional pointer location relative to the list.</param>
        /// <returns>Whether a real playlist row was found.</returns>
        internal static bool OpenPlaylist(ListBox list, MainViewModel owner, Control? source, Point? position)
        {
            if (position is { } point)
            {
                source = list.InputHitTest(point) as Control ?? source;
            }

            var item = source as ListBoxItem ?? source?.FindAncestorOfType<ListBoxItem>();
            var playlist = item?.DataContext as MusicPlaylist ?? (position is null ? list.SelectedItem as MusicPlaylist : null);
            if (playlist is null)
            {
                return false;
            }

            var captured = playlist;
            var menu = Surface("歌单操作");
            menu.Items.Add(Action("打开", owner, async () =>
            {
                OpenHome(list);
                await owner.OpenPlaylistFromMenuAsync(captured);
            }, () => owner.IsIdle, ArcIcons.FolderOpen));
            menu.Items.Add(Action("播放", owner, () => owner.PlayPlaylistFromMenuAsync(captured), () => owner.CanPlay, ArcIcons.Play));
            menu.Items.Add(Action("重命名", owner, () => RenameAsync(list, owner, captured),
                () => owner.CanRenamePlaylistForMenu(captured), ArcIcons.Pencil));
            menu.Items.Add(Action("导出", owner, () => ExportAsync(list, owner, captured),
                () => owner.CanExchangeCollections && TopLevel.GetTopLevel(list)?.StorageProvider.CanSave == true, ArcIcons.ArrowUpFromLine));
            var delete = Action("删除", owner, () => owner.DeletePlaylistFromMenuAsync(captured),
                () => owner.CanDeletePlaylistForMenu(captured), ArcIcons.Trash2);
            delete.HasSeparatorBefore = true;
            delete.IsDestructive = true;
            menu.Items.Add(delete);
            Open(list, menu, position ?? item?.TranslatePoint(new Point(12, item.Bounds.Height), list));
            return true;
        }

        /// <summary>Creates one observed menu action whose target remains captured while asynchronous UI changes occur.</summary>
        /// <param name="header">The concise menu label.</param>
        /// <param name="owner">The lifetime and error owner.</param>
        /// <param name="execute">The captured-target operation.</param>
        /// <param name="canExecute">The current capability check.</param>
        /// <param name="icon">The action glyph, or a playlist glyph for an insertion destination.</param>
        /// <returns>A native Arc menu row.</returns>
        private static MenuEntry Action(string header, MainViewModel owner, Func<Task> execute, Func<bool> canExecute, Geometry? icon = null)
        {
            return new MenuEntry { Header = header, Icon = LocalIcon(icon ?? ArcIcons.List), Command = new AsyncCommand(execute, owner.ReportError, canExecute) };
        }

        /// <summary>Gives each menu an icon owned by its own rendering dispatcher.</summary>
        /// <param name="icon">The shared Arc icon path.</param>
        /// <returns>An independent geometry for this menu.</returns>
        private static Geometry LocalIcon(Geometry icon)
        {
            return icon.Clone();
        }

        /// <summary>Creates a viewport-bounded native Arc context surface without wrapping its target's normal click.</summary>
        /// <param name="name">The accessible action-group name.</param>
        /// <returns>The empty context surface.</returns>
        private static MenuSurface Surface(string name)
        {
            var menu = new MenuSurface { Variant = MenuSurfaceVariant.Context, ClosesOnViewportChange = true };
            Avalonia.Automation.AutomationProperties.SetName(menu, name);
            return menu;
        }

        /// <summary>Replaces the preceding target menu and preserves its normal left-click interaction.</summary>
        /// <param name="target">The pointer or focus anchor.</param>
        /// <param name="menu">The newly captured menu.</param>
        /// <param name="position">The optional local pointer location.</param>
        private static void Open(Control target, MenuSurface menu, Point? position)
        {
            var state = Menus.GetValue(target, static control => new MenuState(control));
            state.Menu?.Close(returnFocus: false);
            state.Menu = menu;
            menu.Closed += (_, _) =>
            {
                if (ReferenceEquals(state.Menu, menu))
                {
                    state.Menu = null;
                }
            };
            if (position is { } point)
            {
                menu.OpenAt(target, point);
            }
            else
            {
                menu.Open(target, MenuOpenFocus.First);
            }
        }

        /// <summary>Returns to the existing browser before starting a captured playlist action.</summary>
        /// <param name="target">The playlist list in either responsive presentation.</param>
        private static void OpenHome(Control target)
        {
            if (target.FindAncestorOfType<MainView>() is { } main)
            {
                main.NavigateToSection(0);
            }
            else if (target.FindAncestorOfType<MobileMusicView>() is { } mobile)
            {
                mobile.SelectedSectionIndex = 0;
            }
        }

        /// <summary>Edits one captured local name in a temporary Arc form.</summary>
        /// <param name="target">The current UI anchor.</param>
        /// <param name="owner">The local playlist operation owner.</param>
        /// <param name="playlist">The playlist captured when its menu opened.</param>
        /// <returns>A task representing dismissal or the exact playlist rename.</returns>
        private static async Task RenameAsync(Control target, MainViewModel owner, MusicPlaylist playlist)
        {
            var input = new TKCode.Arc.Input { Name = "RenamePlaylistInput", Text = playlist.Name, Placeholder = "歌单名称", MinHeight = 44 };
            input.AttachedToVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                input.Focus();
                input.SelectAll();
            }, Avalonia.Threading.DispatcherPriority.Input);
            var save = new TKCode.Arc.Button
            {
                Name = "RenamePlaylistSave",
                Content = "保存",
                Variant = ButtonVariant.Primary,
                IsDefault = true,
                IsEnabled = !string.IsNullOrWhiteSpace(input.Text)
            };
            var cancel = new TKCode.Arc.Button { Content = "取消", IsCancel = true };
            TKCode.Arc.Dialog.SetResult(save, true);
            TKCode.Arc.Dialog.SetResult(cancel, false);
            input.TextChanged += (_, _) => save.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
            var dialog = new PlaylistRenameDialog
            {
                Name = "RenamePlaylistDialog",
                Title = "重命名歌单",
                Content = input,
                Actions = new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { cancel, save }
                }
            };
            var result = await dialog.ShowAsync(target);
            if (result is true)
            {
                await owner.RenamePlaylistFromMenuAsync(playlist, input.Text ?? string.Empty);
            }
        }

        /// <summary>Exports the captured playlist to an explicitly chosen new file.</summary>
        /// <param name="target">The current native file-picker owner.</param>
        /// <param name="owner">The Maj collection exporter.</param>
        /// <param name="playlist">The exact playlist whose menu action was selected.</param>
        /// <returns>A task representing cancellation or the selected playlist's export.</returns>
        private static async Task ExportAsync(Control target, MainViewModel owner, MusicPlaylist playlist)
        {
            if (TopLevel.GetTopLevel(target) is not { } topLevel)
            {
                return;
            }

            using var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出播放列表",
                SuggestedFileName = playlist.IsFavorite ? "MyFavorites.json" : "MajMusic-playlist.json",
                DefaultExtension = OperatingSystem.IsAndroid() ? null : "json",
                FileTypeChoices = [new FilePickerFileType("Maj JSON") { Patterns = ["*.json"], MimeTypes = ["application/json"] }]
            });
            if (file is not null)
            {
                await StoragePickerBridge.ExportAsync(owner, file, playlist);
            }
        }

        /// <summary>Closes a viewport-owned popup as soon as its interaction target is detached.</summary>
        private sealed class MenuState
        {
            /// <summary>Gets or sets the target's only current context surface.</summary>
            public MenuSurface? Menu { get; set; }

            /// <summary>Registers cleanup independently of the target's data-context lifetime.</summary>
            /// <param name="target">The card or list owning the popup.</param>
            public MenuState(Control target)
            {
                target.DetachedFromVisualTree += (_, _) =>
                {
                    Menu?.Close(returnFocus: false);
                    Menu = null;
                };
            }
        }

        /// <summary>Keeps the temporary rename dialog's close glyph on its owning rendering dispatcher.</summary>
        private sealed class PlaylistRenameDialog : TKCode.Arc.Dialog
        {
            /// <summary>Uses the standard Arc dialog theme for this object-specific editor.</summary>
            protected override Type StyleKeyOverride => typeof(TKCode.Arc.Dialog);

            /// <summary>Creates the themed dialog surface with an independently owned close glyph.</summary>
            /// <returns>The standard Arc surface with its normal header and close action.</returns>
            protected override ModalSurface CreateSurface()
            {
                var surface = base.CreateSurface();
                surface.TemplateApplied += (_, _) =>
                {
                    if (surface.CloseButton is { } close)
                    {
                        close.ApplyTemplate();
                        foreach (var glyph in close.GetVisualDescendants().OfType<ArcGlyph>())
                        {
                            glyph.Data = Geometry.Parse("M18 6 6 18 M6 6l12 12");
                        }
                    }
                };
                return surface;
            }
        }
    }
}
