using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Tasks;
using VncDotnet;
using VncDotnet.Messages;

namespace VncDotnet.Encodings
{
    internal class ZRLEEncoding
    {
        private readonly MemoryStream InflateInputStream;
        private readonly DeflateStream InflateOutputStream;
        private bool Fresh = true;

        public ZRLEEncoding()
        {
            InflateInputStream = new MemoryStream();
            InflateOutputStream = new DeflateStream(InflateInputStream, CompressionMode.Decompress);
        }

        public async Task<byte[]> ParseRectangle(PipeReader reader, RfbRectangleHeader header)
        {
            var ZRLEPipe = new Pipe();
            var parser = Task.Run(async () => await ParsePixelData(ZRLEPipe.Reader, header));

            // read compressed length
            var result = await reader.ReadMinBytesAsync(4);
            var length = BinaryPrimitives.ReadUInt32BigEndian(result.Buffer.ForceGetReadOnlySpan(4));
            reader.AdvanceTo(result.Buffer.GetPosition(4));
            long remaining = length;
            if (Fresh)
            {
                result = await reader.ReadMinBytesAsync(2);
                reader.AdvanceTo(result.Buffer.GetPosition(2));
                remaining -= 2;
                Fresh = false;
            }

            // handle compressed rect
            while (remaining > 0)
            {
                result = await reader.ReadAsync();
                long decompressed = 0;
                foreach (var segment in result.Buffer)
                {
                    if (remaining > segment.Length)
                    {
                        Decompress(ZRLEPipe.Writer, segment.Span);
                        decompressed += segment.Length;
                        remaining -= segment.Length;
                    }
                    else
                    {
                        Decompress(ZRLEPipe.Writer, segment.Span.Slice(0, (int)remaining));
                        decompressed += remaining;
                        remaining -= remaining;
                        break;
                    }
                }
                reader.AdvanceTo(result.Buffer.GetPosition(decompressed));
            }
            ZRLEPipe.Writer.Complete();
            return await parser;
        }
        private async Task<byte[]> ParsePixelData(PipeReader uncompressedReader, RfbRectangleHeader header)
        {
            byte[] data = ArrayPool<byte>.Shared.Rent(header.Width * header.Height * 4);
            for (var y = 0; y < header.Height; y += 64)
            {
                var tileHeight = Math.Min(header.Height - y, 64);
                for (var x = 0; x < header.Width; x += 64)
                {
                    var tileWidth = Math.Min(header.Width - x, 64);
                    var result = await uncompressedReader.ReadAsync();
                    byte subencoding = result.Buffer.First.Span[0];
                    uncompressedReader.AdvanceTo(result.Buffer.GetPosition(1));
                    //Debug.WriteLine($"subencoding {subencoding}");

                    if (subencoding == 0) // Raw
                    {
                        for (int ty = 0; ty < tileHeight; ty++)
                        {
                            for (int tx = 0; tx < tileWidth; tx++)
                            {
                                result = await uncompressedReader.ReadMinBytesAsync(3);
                                ParseRaw(result, y, x, ty, tx, data, header.Width);
                                uncompressedReader.AdvanceTo(result.Buffer.GetPosition(3));
                            }
                        }
                    }
                    else if (subencoding == 1) // Solid
                    {
                        var pixel = await ParseCompressedPixel(uncompressedReader);
                        for (int ty = 0; ty < tileHeight; ty++)
                        {
                            for (int tx = 0; tx < tileWidth; tx++)
                            {
                                Buffer.BlockCopy(pixel, 0, data, (
                                    ((y + ty) * header.Width) +     // y + ty rows
                                    (x + tx)                        // x + tx colums
                                    ) * 4, 4);                      // 4 bytes per pixel
                            }
                        }
                        ArrayPool<byte>.Shared.Return(pixel);
                    }
                    else if (subencoding <= 16) // Packed Palette
                    {
                        var palette = new byte[subencoding][];
                        var bitfieldLength = GetPackedPixelBitFieldLength(subencoding);
                        for (int i = 0; i < subencoding; i++)
                        {
                            palette[i] = await ParseCompressedPixel(uncompressedReader);
                        }
                        for (int i = 0; i < tileHeight; i++)
                        {
                            //int c; // = zlibMemoryStream.ReadByte();
                            for (int j = 0; j < tileWidth; j++)
                            {
                                if (j * bitfieldLength % 8 == 0)
                                {
                                    result = await uncompressedReader.ReadAsync();
                                    //c = result.Buffer.First.Span[0];
                                    uncompressedReader.AdvanceTo(result.Buffer.GetPosition(1));
                                }
                                    
                            }
                        }
                        foreach (var pixel in palette)
                        {
                            ArrayPool<byte>.Shared.Return(pixel);
                        }
                    }
                    else if (17 <= subencoding && subencoding <= 127)
                    {
                        throw new InvalidDataException("Subencodings 17 to 127 are unused");
                    }
                    else if (subencoding == 128)
                    {
                        // Plain RLE
                        await ParsePlainRLETile(tileWidth, tileHeight, data, uncompressedReader);
                    }
                    else if (130 <= subencoding && subencoding <= 255)
                    {
                        // Palette RLE
                        var paletteSize = subencoding - 128;
                        var palette = new byte[paletteSize][];
                        for (int i = 0; i < palette.Length; i++)
                        {
                            palette[i] = await ParseCompressedPixel(uncompressedReader);
                        }
                        var pixelsRemaining = tileWidth * tileHeight;
                        while (pixelsRemaining > 0)
                        {
                            result = await uncompressedReader.ReadAsync();
                            var paletteIndex = result.Buffer.First.Span[0];
                            uncompressedReader.AdvanceTo(result.Buffer.GetPosition(1));
                            var runLength = 1;
                            if ((paletteIndex & 128) != 0)
                            {
                                paletteIndex -= 128;
                                int b;
                                do
                                {
                                    result = await uncompressedReader.ReadAsync();
                                    b = result.Buffer.First.Span[0];
                                    uncompressedReader.AdvanceTo(result.Buffer.GetPosition(1));
                                    runLength += b;
                                } while (b == byte.MaxValue);
                            }
                            pixelsRemaining -= runLength;
                            //Debug.WriteLine($"index {paletteIndex} len {runLength}");
                            if (paletteIndex > palette.Length)
                                throw new InvalidDataException();
                        }
                        foreach (var pixel in palette)
                        {
                            ArrayPool<byte>.Shared.Return(pixel);
                        }
                    }
                    else
                    {
                        throw new NotImplementedException();
                    }
                }
            }
            return data;
        }

