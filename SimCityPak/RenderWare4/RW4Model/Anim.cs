using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using Gibbed.Spore.Helpers;
using System.Diagnostics;
using SimCityPak;
using System.Globalization;

namespace SporeMaster.RenderWare4
{
    /// <summary>
    /// A keyframe-animation section (0x70001). One channel per animated joint; each channel
    /// holds time-stamped TRS keyframes. SimCity uses the "LocRot" pose format (0x101: rotation
    /// + translation, no scale), Spore the "LocRotScale" (0x601). Parsing ported from the
    /// SporeModder Blender add-on (rw4_base.KeyframeAnim). Read fills <see cref="channels"/>;
    /// Write round-trips it so models that carry animations still save correctly.
    /// </summary>
    class Anim : RW4Object
    {
        public const int type_code = 0x70001;

        public struct Key
        {
            public float qx, qy, qz, qw;   // rotation quaternion
            public float tx, ty, tz;       // translation
            public float sx, sy, sz;       // scale (1 when the format carries no scale)
            public float time;             // seconds
        }

        public class Channel
        {
            public uint id;                // joint name fnv (matches HierarchyInfo item)
            public uint components;        // 0x601 LocRotScale | 0x101 LocRot | 0x100 BlendFactor
            public uint poseSize;          // bytes per keyframe as stored
            public List<Key> keys = new List<Key>();
        }

        public uint skeleton_id;
        public float length;               // seconds
        public uint flags;
        public uint field_C, field_1C, field_24;
        public Channel[] channels;

        // legacy accessors some callers may expect
        public uint[] channel_names { get { return channels == null ? null : channels.Select(c => c.id).ToArray(); } }

        private static int PoseSizeFor(uint components)
        {
            switch (components)
            {
                case 0x601: return 48;   // rot4 + loc3 + scale3 + pad + time
                case 0x101: return 36;   // rot4 + loc3 + time
                case 0x100: return 8;    // factor + time
                default: return 0;
            }
        }

        public override void Read(RW4Model m, RW4Section s, Stream r)
        {
            if (s.type_code != type_code) throw new ModelFormatException(r, "AN000", s.type_code);
            long basePos = s.Pos;

            uint pChannelNames = r.ReadU32();
            uint channelCount = r.ReadU32();
            skeleton_id = r.ReadU32();
            field_C = r.ReadU32();
            uint pChannelData = r.ReadU32();
            uint pPaddingEnd = r.ReadU32();
            r.ReadU32();                      // channelCount again
            field_1C = r.ReadU32();
            length = r.ReadF32();
            field_24 = r.ReadU32();
            flags = r.ReadU32();
            uint pChannelInfo = r.ReadU32();

            channels = new Channel[channelCount];
            for (int i = 0; i < channelCount; i++) channels[i] = new Channel();

            // p_channel_names / p_channel_info are absolute stream offsets; per-channel keyframe
            // positions (read below) are relative to the section start (basePos).
            r.Seek(pChannelNames, SeekOrigin.Begin);
            for (int i = 0; i < channelCount; i++) channels[i].id = r.ReadU32();

            var positions = new uint[channelCount];
            r.Seek(pChannelInfo, SeekOrigin.Begin);
            for (int i = 0; i < channelCount; i++)
            {
                positions[i] = r.ReadU32();
                channels[i].poseSize = r.ReadU32();
                channels[i].components = r.ReadU32();
            }

            // All but the last channel: keyframe count from the gap to the next channel.
            for (int i = 0; i < channelCount - 1; i++)
            {
                uint stride = channels[i].poseSize != 0 ? channels[i].poseSize : (uint)Math.Max(1, PoseSizeFor(channels[i].components));
                int count = (int)((positions[i + 1] - positions[i]) / stride);
                r.Seek(basePos + positions[i], SeekOrigin.Begin);
                for (int k = 0; k < count; k++) channels[i].keys.Add(ReadKey(r, channels[i].components));
            }
            // Last channel: read until the timestamp stops increasing.
            if (channelCount > 0)
            {
                Channel last = channels[channelCount - 1];
                r.Seek(basePos + positions[channelCount - 1], SeekOrigin.Begin);
                float lastTime = -1f;
                long limit = s.Pos + s.Size;
                while (r.Position + PoseSizeFor(last.components) <= limit)
                {
                    Key k = ReadKey(r, last.components);
                    if (k.time < lastTime) break;
                    lastTime = k.time;
                    last.keys.Add(k);
                    if (k.time >= length) break;
                }
            }
            // Leave the stream at the section end so the loader's size check passes.
            r.Seek(s.Pos + s.Size, SeekOrigin.Begin);
        }

