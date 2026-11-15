#nullable enable

using System.Buffers.Binary;
using Avalonia.Media.Imaging;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Reads common image dimensions before requesting a thumbnail with a bounded long edge.</summary>
    internal static class CoverThumbnailDecoder
    {
        /// <summary>Limits retained cover pixels in either dimension.</summary>
        private const int MaximumEdge = 256;

        /// <summary>Decodes a supported cover directly at thumbnail resolution.</summary>
        /// <param name="path">The complete local image path.</param>
        /// <returns>A bitmap with both dimensions at most 256 pixels, or null for an unsupported header.</returns>
        public static Bitmap? Decode(string path)
        {
            using var stream = File.OpenRead(path);
            if (!TryReadSize(stream, out var width, out var height) || width <= 0 || height <= 0)
            {
                return null;
            }

            stream.Position = 0;
            return width >= height
                ? Bitmap.DecodeToWidth(stream, Math.Min(width, MaximumEdge), BitmapInterpolationMode.MediumQuality)
                : Bitmap.DecodeToHeight(stream, Math.Min(height, MaximumEdge), BitmapInterpolationMode.MediumQuality);
        }

        /// <summary>Reads PNG, JPEG, GIF, BMP and WebP dimensions without decoding original-size pixels.</summary>
        /// <param name="stream">The seekable local image stream.</param>
        /// <param name="width">Receives the source width.</param>
        /// <param name="height">Receives the source height.</param>
        /// <returns>Whether a valid supported header supplied dimensions.</returns>
        private static bool TryReadSize(FileStream stream, out int width, out int height)
        {
            width = 0;
            height = 0;
            Span<byte> header = stackalloc byte[32];
            var length = (int)Math.Min(header.Length, stream.Length);
            stream.ReadExactly(header[..length]);
            if (length >= 24 && header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            {
                width = BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
                height = BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
                return true;
            }

            if (length >= 10 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8)))
            {
                width = BinaryPrimitives.ReadUInt16LittleEndian(header[6..8]);
                height = BinaryPrimitives.ReadUInt16LittleEndian(header[8..10]);
                return true;
            }

            if (length >= 26 && header[..2].SequenceEqual("BM"u8))
            {
                var dibLength = BinaryPrimitives.ReadUInt32LittleEndian(header[14..18]);
                if (dibLength == 12)
                {
                    width = BinaryPrimitives.ReadUInt16LittleEndian(header[18..20]);
                    height = BinaryPrimitives.ReadUInt16LittleEndian(header[20..22]);
                }
                else if (dibLength >= 40)
                {
                    width = BinaryPrimitives.ReadInt32LittleEndian(header[18..22]);
                    var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(header[22..26]);
                    height = signedHeight == int.MinValue ? 0 : Math.Abs(signedHeight);
                }

                return true;
            }

            if (length >= 30 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
            {
                if (header[12..16].SequenceEqual("VP8X"u8))
                {
                    width = 1 + header[24] + (header[25] << 8) + (header[26] << 16);
                    height = 1 + header[27] + (header[28] << 8) + (header[29] << 16);
                }
                else if (header[12..16].SequenceEqual("VP8L"u8) && header[20] == 0x2f)
                {
                    var bits = BinaryPrimitives.ReadUInt32LittleEndian(header[21..25]);
                    width = (int)(bits & 0x3fff) + 1;
                    height = (int)((bits >> 14) & 0x3fff) + 1;
                }
                else if (header[12..16].SequenceEqual("VP8 "u8) && header[23..26].SequenceEqual(new byte[] { 0x9d, 0x01, 0x2a }))
                {
                    width = BinaryPrimitives.ReadUInt16LittleEndian(header[26..28]) & 0x3fff;
                    height = BinaryPrimitives.ReadUInt16LittleEndian(header[28..30]) & 0x3fff;
                }

                return width > 0 && height > 0;
            }

            if (length >= 2 && header[0] == 0xff && header[1] == 0xd8)
            {
                stream.Position = 2;
                while (stream.Position < Math.Min(stream.Length, 1024 * 1024))
                {
                    if (stream.ReadByte() != 0xff)
                    {
                        return false;
                    }

                    var marker = stream.ReadByte();
                    while (marker == 0xff)
                    {
                        marker = stream.ReadByte();
                    }

                    if (marker is < 0 or 0xd9 or 0xda)
                    {
                        return false;
                    }

                    if (marker is 0x01 or >= 0xd0 and <= 0xd8)
                    {
                        continue;
                    }

                    stream.ReadExactly(header[..2]);
                    var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(header[..2]);
                    if (segmentLength < 2 || stream.Position + segmentLength - 2 > stream.Length)
                    {
                        return false;
                    }

                    if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc) && segmentLength >= 7)
                    {
                        stream.ReadExactly(header[..5]);
                        height = BinaryPrimitives.ReadUInt16BigEndian(header[1..3]);
                        width = BinaryPrimitives.ReadUInt16BigEndian(header[3..5]);
                        return true;
                    }

                    stream.Position += segmentLength - 2;
                }
            }

            return false;
        }
    }
}
