#nullable enable

using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.ExceptionServices;
using Avalonia.Headless;
using Avalonia.Platform.Storage;
using MajMusic.Core.Abstractions;
using MajMusic.Core.Models;
using MajMusic.Core.Playback;
using MajMusic.Infrastructure.Application;
using MajMusic.Presentation.Storage;
using MajMusic.Presentation.ViewModels;
using SkiaSharp;

namespace MajMusic.Tests
{
    /// <summary>Checks provider-only local music and Maj JSON using opaque content URIs, without Android hardware or copying audio.</summary>
    internal static class DocumentStorageTests
    {
        /// <summary>Runs a real workspace against in-memory read-only document trees and selected output streams.</summary>
        /// <returns>A task representing document-tree, offline and JSON interoperability assertions.</returns>
        public static async Task RunAsync()
        {
            await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApplication));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await session.Dispatch<bool>(async () =>
            {
                var root = Path.Combine(Path.GetTempPath(), "MajMusic-documents-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    await ValidateDocumentsAsync(root);
                    return true;
                }
                finally
                {
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                    var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (!Path.GetFullPath(root).StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("MajMusic-documents-", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("Refusing to clean outside the generated document fixture.");
                    }

                    Directory.Delete(root, true);
                }
            }, timeout.Token);
        }

