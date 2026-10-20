using System;

namespace TCPTunnel
{
    internal sealed class NavigationMode
    {
        private int clicks, arrows;
        internal bool Mouse { get; private set; }
        internal bool Click(bool inside)
        {
            if (!inside) return false;
            arrows = 0;
            if (Mouse) return false;
            if (++clicks < 2) return false;
            Mouse = true; clicks = 0; return true;
        }
        internal bool Arrow()
        {
            clicks = 0;
            if (!Mouse) return false;
            if (++arrows < 2) return false;
            Mouse = false; arrows = 0; return true;
        }
        internal void Wheel() { if (Mouse) arrows = 0; }
    }
}
