﻿using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.IO;
using System.Linq;
using System.Linq.Dynamic;
using System.Threading.Tasks;
using SmartStore.Core;
using SmartStore.Core.Data;
using SmartStore.Core.Domain.Media;
using SmartStore.Core.Events;
using SmartStore.Core.Logging;
using SmartStore.Services.Media.Storage;
using SmartStore.Core.IO;
using System.Drawing;
using System.Runtime.CompilerServices;

namespace SmartStore.Services.Media
{
    public partial class MediaService : IMediaService
    {
        private readonly IRepository<MediaFile> _fileRepo;
        private readonly IFolderService _folderService;
        private readonly IMediaSearcher _searcher;
        private readonly IMediaTypeResolver _typeResolver;
        private readonly IMediaUrlGenerator _urlGenerator;
        private readonly IEventPublisher _eventPublisher;
        private readonly MediaSettings _mediaSettings;
        private readonly IImageProcessor _imageProcessor;
        private readonly IImageCache _imageCache;
        private readonly IMediaStorageProvider _storageProvider;
        private readonly MediaHelper _helper;

        public MediaService(
            IRepository<MediaFile> fileRepo,
            IAlbumRegistry albumRegistry,
            IFolderService folderService,
            IMediaSearcher searcher,
            IMediaTypeResolver typeResolver,
            IMediaUrlGenerator urlGenerator,
            IEventPublisher eventPublisher,
            MediaSettings mediaSettings,
            IImageProcessor imageProcessor,
            IImageCache imageCache,
            IMediaStorageProvider storageProvider,
            MediaHelper helper)
        {
            _fileRepo = fileRepo;
            _folderService = folderService;
            _searcher = searcher;
            _typeResolver = typeResolver;
            _urlGenerator = urlGenerator;
            _eventPublisher = eventPublisher;
            _mediaSettings = mediaSettings;
            _imageProcessor = imageProcessor;
            _imageCache = imageCache;
            _storageProvider = storageProvider;
            _helper = helper;
        }

        public ILogger Logger { get; set; } = NullLogger.Instance;

        public IMediaStorageProvider StorageProvider
        {
            get => _storageProvider;
        }

        #region Query

        public int CountFiles(MediaSearchQuery query)
        {
            var q = _searcher.PrepareQuery(query, MediaLoadFlags.None);
            var count = q.Count();
            return count;
        }