        /// <summary>Indexes nested standard charts, resolves original audio, reuses private artwork and exchanges compatible collection JSON.</summary>
        /// <param name="root">The isolated application directory.</param>
        /// <returns>A task representing completed provider-only integration assertions.</returns>
        private static async Task ValidateDocumentsAsync(string root)
        {
            var provider = new FakeProvider();
            var tree = provider.Folder("tree", "My charts");
            var nested = provider.Folder("nested", "Numeric 123", tree);
            var chartBytes = Encoding.UTF8.GetBytes("&title=从谱面读取的歌名\n&artist=文档作者\n&inote_1=(120){4}1,2,3,4,E\n");
            provider.File("chart-opaque", "maidata.txt", chartBytes, nested);
            var audio = provider.File("audio-opaque", "track.mp3", "large original music bytes"u8.ToArray(), nested);
            using var pixels = new SKBitmap(32, 32);
            pixels.Erase(SKColors.CornflowerBlue);
            using var png = pixels.Encode(SKEncodedImageFormat.Png, 100);
            var cover = provider.File("cover-opaque", "bg.png", png.ToArray(), nested);
            var hash = Convert.ToBase64String(MD5.HashData(chartBytes));
            var favorites = provider.File("favorites-opaque", "MyFavorites.json",
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Name = "Portable favorites", SongHashs = new[] { hash }, IsPlayList = true, Extension = "retained" })), tree);
            var indexes = provider.Folder("indexes", "majcollection", tree);
            provider.File("playlist-opaque", "Set.json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { Name = "Provider playlist", SongHashs = new[] { hash, "unresolved" } })), indexes);
            var documents = new AvaloniaDocumentStorage(() => provider.Storage, Path.Combine(root, "DocumentCovers"));
            var resolvedByHost = false;
            var hostReference = await StoragePickerBridge.GetDirectoryReferenceAsync(tree.Storage, folder =>
            {
                resolvedByHost = true;
                return Task.FromResult(folder.Path.AbsoluteUri);
            });
            TestAssert.True(resolvedByHost && hostReference == tree.Path.AbsoluteUri && !tree.BookmarkSaved,
                "An explicit host resolver must obtain a read-only directory grant without invoking the default read/write bookmark request.");
            var reference = await StoragePickerBridge.GetDirectoryReferenceAsync(tree.Storage);
            TestAssert.True(reference == tree.Path.AbsoluteUri && tree.BookmarkSaved, "A selected provider tree must preserve its grant and original opaque URI.");
            TestAssert.Equal(reference, LocalDirectorySettings.NormalizePath(reference), "Content roots must not pass through filesystem canonicalization.");
            var data = Path.Combine(root, "data");
            using var workspace = new MusicWorkspace(data, credentialStore: new UnusedCredentials(), documentStorage: documents);
            await workspace.LoadSettingsAsync();
            await workspace.SaveSettingsAsync(new AppSettings { LocalDirectorySources = [new LocalDirectorySource { Id = "saf", Name = tree.Name, Path = reference }] });
            var result = await workspace.IndexDirectoryAsync(reference);
            var track = result.Tracks.Single();
            TestAssert.True(result.ScannedDirectoryCount == 3 && track.Title == "从谱面读取的歌名" && track.Artist == "文档作者" && track.MajChartHash == hash,
                "Nested document charts must use canonical TXT title, artist and original-byte Maj identity.");
            TestAssert.True(track.Audio.LocalPath == audio.Path.AbsoluteUri && audio.ReadBytes == 0,
                "Indexing references original content audio without reading or copying its bytes.");
            TestAssert.Equal(audio.Path.AbsoluteUri, await workspace.ResolveAudioAsync(track, false), "Native playback receives the original granted audio URI.");
            TestAssert.Equal(0L, audio.ReadBytes, "Validating playback access cannot copy the original music into private storage.");
            var cachedCover = await workspace.ResolveCoverAsync(track);
            TestAssert.True(cachedCover is not null && File.ReadAllBytes(cachedCover).SequenceEqual(cover.Bytes),
                "Only requested artwork is copied to the private image cache for ordinary decoders.");
            provider.Available = false;
            TestAssert.Equal(cachedCover, await workspace.ResolveCoverAsync(track), "An existing private cover remains usable without reopening a temporarily unavailable provider.");
            provider.Available = true;
            await using (var covers = new CoverThumbnailCache(workspace, CancellationToken.None))
            using (var card = new SongCardViewModel(track with { RemoteId = "legacy-document-reference" }, covers))
            {
                card.RestoreDownloadState(new SongDownloadState { IsAvailable = true, LocalPath = track.Audio.LocalPath });
                await card.ActivateAsync();
                TestAssert.True(card.Cover is not null && !card.HasDownloadStatus,
                    "Provider music must load its real cover independently of filesystem-only online-download availability and never claim to be a managed download.");
            }

            var exchange = new MajCollectionExchange(workspace, documents);
            var lists = await exchange.ImportIndexDirectoryAsync(reference);
            TestAssert.True(lists.Playlists.Count == 2 && lists.Playlists.Any(list => list.IsFavorite)
                && lists.Playlists.Single(list => !list.IsFavorite).MajCollection!.Entries.Count == 2,
                "Provider JSON indexes must retain favorites recognition and unresolved Maj references.");
            var favorite = await exchange.ImportFileAsync(favorites.Path.AbsoluteUri);
            TestAssert.True(favorite.IsFavorite && favorite.TrackIds.SequenceEqual([track.Id])
                && favorite.MajCollection!.AdditionalProperties.ContainsKey("Extension"),
                "Opaque selected document IDs must use the provider's MyFavorites.json display name, never a guessed URI filename.");
            await using (var model = new MainViewModel(workspace, new PlaybackService(new UnavailablePlaybackBackend()), static action => action(), collections: exchange))
            {
                await model.InitializeAsync();
                model.SelectedPlaylist = model.Playlists.Single(list => list.IsFavorite);
                var output = provider.File("new-export", "MyFavorites.json", [], writable: true);
                await StoragePickerBridge.ExportAsync(model, output.Storage);
                using var exported = JsonDocument.Parse(output.Bytes);
                TestAssert.True(exported.RootElement.GetProperty("SongHashs").EnumerateArray().Select(value => value.GetString()).SequenceEqual([hash])
                    && exported.RootElement.GetProperty("Extension").GetString() == "retained",
                    "An explicitly chosen provider output stream must receive compatible complete JSON with retained extension fields.");
            }

            TestAssert.True(provider.Items.Values.OfType<FakeFile>().Where(file => !file.Writable).All(file => file.WriteCount == 0)
                && !Directory.EnumerateFiles(data, "*.mp3", SearchOption.AllDirectories).Any(),
                "Music roots and imported JSON remain read-only and no audio copy can appear in private storage.");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await TestAssert.ThrowsAsync<OperationCanceledException>(() => documents.ScanAsync(reference, canceled.Token));
        }

        /// <summary>Provides only the in-memory documents explicitly created by this fixture.</summary>
        private sealed class FakeProvider
        {
            /// <summary>Gets a runtime proxy supporting Avalonia's intentionally non-C#-implementable private marker.</summary>
            public IStorageProvider Storage => StorageProxy.Wrap<IStorageProvider>(this);
            /// <summary>Retains provider objects by their opaque URI.</summary>
            public Dictionary<Uri, FakeItem> Items { get; } = new();
            /// <summary>Controls temporary provider availability.</summary>
            public bool Available { get; set; } = true;
            /// <inheritdoc />
            public bool CanOpen => true;
            /// <inheritdoc />
            public bool CanSave => true;
            /// <inheritdoc />
            public bool CanPickFolder => true;
            /// <summary>Creates a nested fixture folder.</summary>
            /// <param name="id">The opaque provider ID.</param>
            /// <param name="name">The actual display name.</param>
            /// <param name="parent">The optional containing folder.</param>
            /// <returns>The created provider folder.</returns>
            public FakeFolder Folder(string id, string name, FakeFolder? parent = null)
            {
                var folder = new FakeFolder(id, name, parent);
                Items.Add(folder.Path, folder);
                parent?.Children.Add(folder);
                return folder;
            }
            /// <summary>Creates a source or explicitly chosen output document.</summary>
            /// <param name="id">The opaque provider ID.</param>
            /// <param name="name">The actual display name.</param>
            /// <param name="bytes">The document bytes.</param>
            /// <param name="parent">The optional source folder.</param>
            /// <param name="writable">Whether this is the separately selected output.</param>
            /// <returns>The created provider document.</returns>
            public FakeFile File(string id, string name, byte[] bytes, FakeFolder? parent = null, bool writable = false)
            {
                var file = new FakeFile(id, name, parent, bytes, writable);
                Items.Add(file.Path, file);
                parent?.Children.Add(file);
                return file;
            }
            /// <inheritdoc />
            public Task<IStorageFile?> TryGetFileFromPathAsync(Uri path) => Task.FromResult(Available && Items.GetValueOrDefault(path) is FakeFile file ? file.Storage : null);
            /// <inheritdoc />
            public Task<IStorageFolder?> TryGetFolderFromPathAsync(Uri path) => Task.FromResult(Available && Items.GetValueOrDefault(path) is FakeFolder folder ? folder.Storage : null);
            /// <inheritdoc />
            public Task<IReadOnlyList<IStorageFile>> OpenFilePickerAsync(FilePickerOpenOptions options) => throw new NotSupportedException();
            /// <inheritdoc />
            public Task<OpenFilePickerResult> OpenFilePickerWithResultAsync(FilePickerOpenOptions options) => throw new NotSupportedException();
            /// <inheritdoc />
            public Task<IStorageFile?> SaveFilePickerAsync(FilePickerSaveOptions options) => throw new NotSupportedException();
            /// <inheritdoc />
            public Task<SaveFilePickerResult> SaveFilePickerWithResultAsync(FilePickerSaveOptions options) => throw new NotSupportedException();
            /// <inheritdoc />
            public Task<IReadOnlyList<IStorageFolder>> OpenFolderPickerAsync(FolderPickerOpenOptions options) => throw new NotSupportedException();
            /// <inheritdoc />
            public Task<IStorageBookmarkFile?> OpenFileBookmarkAsync(string bookmark) => Task.FromResult<IStorageBookmarkFile?>(null);
            /// <inheritdoc />
            public Task<IStorageBookmarkFolder?> OpenFolderBookmarkAsync(string bookmark) => Task.FromResult<IStorageBookmarkFolder?>(null);
            /// <inheritdoc />
            public Task<IStorageFolder?> TryGetWellKnownFolderAsync(WellKnownFolder folder) => Task.FromResult<IStorageFolder?>(null);
        }

        /// <summary>Implements nonmutating provider metadata and explicitly fails any source modification.</summary>
        private abstract class FakeItem(string id, string name, FakeFolder? parent)
        {
            /// <summary>Gets this original item's provider interface.</summary>
            public abstract IStorageItem Storage { get; }
            /// <inheritdoc />
            public string Name => name;
            /// <inheritdoc />
            public Uri Path { get; } = new("content://fixture.documents/document/" + id);
            /// <inheritdoc />
            public bool CanBookmark => true;
            /// <summary>Records grant persistence by the native picker boundary.</summary>
            public bool BookmarkSaved { get; private set; }
            /// <inheritdoc />
            public virtual Task<StorageItemProperties> GetBasicPropertiesAsync() => Task.FromResult(new StorageItemProperties(null, null, DateTimeOffset.UnixEpoch));
            /// <inheritdoc />
            public Task<string?> SaveBookmarkAsync()
            {
                BookmarkSaved = true;
                return Task.FromResult<string?>(Path.AbsoluteUri);
            }
            /// <inheritdoc />
            public Task<IStorageFolder?> GetParentAsync() => Task.FromResult(parent?.Storage);
            /// <inheritdoc />
            public Task DeleteAsync() => throw new InvalidOperationException("Source document deletion is forbidden.");
            /// <inheritdoc />
            public Task<IStorageItem?> MoveAsync(IStorageFolder destination) => throw new InvalidOperationException("Source document moves are forbidden.");
            /// <inheritdoc />
            public void Dispose()
            {
            }
        }

        /// <summary>Enumerates direct provider children without touching a physical filesystem.</summary>
        private sealed class FakeFolder(string id, string name, FakeFolder? parent) : FakeItem(id, name, parent)
        {
            /// <inheritdoc />
            public override IStorageFolder Storage => StorageProxy.Wrap<IStorageFolder>(this);
            /// <summary>Contains the direct fixture children.</summary>
            public List<FakeItem> Children { get; } = new();
            /// <inheritdoc />
            public async IAsyncEnumerable<IStorageItem> GetItemsAsync()
            {
                foreach (var child in Children)
                {
                    yield return child.Storage;
                }

                await Task.CompletedTask;
            }
            /// <inheritdoc />
            public Task<IStorageFolder?> GetFolderAsync(string name) => Task.FromResult(Children.OfType<FakeFolder>().FirstOrDefault(item => item.Name == name)?.Storage);
            /// <inheritdoc />
            public Task<IStorageFile?> GetFileAsync(string name) => Task.FromResult(Children.OfType<FakeFile>().FirstOrDefault(item => item.Name == name)?.Storage);
            /// <inheritdoc />
            public Task<IStorageFile?> CreateFileAsync(string name) => throw new InvalidOperationException("Source document creation is forbidden.");
            /// <inheritdoc />
            public Task<IStorageFolder?> CreateFolderAsync(string name) => throw new InvalidOperationException("Source directory creation is forbidden.");
        }

        /// <summary>Counts original stream reads and forbids writes except on the explicitly selected export destination.</summary>
        private sealed class FakeFile(string id, string name, FakeFolder? parent, byte[] bytes, bool writable) : FakeItem(id, name, parent)
        {
            /// <inheritdoc />
            public override IStorageFile Storage => StorageProxy.Wrap<IStorageFile>(this);
            /// <summary>Stores the original or exported bytes.</summary>
            public byte[] Bytes { get; private set; } = bytes;
            /// <summary>Identifies the separately chosen export document.</summary>
            public bool Writable => writable;
            /// <summary>Counts source bytes actually read, independently of access validation.</summary>
            public long ReadBytes { get; private set; }
            /// <summary>Counts any requested provider write streams.</summary>
            public int WriteCount { get; private set; }
            /// <inheritdoc />
            public override Task<StorageItemProperties> GetBasicPropertiesAsync() => Task.FromResult(new StorageItemProperties((ulong)Bytes.Length, null, DateTimeOffset.UnixEpoch));
            /// <inheritdoc />
            public Task<Stream> OpenReadAsync() => Task.FromResult<Stream>(new ObservedStream(Bytes, count => ReadBytes += count));
            /// <inheritdoc />
            public Task<Stream> OpenWriteAsync()
            {
                WriteCount++;
                if (!writable)
                {
                    throw new InvalidOperationException("Source document writes are forbidden.");
                }

                return Task.FromResult<Stream>(new ExportStream(value => Bytes = value));
            }
        }

        /// <summary>Counts real reads while keeping source bytes immutable.</summary>
        private sealed class ObservedStream(byte[] bytes, Action<int> read) : MemoryStream(bytes, false)
        {
            /// <inheritdoc />
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var count = await base.ReadAsync(buffer, cancellationToken);
                read(count);
                return count;
            }
        }

        /// <summary>Publishes only the selected output's bytes after stream completion.</summary>
        private sealed class ExportStream(Action<byte[]> saved) : MemoryStream
        {
            /// <inheritdoc />
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    saved(ToArray());
                }

                base.Dispose(disposing);
            }
        }

        /// <summary>Prevents all access to the operating-system credential store.</summary>
        private sealed class UnusedCredentials : ICredentialStore
        {
            /// <inheritdoc />
            public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Document fixtures cannot access credentials.");
            /// <inheritdoc />
            public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Document fixtures cannot access credentials.");
            /// <inheritdoc />
            public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Document fixtures cannot access credentials.");
        }

        /// <summary>Supplies runtime-only test implementations for Avalonia interfaces with private NonImplementable marker members.</summary>
        private static class StorageProxy
        {
            /// <summary>Retains only generated proxy types, never source documents or streams.</summary>
            private static readonly Dictionary<Type, Type> Types = new();
            /// <summary>Owns the finite generated interface proxies in this test process.</summary>
            private static readonly ModuleBuilder Module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("MajMusic.StorageTestProxies"), AssemblyBuilderAccess.Run)
                .DefineDynamicModule("Storage");

            /// <summary>Creates a forwarding interface around a plain controllable source fixture.</summary>
            /// <typeparam name="T">The platform storage interface.</typeparam>
            /// <param name="target">The plain fixture carrying matching public methods.</param>
            /// <returns>The runtime interface implementation.</returns>
            public static T Wrap<T>(object target) where T : class
            {
                Type type;
                lock (Types)
                {
                    if (!Types.TryGetValue(typeof(T), out type!))
                    {
                        type = Create(typeof(T));
                        Types.Add(typeof(T), type);
                    }
                }

                Func<string, object?[], object?> invoke = (name, arguments) =>
                {
                    try
                    {
                        return target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public)!.Invoke(target, arguments);
                    }
                    catch (TargetInvocationException exception) when (exception.InnerException is not null)
                    {
                        ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                        throw;
                    }
                };
                return (T)Activator.CreateInstance(type, invoke)!;
            }

            /// <summary>Emits public forwarding calls and no-op private platform marker implementations.</summary>
            /// <param name="contract">The selected storage interface.</param>
            /// <returns>The completed runtime proxy type.</returns>
            private static Type Create(Type contract)
            {
                var builder = Module.DefineType("StorageProxy" + Types.Count, TypeAttributes.Public | TypeAttributes.Sealed);
                var invokeType = typeof(Func<string, object?[], object?>);
                var field = builder.DefineField("_invoke", invokeType, FieldAttributes.Private | FieldAttributes.InitOnly);
                var constructor = builder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [invokeType]);
                var initialize = constructor.GetILGenerator();
                initialize.Emit(OpCodes.Ldarg_0);
                initialize.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
                initialize.Emit(OpCodes.Ldarg_0);
                initialize.Emit(OpCodes.Ldarg_1);
                initialize.Emit(OpCodes.Stfld, field);
                initialize.Emit(OpCodes.Ret);
                foreach (var current in contract.GetInterfaces().Append(contract).Distinct())
                {
                    builder.AddInterfaceImplementation(current);
                    foreach (var method in current.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        var parameters = method.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
                        var implementation = builder.DefineMethod(current.Name + "." + method.Name,
                            MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot | MethodAttributes.HideBySig,
                            method.ReturnType, parameters);
                        var il = implementation.GetILGenerator();
                        if (method.IsPublic)
                        {
                            il.Emit(OpCodes.Ldarg_0);
                            il.Emit(OpCodes.Ldfld, field);
                            il.Emit(OpCodes.Ldstr, method.Name);
                            il.Emit(OpCodes.Ldc_I4, parameters.Length);
                            il.Emit(OpCodes.Newarr, typeof(object));
                            for (var index = 0; index < parameters.Length; index++)
                            {
                                il.Emit(OpCodes.Dup);
                                il.Emit(OpCodes.Ldc_I4, index);
                                il.Emit(OpCodes.Ldarg, index + 1);
                                if (parameters[index].IsValueType)
                                {
                                    il.Emit(OpCodes.Box, parameters[index]);
                                }
                                il.Emit(OpCodes.Stelem_Ref);
                            }

                            il.Emit(OpCodes.Callvirt, invokeType.GetMethod("Invoke")!);
                            if (method.ReturnType == typeof(void))
                            {
                                il.Emit(OpCodes.Pop);
                            }
                            else
                            {
                                il.Emit(method.ReturnType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, method.ReturnType);
                            }
                        }
                        else if (method.ReturnType != typeof(void))
                        {
                            il.Emit(OpCodes.Ldnull);
                        }

                        il.Emit(OpCodes.Ret);
                        builder.DefineMethodOverride(implementation, method);
                    }
                }

                return builder.CreateType()!;
            }
        }
    }
}
