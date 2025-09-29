namespace SkillEye
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Security.Cryptography;
    using GameHelper;

    /// <summary>
    ///     Loads, caches and unloads icon textures via GameHelper's overlay system.
    ///     The loader is key-based: a texture key is typically a filename (e.g., "fireball.png").
    /// </summary>
    public sealed class TextureLoader : IDisposable
    {
        private sealed record TextureInfo(IntPtr Handle, int Width, int Height, string FullPath);

        private readonly Dictionary<string, TextureInfo> _loadedTexturesByKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _lastDesiredKeys = new(StringComparer.OrdinalIgnoreCase);
        private string[] _lastSearchDirectories = Array.Empty<string>();

        private readonly string _cacheDirectory;
        private readonly bool _deleteCacheOnProcessExit;
        private static readonly HashSet<string> _registeredCacheDirs = new(StringComparer.OrdinalIgnoreCase);
        private static bool _exitHookAttached;
        private static Dictionary<string, string> _embeddedIndexByFileName;

        public TextureLoader(string cacheDirectory = null, bool deleteCacheOnProcessExit = true)
        {
            _cacheDirectory = cacheDirectory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache", "skillicons");
            _deleteCacheOnProcessExit = deleteCacheOnProcessExit;
            try { Directory.CreateDirectory(_cacheDirectory); } catch { /* ignore IO failures */ }

            // Register this cache dir for deletion on process exit (optional; default = true)
            if (_deleteCacheOnProcessExit)
                RegisterCacheDirectoryForExitCleanup(_cacheDirectory);
        }

        private static void RegisterCacheDirectoryForExitCleanup(string directoryPath)
        {
            lock (_registeredCacheDirs)
            {
                _registeredCacheDirs.Add(directoryPath);
                if (!_exitHookAttached)
                {
                    _exitHookAttached = true;
                    AppDomain.CurrentDomain.ProcessExit += (_, __) => TryDeleteRegisteredCaches();
                    AppDomain.CurrentDomain.DomainUnload += (_, __) => TryDeleteRegisteredCaches();
                }
            }
        }

        private static void TryDeleteRegisteredCaches()
        {
            lock (_registeredCacheDirs)
            {
                foreach (var dir in _registeredCacheDirs)
                {
                    TryDeleteCacheDir(dir);
                }
                _registeredCacheDirs.Clear();
            }
        }

        private static void TryDeleteCacheDir(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
                {
                    try { File.Delete(file); } catch { /* ignore per-file IO errors */ }
                }
                foreach (var sub in Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly))
                {
                    try { Directory.Delete(sub, recursive: true); } catch { /* ignore */ }
                }
                try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
            }
            catch { /* ignore top-level IO errors */ }
        }

        private static Dictionary<string, string> GetEmbeddedIndex()
        {
            if (_embeddedIndexByFileName != null) return _embeddedIndexByFileName;
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var asm = typeof(TextureLoader).Assembly;
            foreach (var resourceName in asm.GetManifestResourceNames())
            {
                if (resourceName.IndexOf(".Resources.Skillicons.", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var fileName = resourceName[(resourceName.LastIndexOf(".Resources.Skillicons.", StringComparison.OrdinalIgnoreCase)
                        + ".Resources.Skillicons.".Length)..];
                    index[fileName] = resourceName;
                }
            }
            _embeddedIndexByFileName = index;
            return index;
        }

        private static string BytesToSha1(byte[] data)
        {
            var hash = SHA1.HashData(data);
            return string.Concat(Array.ConvertAll(hash, b => b.ToString("x2")));
        }

        private bool TryLoadFromEmbedded(string textureKey)
        {
            try
            {
                var index = GetEmbeddedIndex();
                if (!index.TryGetValue(textureKey, out var resourceName))
                {
                    foreach (var kv in index)
                        if (string.Equals(kv.Key, textureKey, StringComparison.OrdinalIgnoreCase))
                            resourceName = kv.Value;
                    if (resourceName is null) return false;
                }

                var asm = typeof(TextureLoader).Assembly;
                using var stream = asm.GetManifestResourceStream(resourceName);
                if (stream is null) return false;

                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var data = ms.ToArray();
                var hash = BytesToSha1(data);
                var ext = Path.GetExtension(textureKey);
                var name = Path.GetFileNameWithoutExtension(textureKey);
                var cacheFile = Path.Combine(_cacheDirectory, $"{name}_{hash}{ext}");
                if (!File.Exists(cacheFile))
                    File.WriteAllBytes(cacheFile, data);

                Core.Overlay.AddOrGetImagePointer(cacheFile, false, out var handle, out var width, out var height);
                if (handle == IntPtr.Zero) return false;

                _loadedTexturesByKey[textureKey] = new TextureInfo(handle, (int)width, (int)height, cacheFile);
                return true;
            }
            catch 
            {
                return false;
            }
        }

        /// <summary>
        ///     Returns a snapshot list of currently loaded texture keys.
        /// </summary>
        public List<string> TextureKeys => new(_loadedTexturesByKey.Keys);

        /// <summary>
        ///     Total number of textures currently loaded.
        /// </summary>
        public int TotalTexturesLoaded => _loadedTexturesByKey.Count;

        /// <summary>
        ///     True if a key is present in the cache.
        /// </summary>
        public bool IsLoaded(string textureKey) => _loadedTexturesByKey.ContainsKey(textureKey);

        /// <summary>
        ///     Gets the texture triple (handle, width, height) for a given key.
        ///     Returns (IntPtr.Zero, 0, 0) when missing.
        /// </summary>
        public (IntPtr handle, int width, int height) GetTexture(string textureKey) =>
            _loadedTexturesByKey.TryGetValue(textureKey, out var texture)
                ? (texture.Handle, texture.Width, texture.Height)
                : (IntPtr.Zero, 0, 0);

        /// <summary>
        ///     Attempts to load the specified texture key from the first directory in <paramref name="searchDirectories"/>
        ///     that contains a matching file. If the texture is already loaded, returns true.
        /// </summary>
        public bool LoadFromDirectories(string textureKey, params string[] searchDirectories)
        {
            if (_loadedTexturesByKey.ContainsKey(textureKey))
                return true;

            if (TryLoadFromEmbedded(textureKey))
                return true;

            foreach (var directoryPath in searchDirectories)
            {
                if (string.IsNullOrWhiteSpace(directoryPath))
                    continue;

                var fullPath = Path.Combine(directoryPath, textureKey);
                if (!File.Exists(fullPath))
                    continue;

                Core.Overlay.AddOrGetImagePointer(fullPath, false, out var handle, out var width, out var height);
                if (handle == IntPtr.Zero)
                    return false;

                _loadedTexturesByKey[textureKey] = new TextureInfo(handle, (int)width, (int)height, fullPath);
                return true;
            }

            return false;
        }

        /// <summary>
        ///     Unloads and evicts a single texture by key.
        /// </summary>
        public void Unload(string textureKey)
        {
            if (!_loadedTexturesByKey.TryGetValue(textureKey, out var texture))
                return;

            _ = Core.Overlay.RemoveImage(texture.FullPath);
            _loadedTexturesByKey.Remove(textureKey);
        }

        /// <summary>
        ///     Declares the set of textures that should be loaded and kept resident.
        ///     Missing ones are attempted to be loaded (first match wins) and extra ones are unloaded.
        ///     This call also avoids redundant work by detecting when the desired set and search directories
        ///     have not changed since the last call and all desired textures are already resident.
        /// </summary>
        public void SetDesired(IEnumerable<string> desiredTextureKeys, params string[] searchDirectories)
        {
            var desiredKeys = new HashSet<string>(desiredTextureKeys ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            bool inputsUnchanged =
                DirectoriesEqual(_lastSearchDirectories, searchDirectories) &&
                _lastDesiredKeys.SetEquals(desiredKeys);

            if (inputsUnchanged)
            {
                // Early out only if all desired keys are already loaded.
                bool allDesiredLoaded = true;
                foreach (var key in desiredKeys)
                {
                    if (!_loadedTexturesByKey.ContainsKey(key))
                    {
                        allDesiredLoaded = false;
                        break;
                    }
                }

                if (allDesiredLoaded)
                    return;
            }

            _lastSearchDirectories = (string[])searchDirectories.Clone();
            _lastDesiredKeys.Clear();
            foreach (var key in desiredKeys) _lastDesiredKeys.Add(key);

            // Load missing desired textures.
            foreach (var key in desiredKeys)
                _ = LoadFromDirectories(key, searchDirectories);

            // Unload textures no longer desired.
            foreach (var key in new List<string>(_loadedTexturesByKey.Keys))
                if (!desiredKeys.Contains(key))
                    Unload(key);
        }

        /// <summary>
        ///     Unloads everything and clears the cache.
        /// </summary>
        public void Cleanup()
        {
            foreach (var textureKey in new List<string>(_loadedTexturesByKey.Keys))
                Unload(textureKey);

            try
            {
                if (!string.IsNullOrWhiteSpace(_cacheDirectory))
                    TryDeleteCacheDir(_cacheDirectory);
            }
            catch { /* ignore top-level IO errors */ }
        }

        public void Dispose() => Cleanup();

        /// <summary>
        ///     Invalidates memoized inputs so that the next <see cref="SetDesired"/> won't early-out.
        /// </summary>
        public void Invalidate()
        {
            _lastDesiredKeys.Clear();
            _lastSearchDirectories = Array.Empty<string>();
        }

        private static bool DirectoriesEqual(string[] directoriesA, string[] directoriesB)
        {
            if (ReferenceEquals(directoriesA, directoriesB)) return true;
            if (directoriesA == null || directoriesB == null) return false;
            if (directoriesA.Length != directoriesB.Length) return false;

            for (int i = 0; i < directoriesA.Length; i++)
            {
                if (!string.Equals(directoriesA[i], directoriesB[i], StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }
    }
}