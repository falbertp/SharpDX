﻿// Copyright (c) 2010-2013 SharpDX - Alexandre Mutel
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using SharpDX.Collections;

namespace SharpDX.Toolkit.Content
{
    /// <summary>
    /// The content manager implementation is responsible to load and store content data (texture, songs, effects...etc.) using 
    /// several <see cref="IContentResolver"/> to resolve a stream from an asset name and several registered <see cref="IContentReader"/>
    /// to convert data from stream.
    /// </summary>
    public class ContentManager : Component, IContentManager
    {
        private readonly Dictionary<string, object> assetLockers;
        private readonly Dictionary<string, object> loadedAssets;
        private readonly List<IContentResolver> registeredContentResolvers;
        private readonly List<IContentReader> registeredContentReaders;

        private string rootDirectory;

        /// <summary>
        /// Initializes a new instance of ContentManager. Reference page contains code sample.
        /// </summary>
        /// <param name="serviceProvider">The service provider that the ContentManager should use to locate services.</param>
        public ContentManager(IServiceProvider serviceProvider)
            : base("ContentManager")
        {
            if (serviceProvider == null)
                throw new ArgumentNullException("serviceProvider");

            ServiceProvider = serviceProvider;

            // Content resolvers
            Resolvers = new ObservableCollection<IContentResolver>();
            Resolvers.ItemAdded += ContentResolvers_ItemAdded;
            Resolvers.ItemRemoved += ContentResolvers_ItemRemoved;
            registeredContentResolvers = new List<IContentResolver>();

            // Content readers.
            Readers = new ObservableCollection<IContentReader>();
            Readers.ItemAdded += ContentReaders_ItemAdded;
            Readers.ItemRemoved += ContentReaders_ItemRemoved;
            registeredContentReaders = new List<IContentReader>();

            loadedAssets = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            assetLockers = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Add or remove registered <see cref="IContentResolver"/> to this instance.
        /// </summary>
        public ObservableCollection<IContentResolver> Resolvers { get; private set; }

        /// <summary>
        /// Add or remove registered <see cref="IContentReader"/> to this instance.
        /// </summary>
        public ObservableCollection<IContentReader> Readers { get; private set; }

        /// <summary>
        /// Gets the service provider associated with the ContentManager.
        /// </summary>
        /// <value>The service provider.</value>
        public IServiceProvider ServiceProvider { get; protected set; }

        /// <summary>
        /// Gets or sets the root directory.
        /// </summary>
        public string RootDirectory
        {
            get
            {
                return rootDirectory;
            }

            set
            {
                if (loadedAssets.Count > 0)
                {
                    throw new InvalidOperationException("RootDirectory cannot be changed when a ContentManager has already assets loaded");
                }

                rootDirectory = value;
            }
        }

        public virtual bool Exists(string assetName)
        {
            // First, resolve the stream for this asset.
            List<IContentResolver> resolvers;
            lock (registeredContentResolvers)
            {
                resolvers = new List<IContentResolver>(registeredContentResolvers);
            }

            if (resolvers.Count == 0)
            {
                throw new InvalidOperationException("No resolver registered to this content manager");
            }

            string assetPath = Path.Combine(rootDirectory ?? string.Empty, assetName);

            foreach (var contentResolver in resolvers)
            {
                if (contentResolver.Exists(assetPath)) return true;
            }

            return false;
        }

        /// <summary>
        /// Loads an asset that has been processed by the Content Pipeline.  Reference page contains code sample.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="assetName">The asset name </param>
        /// <param name="options">The options to pass to the content reader (null by default).</param>
        /// <returns>``0.</returns>
        /// <exception cref="SharpDX.Toolkit.Content.AssetNotFoundException">If the asset was not found from all <see cref="IContentResolver" />.</exception>
        /// <exception cref="NotSupportedException">If no content reader was suitable to decode the asset.</exception>
        public virtual T Load<T>(string assetName, object options = null)
        {
            object result = null;

            // Lock loading by asset name, like this, we can have several loading in multithreaded // with a single instance per asset name
            lock (GetAssetLocker(assetName, true))
            {
                // First, try to load the asset from the cache
                lock (loadedAssets)
                {
                    if (loadedAssets.TryGetValue(assetName, out result))
                    {
                        return (T)result;
                    }
                }

                // Else we need to load it from a content resolver disk/zip...etc.
                string assetPath = Path.Combine(rootDirectory ?? string.Empty, assetName);

                // First, resolve the stream for this asset.
                Stream stream = FindStream(assetPath);
                if (stream == null)
                    throw new AssetNotFoundException(assetName);

                result = LoadAssetWithDynamicContentReader<T>(assetName, stream, options);

                // Cache the loaded assets
                lock (loadedAssets)
                {
                    loadedAssets.Add(assetName, result);
                }
            }

            // We could have an exception, but that's fine, as the user will be able to find why.
            return (T)result;
        }

        /// <summary>
        /// Unloads all data that was loaded by this ContentManager. All data will be disposed.
        /// </summary>
        /// <remarks>
        /// Unlike <see cref="Load{T}"/> method, this method is not thread safe and must be called by a single caller at a single time.
        /// </remarks>
        public virtual void Unload()
        {
            foreach (var loadedAsset in loadedAssets.Values)
            {
                var disposable = loadedAsset as IDisposable;
                if (disposable != null)
                    disposable.Dispose();
            }

            assetLockers.Clear();
            loadedAssets.Clear();
        }

        /// <summary>
        ///	Unloads and disposes an asset.
        /// </summary>
        /// <param name="assetName">The asset name</param>
        /// <returns><c>true</c> if the asset exists and was unloaded, <c>false</c> otherwise.</returns>
        public virtual bool Unload(string assetName)
        {
            object asset;

            object assetLockerRead = GetAssetLocker(assetName, false);
            if (assetLockerRead == null)
                return false;

            lock (assetLockerRead)
            {
                lock (loadedAssets)
                {
                    if (!loadedAssets.TryGetValue(assetName, out asset))
                        return false;
                    loadedAssets.Remove(assetName);
                }

                lock (assetLockers)
                    assetLockers.Remove(assetName);
            }

            var disposable = asset as IDisposable;
            if (disposable != null)
                disposable.Dispose();

            return true;
        }

        private object GetAssetLocker(string assetName, bool create)
        {
            object assetLockerRead;
            lock (assetLockers)
            {
                if (!assetLockers.TryGetValue(assetName, out assetLockerRead) && create)
                {
                    assetLockerRead = new object();
                    assetLockers.Add(assetName, assetLockerRead);
                }
            }
            return assetLockerRead;
        }

        private Stream FindStream(string assetName)
        {
            Stream stream = null;
            // First, resolve the stream for this asset.
            List<IContentResolver> resolvers;
            lock (registeredContentResolvers)
            {
                resolvers = new List<IContentResolver>(registeredContentResolvers);
            }

            if (resolvers.Count == 0)
            {
                throw new InvalidOperationException("No resolver registered to this content manager");
            }

            foreach (var contentResolver in resolvers)
            {
                stream = contentResolver.Resolve(assetName);
                if (stream != null)
                    break;
            }
            return stream;
        }

        private object LoadAssetWithDynamicContentReader<T>(string assetName, Stream stream, object options)
        {
            object result = null;

            var parameters = new ContentReaderParameters()
                                 {
                                     AssetName = assetName,
                                     AssetType = typeof(T),
                                     Stream = stream,
                                     Options = options
                                 };

            try
            {
                // Else try to load using a dynamic content reader attribute
#if WIN8METRO
                var contentReaderAttribute = Utilities.GetCustomAttribute<ContentReaderAttribute>(typeof(T).GetTypeInfo(), true);
#else
                var contentReaderAttribute = Utilities.GetCustomAttribute<ContentReaderAttribute>(typeof(T), true);
#endif
                if (contentReaderAttribute != null)
                {
                    IContentReader contentReader = null;


#if WIN8METRO
                    var isReaderValid = typeof(IContentReader).GetTypeInfo().IsAssignableFrom(contentReaderAttribute.ContentReaderType.GetTypeInfo());
#else
                    var isReaderValid = typeof(IContentReader).IsAssignableFrom(contentReaderAttribute.ContentReaderType);
#endif
                    if (!isReaderValid)
                    {
                        throw new NotSupportedException(string.Format("Invalid content reader type [{0}]. Expecting an instance of IContentReader", contentReaderAttribute.ContentReaderType.FullName));
                    }


                    lock (registeredContentReaders)
                    {
                        foreach (var reader in registeredContentReaders)
                        {
                            if (contentReaderAttribute.ContentReaderType == reader.GetType())
                            {
                                contentReader = reader;
                                break;
                            }
                        }

                        if (contentReader == null)
                        {
                            contentReader = (IContentReader)Activator.CreateInstance(contentReaderAttribute.ContentReaderType);

                            // If this content reader has been used successfully, then we can register it.
                            lock (registeredContentReaders)
                            {
                                registeredContentReaders.Add(contentReader);
                            }
                        }
                    }

                    result = contentReader.ReadContent(this, ref parameters);
                }
                else
                {
                    throw new InvalidOperationException(string.Format("Type [{0}] doesn't provide a ContentReaderAttribute", typeof(T).FullName));
                }

                if (result == null) throw new NotSupportedException("Unable to load content");
            }
            finally
            {
                // If we don't need to keep the stream open, then we can close it
                // and make sure that we will close the stream even if there is an exception.
                if (!parameters.KeepStreamOpen)
                    stream.Dispose();
            }

            return result;
        }

        protected override void Dispose(bool disposeManagedResources)
        {
            if (disposeManagedResources)
            {
                Unload();
            }

            base.Dispose(disposeManagedResources);
        }

        private void ContentResolvers_ItemAdded(object sender, ObservableCollectionEventArgs<IContentResolver> e)
        {
            lock (registeredContentResolvers)
            {
                registeredContentResolvers.Add(e.Item);
            }
        }

        private void ContentResolvers_ItemRemoved(object sender, ObservableCollectionEventArgs<IContentResolver> e)
        {
            lock (registeredContentResolvers)
            {
                registeredContentResolvers.Add(e.Item);
            }
        }

        private void ContentReaders_ItemAdded(object sender, ObservableCollectionEventArgs<IContentReader> e)
        {
            lock (registeredContentReaders)
            {
                registeredContentReaders.Add(e.Item);
            }
        }

        private void ContentReaders_ItemRemoved(object sender, ObservableCollectionEventArgs<IContentReader> e)
        {
            lock (registeredContentReaders)
            {
                registeredContentReaders.Add(e.Item);
            }
        }
    }
}
