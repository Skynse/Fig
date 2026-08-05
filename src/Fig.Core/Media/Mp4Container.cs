using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Fig.Core.Media
{
    /// <summary>
    /// Lightweight MP4 checks that avoid FFmpeg (and its console spam) when deciding
    /// whether a proxy file is safe to open for playback.
    /// </summary>
    public static class Mp4Container
    {
        /// <summary>
        /// True when <paramref name="path"/> looks like a finalized non-fragmented MP4:
        /// starts with <c>ftyp</c> (or a skippable box then ftyp) and contains a top-level
        /// <c>moov</c> atom. Incomplete proxies written without a trailer fail this check.
        /// </summary>
        public static bool IsCompleteMp4(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return false;

                using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length < 16)
                    return false;

                Span<byte> hdr = stackalloc byte[8];
                Span<byte> ext = stackalloc byte[8];
                var sawFtyp = false;
                long pos = 0;
                while (pos + 8 <= fs.Length)
                {
                    fs.Position = pos;
                    if (fs.Read(hdr) < 8)
                        return false;

                    var size = BinaryPrimitives.ReadUInt32BigEndian(hdr);
                    var type = Encoding.ASCII.GetString(hdr.Slice(4, 4));
                    long headerSize = 8;
                    long boxSize;
                    if (size == 1)
                    {
                        // 64-bit extended size
                        if (fs.Read(ext) < 8)
                            return false;
                        boxSize = (long)BinaryPrimitives.ReadUInt64BigEndian(ext);
                        headerSize = 16;
                    }
                    else if (size == 0)
                    {
                        // extends to EOF
                        boxSize = fs.Length - pos;
                    }
                    else
                    {
                        boxSize = size;
                    }

                    if (boxSize < headerSize || pos + boxSize > fs.Length)
                        return false;

                    if (type == "ftyp")
                        sawFtyp = true;
                    else if (type == "moov" && sawFtyp)
                        return true;

                    pos += boxSize;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}
