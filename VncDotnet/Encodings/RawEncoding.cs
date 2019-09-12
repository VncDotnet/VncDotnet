using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VncDotnet;
using VncDotnet.Messages;

namespace VncDotnet.Encodings
{
    class RawEncoding
    {
        public async Task<byte[]> ParseRectangle(PipeReader reader, RfbRectangleHeader header, PixelFormat format, CancellationToken token)
        {
            Debug.Assert(format.BitsPerPixel == 32);
            var remainingPixels = header.Width * header.Height;
            var buf = ArrayPool<byte>.Shared.Rent(remainingPixels * 4);
            var destPos = 0;
            while (remainingPixels > 0)
            {
                var result = await reader.ReadAsync(token);
                byte[]? remainder = null;
                int read = 0;
                foreach (var segment in result.Buffer)
                {
                    if (remainingPixels == 0)
                        break;
                    int s = 0;
                    if (remainder != null)
                    {
                        if (remainder.Length + segment.Length < 4) // segment + remainder not big enough
                        {
                            var newRemainder = new byte[remainder.Length + segment.Length];
                            remainder.CopyTo(newRemainder, 0);
                            segment.CopyTo(newRemainder.AsMemory(remainder.Length));
                            continue;
                        }
                        else // segment + remainder big enough
                        {
                            var completedReminder = new byte[4];
                            remainder.CopyTo(completedReminder, 0);
                            segment.Span.Slice(0, 3 - remainder.Length).CopyTo(completedReminder.AsSpan(remainder.Length));
                            completedReminder.CopyTo(buf, destPos);
                            buf[destPos + 3] = 0xff;
                            remainingPixels--;
                            read += 4;
                            destPos += 4;
                            s = 4 - remainder.Length;
                            remainder = null;
                        }
                    }
                    while (remainingPixels > 0)
                    {
                        if (s + 4 <= segment.Length) // segment big enough
                        {
                            segment.Slice(s, 3).CopyTo(buf.AsMemory(destPos));
                            buf[destPos + 3] = 0xff;
                            remainingPixels--;
                            read += 4;
                            destPos += 4;
                            s += 4;
                        }
                        else if (s + 4 > segment.Length) // segment not big enough
                        {
                            remainder = segment.Slice(s).ToArray();
                            break;
                        }
                    }
                }
                reader.AdvanceTo(result.Buffer.GetPosition(read));
            }
            return buf;
        }
    }
}
