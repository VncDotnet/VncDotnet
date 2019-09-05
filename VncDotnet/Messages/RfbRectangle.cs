using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VncDotnet;

namespace VncDotnet.Messages
{
    public struct RfbRectangleHeader
    {
        public ushort X { get; set; }
        public ushort Y { get; set; }
        public ushort Width { get; set; }
        public ushort Height { get; set; }
        public RfbEncoding Encoding { get; set; }

        public RfbRectangleHeader(ushort x, ushort y, ushort width, ushort height, int encoding)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
            Encoding = (RfbEncoding) encoding;
        }

        public override string ToString()
        {
            return $"[{X}, {Y}, {Width}, {Height}]";
        }
    }
}
