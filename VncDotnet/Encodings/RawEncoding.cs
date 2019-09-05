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
            var remainingPixels = header.Width * header.Height;
            var buf = ArrayPool<byte>.Shared.Rent(remainingPixels * 4);
            var destPos = 0;
            while (remainingPixels > 0)
            {
                var result = await reader.ReadAsync();
                int i;
                for (i = 0; i < result.Buffer.Length - 3 && remainingPixels > 0; i += 4)
                {
                    ParsePixel(result.Buffer.Slice(i, 4), 0, buf, destPos, ref format);
                    destPos += 4;
                    remainingPixels--;
                }
                reader.AdvanceTo(result.Buffer.GetPosition(i));
            }
            return buf;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ParsePixel(ReadOnlySequence<byte> seq, int sourceOffset, byte[] buf, long destOffset, ref PixelFormat format)
        {
            Debug.Assert(format.BitsPerPixel == 32);
            buf[destOffset] = seq.ForceGet(sourceOffset);
            buf[destOffset + 1] = seq.ForceGet(sourceOffset + 1);
            buf[destOffset + 2] = seq.ForceGet(sourceOffset + 2);
            buf[destOffset + 3] = 0xff;
        }
    }
}
