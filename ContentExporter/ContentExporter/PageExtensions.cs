namespace ContentExporter
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Text;
    using System.Web;
    using System.Web.Security;
    using System.Web.UI;

    using HtmlAgilityPack;

    using ICSharpCode.SharpZipLib.Core;
    using ICSharpCode.SharpZipLib.Zip;

    public static class PageExtensions
    {
        public static void ExportContent(this Page page)
        {
            page.Response.Clear();
            page.Response.ContentType = "application/zip";
            page.Response.AddHeader("Content-Disposition", "attachment;filename=archive.zip");
            page.Response.BufferOutput = false;
            page.Response.ContentEncoding = Encoding.Default;
            page.Response.Charset = string.Empty;

            page.ExportContent(page.Response.OutputStream);
        }

        public static void ExportContent(this Page page, Stream destination)
        {
            var fileName = Path.GetTempFileName();

            using (var zippedStream = new ZipOutputStream(File.OpenWrite(fileName)))
            {
                zippedStream.UseZip64 = UseZip64.Off;
                zippedStream.SetLevel(9);

                var assets = new Assets();

                ExportDocument(page, zippedStream, assets);
                ExportAdditionalAssets(page, zippedStream, assets);

                zippedStream.Finish();
            }

            using (var source = File.OpenRead(fileName))
            {
                StreamUtils.Copy(source, destination, new byte[32 * 1024]);
            }

            File.Delete(fileName);
        }

        private static string GetApplicationRoot(HttpRequest request)
        {
            var currentDomain = request.Url.Scheme + Uri.SchemeDelimiter + request.Url.Host;

            if (request.Url.Port != 80 && request.Url.Port != 443)
            {
                currentDomain += ":" + request.Url.Port;
            }

            return currentDomain;
        }

        private static void ExportAdditionalAssets(Page page, ZipOutputStream stream, Assets assets)
        {
            PutEntries(
                stream,
                assets.StaticContent.Where(item => File.Exists(page.Server.MapPath(item.Key))),
                item => item.Value,
                item => File.OpenRead(page.Server.MapPath(item.Key)));

            PutEntries(
                stream,
                assets.DynamicContent,
                item => item.NormalizedPath,
                item => item.Stream);
        }

        private static void PutEntries<T>(ZipOutputStream destination, IEnumerable<T> items, Func<T, string> pathSelector, Func<T, Stream> streamSelector)
        {
            var buffer = new byte[32 * 1024];

            foreach (var item in items)
            {
                var entry = new ZipEntry(pathSelector(item));

                destination.PutNextEntry(entry);

                using (var source = streamSelector(item))
                {
                    StreamUtils.Copy(source, destination, buffer);
                }
            }
        }

        private static void ExportDocument(Page page, ZipOutputStream stream, Assets assets)
        {
            var entry = new ZipEntry("index.html");

            stream.PutNextEntry(entry);

            var document = GetNormalizedDocument(page, assets);

            var writer = new StreamWriter(stream);

            writer.Write(document);

            writer.Flush();
        }

        private static string GetNormalizedDocument(Page page, Assets assets)
        {
            string output;

            using (var mem = new StringWriter())
            using (var writer = new XhtmlTextWriter(mem))
            {
                page.RenderControl(writer);

                output = mem.GetStringBuilder().ToString();
            }

            var document = new HtmlDocument();

            using (var reader = new StringReader(output))
            {
                document.Load(reader);
            }

            foreach (var node in document.DocumentNode.SelectNodes("//*[@href]|//*[@src]").Where(n => n.Name != "a"))
            {
                foreach (var attr in new[] { "href", "src" }.Where(a => node.GetAttributeValue(a, null) != null))
                {
                    assets.Add(node.GetAttributeValue(attr, null));
                }
            }

            GetDynamicContent(page, assets.DynamicContent);

            foreach (var item in assets.StaticContent)
            {
                output = output.Replace(item.Key, item.Value);
            }

            foreach (var item in assets.DynamicContent)
            {
                output = output.Replace(item.Path, item.NormalizedPath);
            }

            return output;
        }

        private static void GetDynamicContent(Page page, IEnumerable<DynamicAsset> assets)
        {
            var index = -1;

            // since I'm using Forms Authentication, mimic this current authentication context.
            var cookie = page.Request.Cookies[FormsAuthentication.FormsCookieName];

            var authenticationCookie = new Cookie(
                FormsAuthentication.FormsCookieName,
                cookie.Value,
                cookie.Path,
                page.Request.Url.Authority);

            var appRoot = GetApplicationRoot(page.Request);

            var buffer = new byte[32 * 1024];

            foreach (var item in assets)
            {
                var request = (HttpWebRequest)WebRequest.Create(appRoot + item.Path);

                request.UserAgent = page.Request.UserAgent;

                request.CookieContainer = new CookieContainer();
                request.CookieContainer.Add(authenticationCookie);

                var response = (HttpWebResponse)request.GetResponse();

                item.ContentType = response.ContentType;
                item.NormalizedPath = string.Format("dyn_{0:000}.{1}", ++index, item.Extension);
                item.Stream = new MemoryStream();

                using (var responseStream = response.GetResponseStream())
                {
                    StreamUtils.Copy(responseStream, item.Stream, buffer);
                }

                item.Stream.Position = 0;
            }
        }

        internal class Assets
        {
            public Assets()
            {
                this.StaticContent = new PathNormailzationDictionary();
                this.DynamicContent = new DynamicAssetCollection();
            }

            public DynamicAssetCollection DynamicContent { get; private set; }

            public PathNormailzationDictionary StaticContent { get; private set; }

            public void Add(string path)
            {
                if (path.Contains("?"))
                {
                    if (!this.DynamicContent.ContainsKey(path))
                    {
                        this.DynamicContent.Add(new DynamicAsset(path));
                    }
                }
                else
                {
                    this.StaticContent.Add(path);
                }
            }
        }

        internal class DynamicAsset
        {
            private static Dictionary<string, string> ContentTypeExtensionMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "image/png", "png" },
                    { "image/gif", "gif" },
                    { "text/html", "html" }
                };

            public DynamicAsset(string path)
            {
                this.Path = path;
            }

            public Stream Stream { get; set; }

            public string ContentType { get; set; }

            public string Extension
            {
                get
                {
                    if (ContentTypeExtensionMap.ContainsKey(this.ContentType))
                    {
                        return ContentTypeExtensionMap[this.ContentType];
                    }

                    return "xxx";
                }
            }

            public string Path { get; private set; }

            public string NormalizedPath { get; set; }
        }

        internal class DynamicAssetCollection : KeyedCollection<string, DynamicAsset>
        {
            public DynamicAssetCollection()
                : this(StringComparer.OrdinalIgnoreCase)
            {
            }

            public DynamicAssetCollection(IEqualityComparer<string> comparer)
                : base(comparer)
            {
            }

            public bool ContainsKey(string key)
            {
                return this.Contains(key);
            }

            protected override string GetKeyForItem(DynamicAsset item)
            {
                return item.Path;
            }
        }

        internal class PathNormailzationDictionary : IDictionary<string, string>
        {
            #region Constants and Fields

            private IEqualityComparer<string> comparer;

            private Dictionary<string, Dictionary<string, int>> map;

            #endregion

            #region Constructors and Destructors

            public PathNormailzationDictionary()
                : this(StringComparer.OrdinalIgnoreCase)
            {
            }

            public PathNormailzationDictionary(IEqualityComparer<string> comparer)
            {
                this.comparer = comparer;
                this.map = new Dictionary<string, Dictionary<string, int>>(this.comparer);
            }

            #endregion

            #region Properties

            public int Count
            {
                get
                {
                    return this.map.Sum(m => m.Value.Count);
                }
            }

            public bool IsReadOnly
            {
                get
                {
                    return false;
                }
            }

            public ICollection<string> Keys
            {
                get
                {
                    return (from item in this select item.Key).ToList();
                }
            }

            public ICollection<string> Values
            {
                get
                {
                    return (from item in this select item.Value).ToList();
                }
            }

            #endregion

            #region Indexers

            public string this[string key]
            {
                get
                {
                    string value;

                    this.TryGetValue(key, out value);

                    return value;
                }

                set
                {
                    throw new NotSupportedException();
                }
            }

            #endregion

            #region Public Methods

            public void Add(string path)
            {
                var key = this.GetKey(path);

                if (!this.map.ContainsKey(key))
                {
                    this.map.Add(key, new Dictionary<string, int>(this.comparer));
                }

                if (!this.map[key].ContainsKey(path))
                {
                    this.map[key].Add(path, this.map[key].Count);
                }
            }

            #endregion

            private string GetKey(string path)
            {
                var ext = Path.GetExtension(path);

                if (ext != null && ext.StartsWith("."))
                {
                    return ext.Substring(1);
                }

                return ext;
            }

            #region Implemented Interfaces

            #region ICollection<KeyValuePair<string,string>>

            public void Clear()
            {
                this.map.Clear();
            }

            public bool Contains(KeyValuePair<string, string> item)
            {
                return this.ContainsKey(item.Key);
            }

            public void CopyTo(KeyValuePair<string, string>[] array, int arrayIndex)
            {
                throw new NotImplementedException();
            }

            public bool Remove(KeyValuePair<string, string> item)
            {
                throw new NotImplementedException();
            }

            void ICollection<KeyValuePair<string, string>>.Add(KeyValuePair<string, string> item)
            {
                this.Add(item.Key);
            }

            #endregion

            #region IDictionary<string,string>

            public bool ContainsKey(string key)
            {
                return this.map.ContainsKey(this.GetKey(key)) && this.map[this.GetKey(key)].ContainsKey(key);
            }

            public bool Remove(string key)
            {
                throw new NotImplementedException();
            }

            public bool TryGetValue(string key, out string value)
            {
                if (this.ContainsKey(key))
                {
                    value = string.Format("{0}_{1:0000}.{0}", this.GetKey(key), this.map[this.GetKey(key)][key]);

                    return true;
                }
                else
                {
                    value = null;

                    return false;
                }
            }

            void IDictionary<string, string>.Add(string key, string value)
            {
                this.Add(key);
            }

            #endregion

            #region IEnumerable

            IEnumerator IEnumerable.GetEnumerator()
            {
                return this.GetEnumerator();
            }

            #endregion

            #region IEnumerable<KeyValuePair<string,string>>

            public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
            {
                foreach (var selector in this.map)
                {
                    foreach (var item in selector.Value)
                    {
                        yield return
                            new KeyValuePair<string, string>(
                                item.Key, string.Format("{0}_{1:0000}.{0}", selector.Key, item.Value));
                    }
                }
            }

            #endregion

            #endregion
        }
    }
}
