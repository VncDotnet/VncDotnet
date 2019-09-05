using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using VncDotnet;
using VncDotnet.Messages;

namespace VncDotnet.Encodings
{
    class RawEncoding
    {
        public async Task<byte[]> ParseRectangle(PipeReader reader, RfbRectangleHeader header, PixelFormat format)
        {
            Debug.Assert(format.BitsPerPixel == 32);
            var remainingPixels = header.Width * header.Height;
            var buf = ArrayPool<byte>.Shared.Rent(remainingPixels * 4);
            var destPos = 0;
            while (remainingPixels > 0)
            {
                var result = await reader.ReadAsync();
                byte[]? remainder = null;
                int read = 0;
                foreach (var segment in result.Buffer)
                {
                    if (remainingPixels == 0)
                        break;
                    int i = 0;
                    if (remainder != null)
                    {
                        remainder.CopyTo(buf.AsMemory(destPos));
                        segment.Slice(0, 4 - remainder.Length).CopyTo(buf.AsMemory(destPos + remainder.Length));
                        read += 4;
                        destPos += 4;
                        i = 4 - remainder.Length;
                        remainder = null;
                    }
                    for (; i < segment.Length && remainingPixels > 0; i += 4)
                    {
                        segment.Slice(i, 3).CopyTo(buf.AsMemory(destPos));
                        buf[destPos + 3] = 0xff;
                        remainingPixels--;
                        read += 4;
                        destPos += 4;
                    }
                    if (i > segment.Length && remainingPixels > 0)
                    {
                        remainder = segment.Slice(i).ToArray();
                    }
                }
                reader.AdvanceTo(result.Buffer.GetPosition(read));
            }
            return buf;
        }
    }
}
