using System;
using System.Collections.Generic;
using System.Text;

namespace VncDotnet.Messages
{
    public class ServerInitMessage
    {
        public ushort FramebufferWidth { get; set; }
        public ushort FramebufferHeight { get; set; }
        public PixelFormat PixelFormat { get; set; }
        public string Name { get; set; }

        public override string ToString()
        {
            return $"{FramebufferWidth}x{FramebufferHeight}\n{PixelFormat}\n{Name}";
        }

        public ServerInitMessage(ushort framebufferWith, ushort framebufferHeight, PixelFormat pixelFormat, string name)
        {
            FramebufferWidth = framebufferWith;
            FramebufferHeight = framebufferHeight;
            PixelFormat = pixelFormat;
            Name = name;
        }
    }

    public class PixelFormat
    {
        public byte BitsPerPixel { get; set; }
        public byte Depth { get; set; }
        public byte BigEndianFlag { get; set; }
        public byte TrueColorFlag { get; set; }
        public ushort RedMax { get; set; }
        public ushort GreenMax { get; set; }
        public ushort BlueMax { get; set; }
        public byte RedShift { get; set; }
        public byte GreenShift { get; set; }
        public byte BlueShift { get; set; }

        public override string ToString()
        {
            return $"bpp={BitsPerPixel} depth={Depth} bigendian={BigEndianFlag} truecolor={TrueColorFlag} rmax={RedMax} gmax={GreenMax}" +
                $"bmax={BlueMax} rshift={RedShift} gshift={GreenShift} bshift={BlueShift}";
        }
    }
}