        private void ParseRaw(ReadResult result, int y, int x, int ty, int tx, byte[] data, int rectangleWidth)
        {
            var raw = result.Buffer.ForceGetReadOnlySpan(3);
            Buffer.BlockCopy(new byte[] { raw[0], raw[1], raw[2], 0xff }, 0, data, (
                                    ((y + ty) * rectangleWidth) +     // y + ty rows
                                    (x + tx)                        // x + tx colums
                                    ) * 4, 4);                      // 4 bytes per pixel
        }

        private int GetPackedPixelBitFieldLength(uint paletteSize)
        {
            if (paletteSize <= 2)
                return 1;
            if (paletteSize <= 4)
                return 2;
            if (paletteSize <= 16)
                return 4;
            throw new InvalidOperationException("Invalid palette size");
        }

        #region gammel
        private async Task ParsePlainRLETile(int tileWidth, int tileHeight, Memory<byte> data, PipeReader uncompressedReader)
        {
            Debug.Assert(data.Length > 0);
            var tileLength = tileWidth * tileHeight;
            while (tileLength > 0)
            {
                var pixel = await ParseCompressedPixel(uncompressedReader);
                tileLength -= await ParseRunLength(uncompressedReader);
                ArrayPool<byte>.Shared.Return(pixel);
            }
            if (tileLength != 0)
                throw new InvalidDataException();
        }

        private async Task<int> ParseRunLength(PipeReader uncompressedReader)
        {
            int runLength = 1;
            int b;
            do
            {
                var result = await uncompressedReader.ReadAsync();
                b = result.Buffer.First.Span[0];
                uncompressedReader.AdvanceTo(result.Buffer.GetPosition(1));
                runLength += b;
            } while (b == byte.MaxValue);
            return runLength;
        }

        private async Task<byte[]> ParseCompressedPixel(PipeReader uncompressedReader)
        {
            var result = await uncompressedReader.ReadMinBytesAsync(3);
            var pixel = ParseCompressedPixel(result);
            uncompressedReader.AdvanceTo(result.Buffer.GetPosition(3));
            return pixel;
        }
        #endregion

        private byte[] ParseCompressedPixel(ReadResult result)
        {
            var pixel = ArrayPool<byte>.Shared.Rent(4);
            var span = result.Buffer.ForceGetReadOnlySpan(3);
            pixel[0] = span[0];
            pixel[1] = span[1];
            pixel[2] = span[2];
            pixel[3] = 0xff;
            return pixel;
        }

        private void Decompress(PipeWriter writer, ReadOnlySpan<byte> data)
        {
            var buf = ArrayPool<byte>.Shared.Rent(4096);
            InflateInputStream.Position = 0;
            InflateInputStream.Write(data);
            InflateInputStream.Position = 0;
            InflateInputStream.SetLength(data.Length);
            int read;

            do
            {
                read = InflateOutputStream.Read(buf);
                writer.WriteAsync(buf.AsMemory().Slice(0, read));
            }
            while (read != 0);
            ArrayPool<byte>.Shared.Return(buf);
        }
    }
}
