using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VncDotnet
{
    internal static class PipeReaderExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static async Task<ReadResult> ReadMinBytesAsync(this PipeReader reader, int bytes, CancellationToken token)
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync(token);
                if (result.Buffer.Length >= bytes)
                {
                    return result;
                }
                reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
                if (result.IsCompleted)
                    throw new VncConnectionException();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static async Task<byte> ReadByteAsync(this PipeReader reader, CancellationToken token)
        {
            var result = await reader.ReadMinBytesAsync(1, token);
            var b = result.Buffer.First.Span[0];
            reader.AdvanceTo(result.Buffer.GetPosition(1));
            return b;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static async Task<byte[]> ReadBytesAsync(this PipeReader reader, int bytes, CancellationToken token)
        {
            var array = new byte[bytes];
            var result = await reader.ReadMinBytesAsync(bytes, token);
            result.Buffer.ForceGetReadOnlySpan(bytes).Slice(0, bytes).CopyTo(array);
            reader.AdvanceTo(result.Buffer.GetPosition(bytes));
            return array;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static async Task<uint> ReadU32BEAsync(this PipeReader reader, CancellationToken token)
        {
            var result = await reader.ReadMinBytesAsync(4, token);
            var number = BinaryPrimitives.ReadUInt32BigEndian(result.Buffer.ForceGetReadOnlySpan(4));
            reader.AdvanceTo(result.Buffer.GetPosition(4));
            return number;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ReadOnlySpan<byte> ForceGetReadOnlySpan(this ReadOnlySequence<byte> sequence, int offset, int length)
        {
            if (offset + length > sequence.Length)
                throw new InvalidOperationException();

            if (sequence.First.Length >= offset + length)
                return sequence.First.Span;

            var slice = sequence.Slice(offset, length);
            return slice.ToArray();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ReadOnlySpan<byte> ForceGetReadOnlySpan(this ReadOnlySequence<byte> sequence, int length)
        {
            return ForceGetReadOnlySpan(sequence, 0, length);
        }

        public static async Task ForEach(this PipeReader reader, int elementSize, int elementCount, Action<ReadOnlyMemory<byte>> handler)
        {
            while (elementCount > 0)
            {
                var result = await reader.ReadAsync();
                byte[]? remainder = null;
                int read = 0;
                foreach (var segment in result.Buffer)
                {
                    if (elementCount == 0)
                        break;
                    int s = 0;
                    if (remainder != null)
                    {
                        if (remainder.Length + segment.Length < elementSize) // segment + remainder not big enough
                        {
                            var newRemainder = new byte[remainder.Length + segment.Length];
                            remainder.CopyTo(newRemainder, 0);
                            segment.CopyTo(newRemainder.AsMemory(remainder.Length));
                            continue;
                        }
                        else // segment + remainder big enough
                        {
                            var danglingElement = new byte[elementSize];
                            remainder.CopyTo(danglingElement, 0);
                            segment.Span.Slice(0, elementSize - remainder.Length).CopyTo(danglingElement.AsSpan(remainder.Length));
                            handler(danglingElement);
                            elementCount--;
                            read += elementSize;
                            s = elementSize - remainder.Length;
                            remainder = null;
                        }
                    }
                    while (elementCount > 0)
                    {
                        if (s + elementSize <= segment.Length) // segment big enough
                        {
                            handler(segment.Slice(s, elementSize));
                            elementCount--;
                            read += elementSize;
                            s += elementSize;
                        }
                        else // segment not big enough
                        {
                            remainder = segment.Slice(s).ToArray();
                            break;
                        }
                    }
                }
                reader.AdvanceTo(result.Buffer.GetPosition(read));
                if (elementCount > 0 && result.IsCompleted)
                    throw new VncConnectionException();
            }
        }
    }

    internal static class ReadOnlySequenceExcensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte ForceGet(this ReadOnlySequence<byte> seq, int index)
        {
            foreach (var segment in seq)
            {
                if (segment.Length > index)
                    return segment.Span[index];
                index -= segment.Length;
            }
            throw new InvalidOperationException();
        }
    }

    internal class Utils
    {
        internal static byte[] EncryptChallenge(string password, byte[] challenge)
        {
            using var des = new DESCryptoServiceProvider()
            {
                Padding = PaddingMode.None,
                Mode = CipherMode.ECB
            };
            var key = new byte[8];
            // https://www.cl.cam.ac.uk/~am21/hakmemc.html
            Encoding.ASCII.GetBytes(password).Take(8).Select(b => (byte)((b * 0x0202020202 & 0x010884422010) % 1023)).ToArray().CopyTo(key, 0);
            using var enc = des.CreateEncryptor(key, null);
            var response = new byte[16];
            enc.TransformBlock(challenge, 0, challenge.Length, response, 0);
            return response;
        }
    }
}