        public Task<int> CountFilesAsync(MediaSearchQuery query)
        {
            var q = _searcher.PrepareQuery(query, MediaLoadFlags.None);
            return q.CountAsync();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MediaSearchResult SearchFiles(MediaSearchQuery query, MediaLoadFlags flags = MediaLoadFlags.AsNoTracking)
        {
            var files = _searcher.SearchFiles(query, flags);
            return new MediaSearchResult(files.Load(), ConvertMediaFile);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public async Task<MediaSearchResult> SearchFilesAsync(MediaSearchQuery query, MediaLoadFlags flags = MediaLoadFlags.AsNoTracking)
        {
            var files = _searcher.SearchFiles(query, flags);
            return new MediaSearchResult(await files.LoadAsync(), ConvertMediaFile);
        }

        #endregion

        #region Read

        public bool FileExists(string path)
        {
            Guard.NotEmpty(path, nameof(path));

            if (_helper.TokenizePath(path, out var tokens))
            {
                return _fileRepo.Table.Any(x => x.FolderId == tokens.Folder.Id && x.Name == tokens.FileName);
            }
            
            return false;
        }

        public MediaFileInfo GetFileByPath(string path, MediaLoadFlags flags = MediaLoadFlags.None)
        {
            Guard.NotEmpty(path, nameof(path));

            if (_helper.TokenizePath(path, out var tokens))
            {
                var table = _fileRepo.Table;

                // TODO: (mm) LoadFlags > Blob | Tags | Tracks

                var entity = table.FirstOrDefault(x => x.FolderId == tokens.Folder.Id && x.Name == tokens.FileName);
                if (entity != null)
                {
                    EnsureMetadataResolved(entity, true);
                    return new MediaFileInfo(entity, _storageProvider, tokens.Folder.Path);
                }
            }

            return null;
        }

        public MediaFileInfo GetFileById(int id, MediaLoadFlags flags = MediaLoadFlags.None)
        {
            if (id <= 0)
                return null;

            var query = _fileRepo.Table.Where(x => x.Id == id);
            var entity = _searcher.ApplyLoadFlags(query, flags).FirstOrDefault();

            if (entity != null)
            {
                EnsureMetadataResolved(entity, true);
                var dir = _folderService.FindNode(entity)?.Value?.Path;
                return new MediaFileInfo(entity, _storageProvider, dir);
            }

            return null;
        }

        public MediaFileInfo GetFileByName(int folderId, string fileName, MediaLoadFlags flags = MediaLoadFlags.None)
        {
            Guard.IsPositive(folderId, nameof(folderId));
            Guard.NotEmpty(fileName, nameof(fileName));

            var query = _fileRepo.Table.Where(x => x.Name == fileName && x.FolderId == folderId);
            var entity = _searcher.ApplyLoadFlags(query, flags).FirstOrDefault();

            if (entity != null)
            {
                EnsureMetadataResolved(entity, true);
                var dir = _folderService.FindNode(entity)?.Value?.Path;
                return new MediaFileInfo(entity, _storageProvider, dir);
            }

            return null;
        }

        public IList<MediaFileInfo> GetFilesByIds(int[] ids, MediaLoadFlags flags = MediaLoadFlags.AsNoTracking)
        {
            Guard.NotNull(ids, nameof(ids));

            var query = _fileRepo.Table.Where(x => ids.Contains(x.Id));
            var result = _searcher.ApplyLoadFlags(query, flags).ToList();

            return result.OrderBySequence(ids).Select(ConvertMediaFile).ToList();
        }

        public bool CheckUniqueFileName(string path, out string newPath)
        {
            Guard.NotEmpty(path, nameof(path));

            // TODO: (mm) throw when path is not a file path

            newPath = null;

            if (!_helper.TokenizePath(path, out var pathData))
            {
                return false;
            }

            if (CheckUniqueFileName(pathData))
            {
                newPath = pathData.FullPath;
            }

            return newPath != null;
        }

        protected internal virtual bool CheckUniqueFileName(MediaPathData pathData)
        {
            // (perf) First make fast check
            var exists = _fileRepo.Table.Any(x => x.Name == pathData.FileName && x.FolderId == pathData.Folder.Id);
            if (!exists)
            {
                return false;
            }

            var q = new MediaSearchQuery
            {
                FolderId = pathData.Folder.Id,
                Term = string.Concat(pathData.FileTitle, "-*", pathData.Extension)
            };

            var query = _searcher.PrepareQuery(q, MediaLoadFlags.AsNoTracking).Select(x => x.Name);
            var files = new HashSet<string>(query.ToList(), StringComparer.OrdinalIgnoreCase);

            if (files.Count == 0)
            {
                return false;
            }

            var title = pathData.FileTitle;
            var ext = pathData.Extension;

            int i = 1;
            while (true)
            {
                var fileName = string.Concat(title, "-", i, ext);
                if (!files.Contains(fileName))
                {
                    // Found our gap
                    pathData.FileName = fileName;
                    return true;
                }

                i++;
            }
        }

        public byte[] FindEqualFile(byte[] fileBuffer, IEnumerable<MediaFile> files, out int equalFileId)
        {
            Guard.NotNull(fileBuffer, nameof(fileBuffer));
            Guard.NotNull(files, nameof(files));

            equalFileId = 0;

            var myStream = new MemoryStream(fileBuffer);

            try
            {
                foreach (var file in files)
                {
                    myStream.Seek(0, SeekOrigin.Begin);

                    using (var otherStream = _storageProvider.OpenRead(file))
                    {
                        if (myStream.ContentsEqual(otherStream))
                        {
                            equalFileId = file.Id;
                            return null;
                        }
                    }
                }

                return fileBuffer;
            }
            catch
            {
                return fileBuffer;
            }
            finally
            {
                myStream.Dispose();
            }
        }

        #endregion

        #region Create/Update/Delete

        //public MediaFileInfo CreateFile(string path)
        //{
        //    if (_mediaHelper.TokenizePath(path, out var tokens))
        //    {
        //        return SaveFile(
        //            null, // album
        //            new MediaFile { Name = tokens.FileName, FolderId = tokens.Folder.Id },
        //            null, // stream
        //            false);
        //    }

        //    // TODO: (mm) throw
        //    throw new Exception();
        //}

        public MediaFileInfo SaveFile(string path, Stream stream, bool isTransient = true, bool overwrite = false)
        {
            var pathData = CreatePathData(path);

            var file = _fileRepo.Table.FirstOrDefault(x => x.Name == pathData.FileName && x.FolderId == pathData.Folder.Id);
            stream = ProcessFile(ref file, pathData, stream, isTransient, overwrite);

            using (var scope = new DbContextScope(_fileRepo.Context, autoCommit: false))
            {
                if (file.Id == 0)
                {
                    _fileRepo.Insert(file);
                    scope.Commit();
                }

                _storageProvider.Save(file, stream);
                scope.Commit();
            }

            return ConvertMediaFile(file, pathData.Folder);
        }

        public async Task<MediaFileInfo> SaveFileAsync(string path, Stream stream, bool isTransient = true, bool overwrite = false)
        {
            var pathData = CreatePathData(path);

            var file = await _fileRepo.Table.FirstOrDefaultAsync(x => x.Name == pathData.FileName && x.FolderId == pathData.Folder.Id);
            stream = ProcessFile(ref file, pathData, stream, isTransient, overwrite);

            using (var scope = new DbContextScope(_fileRepo.Context, autoCommit: false))
            {
                if (file.Id == 0)
                {
                    _fileRepo.Insert(file);
                    await scope.CommitAsync();
                }

                await _storageProvider.SaveAsync(file, stream);
                await scope.CommitAsync();
            }

            return ConvertMediaFile(file, pathData.Folder);
        }

        public void DeleteFile(MediaFile file, bool permanent)
        {
            Guard.NotNull(file, nameof(file));

            if (!permanent)
            {
                file.Deleted = true;
                _fileRepo.Update(file);
            }
            else
            {
                _imageCache.Delete(file);

                // Delete from storage
                _storageProvider.Remove(file);

                // Delete entity
                _fileRepo.Delete(file);
            }
        }

        protected MediaPathData CreatePathData(string path)
        {
            Guard.NotEmpty(path, nameof(path));

            if (!_helper.TokenizePath(path, out var pathData))
            {
                throw new ArgumentException("Invalid path '{0}'. Valid path expression is: {albumName}[/subfolders]/{fileName}.{extension}".FormatInvariant(path), nameof(path));
            }

            if (pathData.Extension.IsEmpty())
            {
                throw new ArgumentException($"Cannot process files without file extension. Path: {path}", nameof(path));
            }

            return pathData;
        }

        protected Stream ProcessFile(ref MediaFile file, MediaPathData pathData, Stream inStream, bool isTransient = true, bool overwrite = false)
        {
            if (file != null && !overwrite)
            {
                throw new DuplicateMediaFileException(pathData.FullPath);
            }

            if (file != null)
            {
                ValidateMimeTypes("Save", file.MimeType, pathData.MimeType);
                _imageCache.Delete(file);
            }

            file = file ?? new MediaFile
            {
                IsTransient = isTransient,
                FolderId = pathData.Node.Value.Id
            };

            // TODO: (mm) Get pretty SeName

            file.Name = pathData.FileName;
            file.Extension = pathData.Extension;
            file.MimeType = pathData.MimeType;
            if (file.MediaType == null)
            {
                file.MediaType = _typeResolver.Resolve(pathData.Extension, pathData.MimeType);
            }

            // Process image
            if (inStream != null && inStream.Length > 0 && file.MediaType == MediaType.Image && ProcessImage(file, inStream, out var outStream, out var size))
            {
                inStream = outStream;
                file.Size = (int)outStream.Length;
                file.Width = size.Width;
                file.Height = size.Height;
                file.PixelSize = size.Width * size.Height;
            }
            else
            {
                file.RefreshMetadata(inStream);
            }  

            return inStream;
        }

        protected bool ProcessImage(MediaFile file, Stream inStream, out Stream outStream, out Size size)
        {
            outStream = null;
            size = Size.Empty;

            var originalSize = ImageHeader.GetDimensions(inStream, file.MimeType);

            if (!_imageProcessor.IsSupportedImage(file.Extension))
            {
                size = originalSize; // e.g.: image/svg+xml
                return true;
            }    

            var maxSize = _mediaSettings.MaximumImageSize;

            var query = new ProcessImageQuery(inStream)
            {
                Quality = _mediaSettings.DefaultImageQuality,
                Format = file.Extension,
                DisposeSource = true,
                IsValidationMode = true
            };

            if (originalSize.IsEmpty || (originalSize.Height <= maxSize && originalSize.Width <= maxSize))
            {
                // Give subscribers the chance to (pre)-process
                var evt = new ImageUploadValidatedEvent(query, originalSize);
                _eventPublisher.Publish(evt);

                if (evt.ResultStream != null)
                {
                    outStream = evt.ResultStream;
                    // Maybe subscriber forgot to set this, so check
                    size = evt.ResultSize.IsEmpty ? originalSize : evt.ResultSize;
                }
                else
                {
                    size = originalSize;
                }

                return true;
            }

            query.MaxWidth = maxSize;
            query.MaxHeight = maxSize;

            using (var result = _imageProcessor.ProcessImage(query, false))
            {
                size = new Size(result.Width, result.Height);
                outStream = result.OutputStream;
                return true;
            }
        }

        #endregion

        #region Copy/Move/Rename/Update etc.

        public MediaFileInfo CopyFile(MediaFile file, string destinationFileName, bool overwrite = false)
        {
            ValidateCopyOperation(file, destinationFileName, overwrite, out var existingFile, out var destPathData);

            var isOverwrite = existingFile != null;
            var newFile = existingFile ?? new MediaFile();

            // Simple clone
            MapMediaFile(file, newFile);

            // Set folder id
            newFile.FolderId = destPathData.Folder.Id;

            // Set name stuff
            if (!newFile.Name.IsCaseInsensitiveEqual(destPathData.FileName))
            {
                newFile.Name = destPathData.FileName;
                newFile.Extension = destPathData.Extension;
                newFile.MimeType = destPathData.MimeType;
            }

            // TODO: (mm) copy tags
            // TODO: (mm) Copy localized values (Alt, Title)

            // Save to DB
            if (isOverwrite)
            {
                _fileRepo.Update(newFile);
            }
            else
            {
                _fileRepo.Insert(newFile);
            }           

            // Save BLOB to storage.
            _storageProvider.Save(newFile, _storageProvider.OpenRead(file));

            return ConvertMediaFile(newFile, destPathData.Folder);
        }

        public MediaFileInfo MoveFile(MediaFile file, string destinationFileName)
        {
            if (ValidateMoveOperation(file, destinationFileName, out var nameChanged, out var destPathData))
            {
                file.FolderId = destPathData.Folder.Id;

                if (nameChanged)
                {
                    var title = destPathData.FileTitle.ToValidFileName();
                    var ext = destPathData.Extension.ToLower();

                    file.Name = title + "." + ext;

                    if (ext != file.Extension.ToLower())
                    {
                        _storageProvider.ChangeExtension(file, ext);
                        file.Extension = ext;
                    }

                    _imageCache.Delete(file);
                }

                _fileRepo.Update(file);
            }

            return ConvertMediaFile(file, destPathData.Folder);
        }

        private bool ValidateMoveOperation(MediaFile file, string destinationFileName, out bool nameChanged, out MediaPathData destPathData)
        {
            Guard.NotNull(file, nameof(file));
            Guard.NotEmpty(destinationFileName, nameof(destinationFileName));

            destPathData = CreateDestinationPathData(file, destinationFileName);

            var destFileName = destPathData.FileName;
            var destFolderId = destPathData.Folder.Id;
            var folderChanged = destFolderId != file.FolderId;
            nameChanged = !destFileName.IsCaseInsensitiveEqual(file.Name);

            if (file.FolderId.HasValue && folderChanged)
            {
                // When "Move" operation: ensure file stays in source album.
                ValidateAlbums("Move", destFolderId, file.FolderId.Value);
            }

            if (nameChanged)
            {
                // Ensure bot MIME types are equal
                ValidateMimeTypes("Move", file.MimeType, destPathData.MimeType);
            }

            // Check whether destination file exists
            var exists = _fileRepo.Table.Any(x => x.Name == destFileName && x.FolderId == destFolderId);
            if (exists)
            {
                throw new DuplicateMediaFileException(destPathData.FullPath);
            }

            return folderChanged || nameChanged;
        }

        private bool ValidateCopyOperation(MediaFile file, string destinationFileName, bool overwrite, out MediaFile existing, out MediaPathData destPathData)
        {
            Guard.NotNull(file, nameof(file));
            Guard.NotEmpty(destinationFileName, nameof(destinationFileName));

            existing = null;
            destPathData = CreateDestinationPathData(file, destinationFileName);

            var destFileName = destPathData.FileName;
            var destFolderId = destPathData.Folder.Id;

            if (file.FolderId == destPathData.Folder.Id)
            {
                CheckUniqueFileName(destPathData);
            }
            else
            {
                // Check whether destination file exists
                existing = _fileRepo.Table.FirstOrDefault(x => x.FolderId == destFolderId && x.Name == destFileName);
                if (existing != null && !overwrite)
                {
                    throw new DuplicateMediaFileException(destPathData.FullPath);
                }
            }

            return true;
        }

        #endregion

        #region URL generation

        public string GetUrl(
            MediaFileInfo file,
            ProcessImageQuery imageQuery,
            string host = null,
            FallbackPictureType fallbackType = FallbackPictureType.Entity)
        {
            return _urlGenerator.GenerateUrl(file, imageQuery, host, fallbackType);
        }

        #endregion

        #region Utils

        protected MediaFileInfo ConvertMediaFile(MediaFile file)
        {
            var folder = _folderService.FindNode(file)?.Value;
            return new MediaFileInfo(file, _storageProvider, folder?.Path);
        }

        protected MediaFileInfo ConvertMediaFile(MediaFile file, MediaFolderNode folder)
        {
            return new MediaFileInfo(file, _storageProvider, folder?.Path);
        }

        private void EnsureMetadataResolved(MediaFile file, bool saveOnResolve)
        {
            var mediaType = _typeResolver.Resolve(file.Extension, file.MimeType);

            var resolveDimensions = mediaType == MediaType.Image && file.Width == null && file.Height == null;
            var resolveSize = file.Size <= 0;

            Stream stream = null;

            if (resolveDimensions || resolveSize)
            {
                stream = _storageProvider.OpenRead(file);
            }

            // Resolve image dimensions
            if (stream != null)
            {
                try
                {
                    if (resolveSize)
                    {
                        file.Size = (int)stream.Length;
                    }

                    if (resolveDimensions)
                    {
                        var size = ImageHeader.GetDimensions(stream, file.MimeType, true);
                        file.Width = size.Width;
                        file.Height = size.Height;
                        file.PixelSize = size.Width * size.Height;
                    }

                    if (saveOnResolve)
                    {
                        try
                        {
                            _fileRepo.Update(file);
                        }
                        catch (InvalidOperationException ioe)
                        {
                            // Ignore exception for pictures that already have been processed.
                            if (!ioe.IsAlreadyAttachedEntityException())
                            {
                                throw;
                            }
                        }
                    }
                }
                finally
                {
                    stream.Dispose();
                }
            }
        }

        private static void MapMediaFile(MediaFile from, MediaFile to)
        {
            to.Alt = from.Alt;
            to.Deleted = from.Deleted;
            to.Extension = from.Extension;
            to.FolderId = from.FolderId;
            to.Height = from.Height;
            to.Hidden = from.Hidden;
            to.IsTransient = from.IsTransient; // TBD: (mm) really?
            to.MediaType = from.MediaType;
            to.Metadata = from.Metadata;
            to.MimeType = from.MimeType;
            to.Name = from.Name;
            to.PixelSize = from.PixelSize;
            to.Size = from.Size;
            to.Title = from.Title;
            to.Version = from.Version;
            to.Width = from.Width;
        }

        private MediaPathData CreateDestinationPathData(MediaFile file, string destinationFileName)
        {
            if (!_helper.TokenizePath(destinationFileName, out var pathData))
            {
                // Passed path is NOT a path, but a file name

                if (IsPath(destinationFileName))
                {
                    // ...but file name includes path chars, which is not allowed
                    throw new ArgumentException("Invalid path '{0}'.".FormatInvariant(Path.GetDirectoryName(destinationFileName)), nameof(destinationFileName));
                }

                if (file.FolderId == null)
                {
                    throw new NotSupportedException("Cannot operate on files without folder assignment.");
                }

                pathData = new MediaPathData(_folderService.GetNodeById(file.FolderId.Value), destinationFileName);
            }

            return pathData;
        }

        private static void ValidateMimeTypes(string operation, string mime1, string mime2)
        {
            if (!mime1.Equals(mime2, StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException($"The file operation '{operation}' does not allow MIME type switching. Source mime: ${mime1}, target mime: ${mime2}.");
            }
        }

        private void ValidateAlbums(string operation, int folderId1, int folderId2)
        {
            if (!_folderService.AreInSameAlbum(folderId1, folderId2))
            {
                throw new NotSameAlbumException(
                    _folderService.GetNodeById(folderId1).Value.Path, 
                    _folderService.GetNodeById(folderId2).Value.Path);
            }
        }

        private static bool IsPath(string path)
        {
            return path.IndexOfAny(new[] { '/', '\\' }) > -1;
        }

        #endregion
    }
}