        private static Key ReadKey(Stream r, uint components)
        {
            Key k = new Key { sx = 1, sy = 1, sz = 1 };
            if (components == 0x100)   // blend factor: not a transform; keep identity, store time
            {
                r.ReadF32();           // factor (unused for glTF transform tracks)
                k.time = r.ReadF32();
                return k;
            }
            k.qx = r.ReadF32(); k.qy = r.ReadF32(); k.qz = r.ReadF32(); k.qw = r.ReadF32();
            k.tx = r.ReadF32(); k.ty = r.ReadF32(); k.tz = r.ReadF32();
            if (components == 0x601)
            {
                k.sx = r.ReadF32(); k.sy = r.ReadF32(); k.sz = r.ReadF32();
                r.ReadU32();           // padding
            }
            k.time = r.ReadF32();
            return k;
        }

        public override void Write(RW4Model m, RW4Section s, Stream w)
        {
            long basePos = w.Position;
            uint channelCount = (uint)channels.Length;

            // header (12 uints); offsets are relative to basePos.
            uint pChannelNames = 12 * 4;
            uint pChannelInfo = pChannelNames + channelCount * 4;
            uint pChannelData = pChannelInfo + channelCount * 12;

            uint dataLen = 0;
            for (int i = 0; i < channelCount; i++) dataLen += (uint)(channels[i].keys.Count * PoseSizeFor(channels[i].components));
            uint pPaddingEnd = pChannelData + dataLen;

            w.WriteU32(pChannelNames);
            w.WriteU32(channelCount);
            w.WriteU32(skeleton_id);
            w.WriteU32(field_C);
            w.WriteU32(pChannelData);
            w.WriteU32(pPaddingEnd);
            w.WriteU32(channelCount);
            w.WriteU32(field_1C);
            w.WriteF32(length);
            w.WriteU32(field_24);
            w.WriteU32(flags);
            w.WriteU32(pChannelInfo);

            foreach (Channel c in channels) w.WriteU32(c.id);

            uint pos = pChannelData;
            foreach (Channel c in channels)
            {
                w.WriteU32(pos);
                w.WriteU32((uint)PoseSizeFor(c.components));
                w.WriteU32(c.components);
                pos += (uint)(c.keys.Count * PoseSizeFor(c.components));
            }

            foreach (Channel c in channels)
                foreach (Key k in c.keys) WriteKey(w, c.components, k);
        }

        private static void WriteKey(Stream w, uint components, Key k)
        {
            if (components == 0x100) { w.WriteF32(0); w.WriteF32(k.time); return; }
            w.WriteF32(k.qx); w.WriteF32(k.qy); w.WriteF32(k.qz); w.WriteF32(k.qw);
            w.WriteF32(k.tx); w.WriteF32(k.ty); w.WriteF32(k.tz);
            if (components == 0x601) { w.WriteF32(k.sx); w.WriteF32(k.sy); w.WriteF32(k.sz); w.WriteU32(0); }
            w.WriteF32(k.time);
        }

        public override int ComputeSize()
        {
            // Match the on-disk layout produced by Write (the loader verifies this).
            uint channelCount = (uint)channels.Length;
            int size = 12 * 4 + (int)channelCount * 4 + (int)channelCount * 12;
            foreach (Channel c in channels) size += c.keys.Count * PoseSizeFor(c.components);
            return size;
        }
    };
}
