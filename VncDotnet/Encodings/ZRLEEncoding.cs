using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
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

        public async Task<byte[]> ParseRectangle(PipeReader reader, RfbRectangleHeader header, PixelFormat format)
        {
            Debug.Assert(format.BitsPerPixel == 32);
            var ZRLEPipe = new Pipe();
            var parser = Task.Run(() => ParsePixelData(ZRLEPipe.Reader, header));

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
                        await Decompress(ZRLEPipe.Writer, segment);
                        decompressed += segment.Length;
                        remaining -= segment.Length;
                    }
                    else
                    {
                        await Decompress(ZRLEPipe.Writer, segment.Slice(0, (int)remaining));
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
            Array.Clear(data, 0, data.Length);
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
                        await ParseRawTile(uncompressedReader, data, x, y, tileWidth, tileHeight, header.Width);
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
                        await ParsePackedPaletteTile(uncompressedReader, data, x, y, tileWidth, tileHeight, header.Width, subencoding);
                    }
                    else if (17 <= subencoding && subencoding <= 127)
                    {
                        throw new InvalidDataException("Subencodings 17 to 127 are unused");
                    }
                    else if (subencoding == 128) // Plain RLE
                    {
                        await ParsePlainRLETile(uncompressedReader, data, x, y, tileWidth, tileHeight, header.Width);
                    }
                    else if (130 <= subencoding && subencoding <= 255) // Palette RLE
                    {
                        await ParsePaletteRLETile(uncompressedReader, data, x, y, tileWidth, tileHeight, header.Width, subencoding - 128);
                    }
                    else
                    {
                        throw new NotImplementedException();
                    }
                }
            }
            return data;
        }

        private async Task ParseRawTile(PipeReader uncompressedReader, byte[] data, int x, int y, int tileWidth, int tileHeight, int rectangleWidth)
        {
            int tx = 0;
            int ty = 0;
            await uncompressedReader.ForEach(3, tileWidth * tileHeight, (mem) =>
            {
                var dataIndex = (((y + ty) * rectangleWidth) + x + tx) * 4;
                data[dataIndex] = mem.Span[0];
                data[dataIndex + 1] = mem.Span[1];
                data[dataIndex + 2] = mem.Span[2];
                data[dataIndex + 3] = 0xff;
                tx += 1;
                if (tx >= tileWidth)
                {
                    tx = 0;
                    ty += 1;
                }
            });
        }

        private async Task ParsePlainRLETile(PipeReader uncompressedReader, byte[] data, int x, int y, int tileWidth, int tileHeight, int rectangleWidth)
        {
            var tileLength = tileWidth * tileHeight;
            var pixel = ArrayPool<byte>.Shared.Rent(4);
            pixel[3] = 0xff;
            int tx = 0;
            int ty = 0;

            while (tileLength > 0)
            {
                var result = await uncompressedReader.ReadAsync();
                int read = 0;
                if (result.Buffer.Length < 4)
                {
                    uncompressedReader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
                    continue;
                }

                while (tileLength > 0)
                {
                    if (result.Buffer.Length - read < 4)
                        break;
                    result.Buffer.Slice(read, 3).CopyTo(pixel);
                    var runLengthSlice = result.Buffer.Slice(read + 3);
                    if (TryParseRunLength(runLengthSlice, out var length, out var byteLength))
                    {
                        tileLength -= length;
                        read += 3 + byteLength;
                        for (int i = 0; i < length; i++)
                        {
                            Buffer.BlockCopy(pixel, 0, data, (
                                    ((y + ty) * rectangleWidth) +   // y + ty rows
                                    (x + tx)                        // x + tx columns
                                ) * 4, 4);                          // 4 bytes per pixel
                            tx += 1;
                            if (tx == tileWidth)
                            {
                                tx = 0;
                                ty++;
                            }
                        }
                    }
                    else
                    {
                        break;
                    }
                }
                uncompressedReader.AdvanceTo(result.Buffer.GetPosition(read));
            }
            Debug.Assert(tileLength == 0);
            ArrayPool<byte>.Shared.Return(pixel);
        }

        private async Task ParsePackedPaletteTile(PipeReader uncompressedReader, byte[] data, int x, int y, int tileWidth, int tileHeight, int rectangleWidth, int paletteSize)
        {
            var bitFieldSize = GetPackedPaletteBitFieldLength(paletteSize, tileWidth, tileHeight);
            var bitFieldEntrySize = GetPackedPaletteBitFieldEntryLength(paletteSize);
            var tileLength = tileWidth * tileHeight;
            var palette = ArrayPool<byte>.Shared.Rent(paletteSize * 3);
            var row = ArrayPool<byte>.Shared.Rent(tileWidth * 4);
            int tx = 0;
            int ty = 0;
            int p = 0;
            await uncompressedReader.ForEach(3, paletteSize, (mem) =>
            {
                palette[p] = mem.Span[0];
                palette[p + 1] = mem.Span[1];
                palette[p + 2] = mem.Span[2];
                p += 3;
            });
            int mask = 1;
            for (int i = 0; i < bitFieldEntrySize - 1; i++)
            {
                mask = (mask << 1) | 1;
            }
            await uncompressedReader.ForEach(1, bitFieldSize, (mem) =>
            {
                int i = 8 - bitFieldEntrySize;
                while (i >= 0)
                {
                    var index = (mem.Span[0] >> i) & ((1 << bitFieldEntrySize) - 1);
                    row[tx * 4] = palette[index * 3];
                    row[(tx * 4) + 1] = palette[(index * 3) + 1];
                    row[(tx * 4) + 2] = palette[(index * 3) + 2];
                    row[(tx * 4) + 3] = 0xff;

                    tx++;
                    mask >>= bitFieldEntrySize;
                    i -= bitFieldEntrySize;
                    if (tx == tileWidth)
                    {
                        Buffer.BlockCopy(row, 0, data, (
                                ((y + ty) * rectangleWidth) +   // y + ty rows
                                x                               // x columns
                            ) * 4, tileWidth * 4);
                        ty++;
                        tx = 0;
                        break;
                    }
                }
            });
            ArrayPool<byte>.Shared.Return(palette);
            ArrayPool<byte>.Shared.Return(row);
        }

        private async Task ParsePaletteRLETile(PipeReader uncompressedReader, byte[] data, int x, int y, int tileWidth, int tileHeight, int rectangleWidth, int paletteSize)
        {
            var palette = ArrayPool<byte>.Shared.Rent(paletteSize * 4);
            var row = ArrayPool<byte>.Shared.Rent(tileWidth * 4);
            int p = 0;
            int tx = 0;
            int ty = 0;
            await uncompressedReader.ForEach(3, paletteSize, (mem) =>
            {
                palette[p] = mem.Span[0];
                palette[p + 1] = mem.Span[1];
                palette[p + 2] = mem.Span[2];
                palette[p + 3] = 0xff;
                p += 4;
            });
            var remainingPixels = tileWidth * tileHeight;
            while (remainingPixels > 0)
            {
                int read = 0;
                var result = await uncompressedReader.ReadAsync();
                while (remainingPixels > 0)
                {
                    if (result.Buffer.Length - read < 1)
                    {
                        break;
                    }
                    var slice = result.Buffer.Slice(read);
                    if (TryParsePaletteRunLength(slice, out var index, out var length, out var byteLength))
                    {
                        remainingPixels -= length;
                        read += byteLength;
                        for (int i = 0; i < length; i++)
                        {
                            Buffer.BlockCopy(palette, index*4, data, (
                                ((y + ty) * rectangleWidth) +   // y + ty rows
                                (x + tx)                        // x + tx columns
                            ) * 4, 4);                          // 4 bytes per pixel
                            tx++;
                            if (tx == tileWidth)
                            {
                                tx = 0;
                                ty++;
                            }
                        }
                    }
                    else
                    {
                        break;
                    }
                }
                uncompressedReader.AdvanceTo(result.Buffer.GetPosition(read));
            }
            Debug.Assert(remainingPixels == 0);
            ArrayPool<byte>.Shared.Return(palette);
        }

        private bool TryParseRunLength(ReadOnlySequence<byte> seq, out int length, out int byteLength)
        {
            length = 1;
            byteLength = 0;
            foreach (var segment in seq)
            {
                for (int i = 0; i < segment.Span.Length; i++)
                {
                    var b = segment.Span[i];
                    length += b;
                    byteLength++;
                    if (b != byte.MaxValue)
                        return true;
                }
            }
            return false;
        }

        private bool TryParsePaletteRunLength(ReadOnlySequence<byte> seq, out int index, out int length, out int byteLength)
        {
            length = 1;
            byteLength = 1;
            index = 0;
            foreach (var segment in seq)
            {
                for (int i = 0; i < segment.Span.Length; i++)
                {
                    var b = segment.Span[i];
                    if (byteLength == 1)
                    {
                        index = b & 127;
                        if ((b & 128) == 0)
                        {
                            return true;
                        }
                    }
                    else
                    {
                        length += b;
                        if (b != byte.MaxValue)
                            return true;
                    }
                    byteLength++;
                }
            }
            return false;
        }

        private int GetPackedPaletteBitFieldEntryLength(int paletteSize)
        {
            if (paletteSize < 2)
                throw new InvalidOperationException();
            if (paletteSize == 2)
                return 1;
            if (paletteSize <= 4)
                return 2;
            if (paletteSize <= 16)
                return 4;
            throw new InvalidDataException();
        }

        private int GetPackedPaletteBitFieldLength(int paletteSize, int tileWidth, int tileHeight)
        {
            if (paletteSize < 2)
                throw new InvalidOperationException();
            if (paletteSize == 2)
                return (tileWidth + 7) / 8 * tileHeight;
            if (paletteSize <= 4)
                return (tileWidth + 3) / 4 * tileHeight;
            if (paletteSize <= 16)
                return (tileWidth + 1) / 2 * tileHeight;
            throw new InvalidDataException();
        }

        private async ValueTask<byte[]> ParseCompressedPixel(PipeReader uncompressedReader)
        {
            var result = await uncompressedReader.ReadMinBytesAsync(3);
            var pixel = ParseCompressedPixel(result);
            uncompressedReader.AdvanceTo(result.Buffer.GetPosition(3));
            return pixel;
        }

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

        private async Task Decompress(PipeWriter writer, ReadOnlyMemory<byte> data)
        {
            var buf = ArrayPool<byte>.Shared.Rent(4096);
            InflateInputStream.Position = 0;
            InflateInputStream.Write(data.Span);
            InflateInputStream.Position = 0;
            InflateInputStream.SetLength(data.Length);
            int read;

            do
            {
                //Memory<byte> memory = writer.GetMemory(data.Length * 2);
                //read = InflateOutputStream.Read(memory.Span);
                //writer.Advance(read);
                read = InflateOutputStream.Read(buf);
                await writer.WriteAsync(buf.AsMemory().Slice(0, read));
            }
            while (read != 0);
            //await writer.FlushAsync();
            ArrayPool<byte>.Shared.Return(buf);
        }
    }
}
