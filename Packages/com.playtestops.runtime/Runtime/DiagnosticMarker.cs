using UnityEngine;

namespace PlaytestOps.RuntimeStreaming
{
    internal static class DiagnosticMarker
    {
        // Protocol v1: 64x64 native-pixel barcode with a magenta border and 16-bit checksum.
        internal static void Draw(uint id)
        {
            if (Event.current.type != EventType.Repaint) return;
            var oldColor = GUI.color;
            var oldMatrix = GUI.matrix;
            var oldDepth = GUI.depth;
            GUI.matrix = Matrix4x4.identity;
            GUI.depth = int.MinValue;
            Paint(new Rect(4, 4, 72, 72), Color.magenta);
            var bit = 0;
            var checksum = (id ^ (id >> 16) ^ 0xA5A5u) & 0xFFFFu;
            for (var i = 0; i < 64; i++)
            {
                bool white;
                if (i == 0 || i == 63) white = true;
                else if (i == 7 || i == 56) white = false;
                else
                {
                    white = bit < 32 ? ((id >> bit) & 1u) != 0
                        : bit < 48 ? ((checksum >> (bit - 32)) & 1u) != 0 : i % 2 != 0;
                    bit++;
                }
                Paint(new Rect(8 + i % 8 * 8, 8 + i / 8 * 8, 8, 8), white ? Color.white : Color.black);
            }
            GUI.color = oldColor;
            GUI.matrix = oldMatrix;
            GUI.depth = oldDepth;
        }

        private static void Paint(Rect rect, Color color)
        {
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
        }
    }
}
