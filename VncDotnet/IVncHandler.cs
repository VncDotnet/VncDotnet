using System;
using System.Collections.Generic;
using System.Text;
using VncDotnet.Messages;

namespace VncDotnet
{
    public interface IVncHandler
    {
        void HandleFramebufferUpdate(IEnumerable<(RfbRectangleHeader, byte[])> rectangles);
        void HandleResolutionUpdate(int framebufferWidth, int framebufferHeight);
    }
}
