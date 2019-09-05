using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace VncDotnet
{
    internal static class PipeReaderExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static async Task<ReadResult> ReadMinBytesAsync(this PipeReader reader, int bytes)
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync();
                if (result.Buffer.Length >= bytes)
                {
                    return result;
                }
                reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static async Task<byte> ReadByteAsync(this PipeReader reader)
        {
            var result = await reader.ReadMinBytesAsync(1);
            var b = result.Buffer.First.Span[0];
            reader.AdvanceTo(result.Buffer.GetPosition(1));
            return b;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static async Task<byte[]> ReadBytesAsync(this PipeReader reader, int bytes)
        {
            var array = new byte[bytes];
            var result = await reader.ReadMinBytesAsync(bytes);
            result.Buffer.ForceGetReadOnlySpan(bytes).Slice(0, bytes).CopyTo(array);
            reader.AdvanceTo(result.Buffer.GetPosition(bytes));
            return array;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static async Task<uint> ReadU32BEAsync(this PipeReader reader)
        {
            var result = await reader.ReadMinBytesAsync(4);
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
