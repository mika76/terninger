﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using MurrayGrant.Terninger.Helpers;

namespace MurrayGrant.Terninger.PersistentState
{
    /// <summary>
    /// A file backed state reader and writer which stores each item on a separate line using key values.
    /// </summary>
    /// <remarks>
    /// The first line is a header:
    ///    magic number, version number, base64(sha256 checksum of file), count of items (lines)
    /// Each line looks like
    ///    namespace, key, base64(value)
    /// The "Record Separator" (U+001F) is used as the default delimiter.
    /// Newlines may be any combination of \r and \n (just \n is emitted by default).
    /// All write operations write the entire file to disk, even for a single record.
    /// Any seekable stream can be used.
    ///
    /// This works pretty well for ~100 items, each of ~64 bytes each.
    /// But isn't so great for more items or larger items.
    /// </remarks>
    public sealed class TextFileReaderWriter : IPersistentStateReader, IPersistentStateWriter
    {
        public const string DefaultSeparator = "\u001F";
        public static readonly Encoding DefaultEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        public const string MagicString = "TngrData";       // Keep to 8 characters to fit in a UInt64
        public const int FileVersionNumber = 1;

        public string Separator { get; }
        readonly string[] Separators;
        public Encoding Encoding { get; }
        public string FilePath { get; }

        public TextFileReaderWriter(string filePath, string separator = DefaultSeparator, Encoding encoding = null, bool disposeStream = false)
        {
            if (String.IsNullOrEmpty(filePath))
                throw new ArgumentNullException(nameof(filePath));

            this.FilePath = filePath;
            this.Separator = String.IsNullOrEmpty(separator) ? DefaultSeparator : separator;
            this.Separators = new[] { this.Separator };
            this.Encoding = encoding ?? DefaultEncoding;
        }

        public Task<PersistentItemCollection> ReadAsync()
        {
            using var stream = new FileStream(this.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new TextStreamReader(stream, Separator, Encoding).ReadAsync();
        }

        public async Task WriteAsync(PersistentItemCollection items)
        {
            _ = items ?? throw new ArgumentNullException(nameof(items));

            // Write to temp file.
            var tempPath = this.FilePath + ".tmp";
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await new TextStreamWriter(stream, Separator, Encoding).WriteAsync(items);
            }

            // Swap temp file to main path.
            // Note this isn't transactional, but shouldn't destroy any data in the face of failure.
            var oldPath = this.FilePath + ".old";
            if (File.Exists(oldPath) && File.Exists(this.FilePath))
                File.Delete(oldPath);
            if (File.Exists(this.FilePath))
                File.Move(this.FilePath, oldPath);
            File.Move(tempPath, this.FilePath);
            if (File.Exists(oldPath))
                File.Delete(oldPath);
        }

        #region Static methods shared with TextStreamReader and TextStreamWriter
        internal static void SeekToBeginningOfData(Stream stream)
        {
            stream.Seek(0L, SeekOrigin.Begin);

            // Find the start of the first line break.
            int b;
            while ((b = stream.ReadByte()) != -1)
            {
                if (b == 0x0a || b == 0x0d)
                    break;
            }
            if (b == -1)
                throw new InvalidDataException("No data found!");

            // Find beginning of first line
            while ((b = stream.ReadByte()) != -1)
            {
                if (b != 0x0a && b != 0x0d)
                    break;
            }
            if (b == -1)
                throw new InvalidDataException("No data found!");
            stream.Seek(-1L, SeekOrigin.Current);
        }

        internal static byte[] ChecksumFromCurrentPosition(Stream stream)
            => SHA256.Create().ComputeHash(stream);

        internal static string StreamName(Stream stream)
            => stream is FileStream fs ? fs.Name
            : stream.GetType().Name;
        #endregion
    }
}
