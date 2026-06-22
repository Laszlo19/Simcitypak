using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using Gibbed.Spore.Helpers;
using System.Diagnostics;
using SimCityPak;
using System.Globalization;
using Microsoft.Xna.Framework;

namespace SporeMaster.RenderWare4
{
    public struct Vertex : IRW4Struct
    {
        //minimum size = 40
        private uint size;

        public uint VertexSize
        {
            get { return size; }
            set { size = value; }
        }

        public List<IVertexComponentValue> VertexComponents { get; set; }


        public VertexFloat3Value Position
        {
            get
            {
                return (VertexFloat3Value)VertexComponents.First(v => v.Usage == D3DDECLUSAGE.D3DDECLUSAGE_POSITION && v.DeclarationType == D3DDECLTYPE.D3DDECLTYPE_FLOAT3);

            }
        }

        public int Element { get; set; }

        public VertexFloat4Value TextureCoordinates
        {
            get
            {
                return (VertexFloat4Value)VertexComponents.First(v => v.Usage == D3DDECLUSAGE.D3DDECLUSAGE_TEXCOORD && v.DeclarationType == D3DDECLTYPE.D3DDECLTYPE_FLOAT4);
            }
        }

        /// <summary>True when this vertex carries usable texture coordinates (FLOAT2 — the
        /// normal per-vertex UV on most models — or FLOAT4, used by the facade buildings). Some
        /// meshes have none (shadow/collision); used to avoid throwing on export.</summary>
        public bool HasTextureCoordinates
        {
            get
            {
                return VertexComponents != null && VertexComponents.Any(
                    v => v.Usage == D3DDECLUSAGE.D3DDECLUSAGE_TEXCOORD &&
                         (v.DeclarationType == D3DDECLTYPE.D3DDECLTYPE_FLOAT2 || v.DeclarationType == D3DDECLTYPE.D3DDECLTYPE_FLOAT4));
            }
        }

        /// <summary>Best UV for export: the first FLOAT2 TEXCOORD (the real per-vertex UV on
        /// vehicles/props/characters), else the first FLOAT4 TEXCOORD (facade buildings store a
        /// large tiling/world coordinate here). Returns false if the vertex has no usable UV.</summary>
        public bool TryGetUV(out float u, out float v)
        {
            u = 0; v = 0;
            if (VertexComponents == null) return false;
            foreach (var c in VertexComponents)
            {
                if (c.Usage != D3DDECLUSAGE.D3DDECLUSAGE_TEXCOORD) continue;
                var f2 = c as VertexFloat2Value;
                if (f2 != null) { u = f2.X; v = f2.Y; return true; }
            }
            foreach (var c in VertexComponents)
            {
                if (c.Usage != D3DDECLUSAGE.D3DDECLUSAGE_TEXCOORD) continue;
                var f4 = c as VertexFloat4Value;
                if (f4 != null) { u = f4.X; v = f4.Y; return true; }
            }
            return false;
        }

        public Vector3 Normal
        {
            get
            {
                Vector3 vector = new Vector3();
                IVertexComponentValue component = VertexComponents.FirstOrDefault(v => v.Usage == D3DDECLUSAGE.D3DDECLUSAGE_NORMAL);
                if (component == null) return vector;
                if (component.DeclarationType == D3DDECLTYPE.D3DDECLTYPE_UBYTE4)
                {
                    VertexUByte4Value byteValue = component as VertexUByte4Value;
                    vector = new Vector3(
                                         (((float)byteValue.X) - 127.5f) / 127.5f,
                                         (((float)byteValue.Y) - 127.5f) / 127.5f,
                                         (((float)byteValue.Z) - 127.5f) / 127.5f
                                       );
                }
                return vector;
            }
        }

        public uint PackedBoneIndices { get; set; }
        public uint PackedBoneWeights { get; set; }

        public byte[] additionalData;

        public int AdditionalDataSize
        {
            get
            {
                if (additionalData != null)
                {
                    return additionalData.Length;
                }
                return 0;
            }
        }

        public uint Size() { return size; }

        public void Read(Stream r)
        {
            throw new Exception("Use Read(Stream r, VertexFormat vFormat) instead for Vertices");
        }

        public void Read(Stream r, VertexFormat vFormat)
        {
            VertexComponents = new List<IVertexComponentValue>();

            size = vFormat.VertexSize;
            long pos = r.Position;

            foreach (VertexUsage usage in vFormat.VertexElements.OrderBy(vu => vu.Offset))
            {
                r.Seek(pos + usage.Offset, SeekOrigin.Begin);


                IVertexComponentValue componentValue = VertexComponentValueFactory.CreateComponent(usage.DeclarationType);
                if (componentValue != null)
                {
                    componentValue.Usage = usage.Usage;
                    componentValue.Read(r);

                    VertexComponents.Add(componentValue);
                }
            }

            r.Seek(pos + size, SeekOrigin.Begin);
        }

        public void SetSize(uint _size)
        {
            this.size = _size;
        }

        public void Write(Stream w)
        {
            foreach (IVertexComponentValue value in this.VertexComponents)
            {
                value.Write(w);
            }
        }
        public void Read4(Stream r)
        {
            //X = r.ReadF32(); Y = r.ReadF32(); Z = r.ReadF32(); r.expect(0, "4V001");
        }
        public void Write4(Stream w)
        {
            //w.WriteF32(X); w.WriteF32(Y); w.WriteF32(Z); w.WriteU32(0);
        }

        public static UInt32 PackNormal(float x, float y, float z)
        {
            float invl = 127.5F;
            var xb = (byte)(x * invl + 127.5);
            var yb = (byte)(y * invl + 127.5);
            var zb = (byte)(z * invl + 127.5);
            return ((UInt32)xb) + ((UInt32)yb << 8) + ((UInt32)zb << 16) + ((UInt32)0x01 << 24);
        }

        public static UInt32 PackNormal(float x, float y, float z, float w)
        {
            float invl = 127.5F;
            var xb = (byte)(x * invl + 127.5);
            var yb = (byte)(y * invl + 127.5);
            var zb = (byte)(z * invl + 127.5);
            var wb = (byte)(w * invl + 127.5);
            return ((UInt32)xb) + ((UInt32)yb << 8) + ((UInt32)zb << 16) + ((UInt32)0x01 << 24);
        }

        public static float UnpackNormal(UInt32 packed, int dim)
        {
            byte b = (byte)((packed >> (dim * 8)) & 0xff);
            return (((float)b) - 127.5f) / 127.5f;
        }


    };
}
