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
            var ZRLEPipe = new Pipe();
            var parser = Task.Run(() => ParsePixelData(ZRLEPipe.Reader, header, format));

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
                        await Decompress(ZRLEPipe.Writer, segment, segment.Length);
                        decompressed += segment.Length;
                        remaining -= segment.Length;
                    }
                    else
                    {
                        //await Decompress(ZRLEPipe.Writer, segment.Slice(0, (int) remaining), (int) remaining);
                        await Decompress(ZRLEPipe.Writer, segment, (int) remaining);
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

        private async Task<byte[]> ParsePixelData(PipeReader uncompressedReader, RfbRectangleHeader header, PixelFormat format)
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
                        await ParseRawTile(uncompressedReader, data, x, y, tileWidth, tileHeight, header.Width, format.BitsPerPixel/8);
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
                    else if (subencoding == 128) // Plain RLE
                    {
                        await ParsePlainRLETile(uncompressedReader, data, x, y, tileWidth, tileHeight, header.Width);
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

        private async Task ParseRawTile(PipeReader uncompressedReader, byte[] data, int x, int y, int tileWidth, int tileHeight, int rectangleWidth, int bytesPerPixel)
        {
            int tx = 0;
            int ty = 0;
            var tileRow = ArrayPool<byte>.Shared.Rent(tileWidth * 4);
            await uncompressedReader.Foreach(3, tileWidth * tileHeight, (mem) =>
            {
                tileRow[4*tx] = mem.Span[0];
                tileRow[(4*tx)+1] = mem.Span[1];
                tileRow[(4*tx)+2] = mem.Span[2];
                tileRow[(4*tx)+3] = 0xff;
                tx += 1;
                if (tx >= tileWidth)
                {
                    tx = 0;
                    Buffer.BlockCopy(tileRow, 0, data, (
                            ((y + ty) * rectangleWidth) +   // y + ty rows
                            (x)                             // x colums
                        ) * 4, tileWidth * 4);              // 4 bytes per pixel
                    ty += 1;
                }
            });
            ArrayPool<byte>.Shared.Return(tileRow);
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
                                    (x + tx)                        // x + tx colums
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

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async ValueTask<int> ParseRunLength(PipeReader uncompressedReader)
        {
            int runLength = 1;
            int read = 0;
            while(true)
            {
                var result = await uncompressedReader.ReadAsync();
                foreach (var segment in result.Buffer)
                {
                    for (int i = 0; i < segment.Length; i++)
                    {
                        read++;
                        var b = segment.Span[i];
                        runLength += b;
                        if (b != byte.MaxValue)
                        {
                            uncompressedReader.AdvanceTo(result.Buffer.GetPosition(read));
                            return runLength;
                        }
                    }
                }
            }
        }

        #region gammel

        private async ValueTask<byte[]> ParseCompressedPixel(PipeReader uncompressedReader)
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

        private async Task Decompress(PipeWriter writer, ReadOnlyMemory<byte> data, int count)
        {
            InflateInputStream.Position = 0;
            InflateInputStream.Write(data.Span.Slice(0, count));
            InflateInputStream.Position = 0;
            InflateInputStream.SetLength(count);
            int read;

            do
            {
                Memory<byte> memory = writer.GetMemory(data.Length * 2);
                read = InflateOutputStream.Read(memory.Span);
                writer.Advance(read);
            }
            while (read != 0);
            await writer.FlushAsync();
        }
    }
}
