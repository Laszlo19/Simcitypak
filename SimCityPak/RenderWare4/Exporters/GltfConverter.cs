using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;

namespace SporeMaster.RenderWare4
{
    /// <summary>
    /// Exports an RW4 mesh to a self-contained binary glTF 2.0 file (.glb):
    /// positions + normals + texture coordinates + triangle indices, plus the
    /// model's textures as a PBR material — base color, normal map, and a
    /// specular map (KHR_materials_specular). The maps are supplied by an optional
    /// resolver callback (which looks up the model's RW4Material texture references
    /// across packages); if no resolver is given or it returns no base color, a
    /// heuristic picks the largest non-normal-map texture embedded in the model.
    ///
    /// Still TODO: skeleton and animation (see HANDOFF.md).
    /// </summary>
    public class GltfConverter : IConverter
    {
        /// <summary>The set of PBR maps for a mesh, as PNG bytes (any may be null).</summary>
        public class MaterialTextures
        {
            public byte[] BaseColor;   // -> pbrMetallicRoughness.baseColorTexture
            public byte[] Normal;      // -> material.normalTexture (glTF convention, flat = 128,128,255)
            public byte[] Specular;    // -> KHR_materials_specular.specularColorTexture (RGB)
        }

        /// <summary>
        /// Optional callback that returns the texture maps for a mesh (or null). When set it
        /// takes priority over the internal-texture heuristic; it lets the caller resolve the
        /// model's RW4Material texture references against other loaded packages (the real
        /// SimCity textures live outside the model).
        /// </summary>
        private readonly Func<RW4Mesh, MaterialTextures> _textureProvider;

        public GltfConverter() { }
        public GltfConverter(Func<RW4Mesh, MaterialTextures> textureProvider) { _textureProvider = textureProvider; }

        private const uint GLB_MAGIC = 0x46546C67;   // "glTF"
        private const uint GLB_VERSION = 2;
        private const uint CHUNK_JSON = 0x4E4F534A;  // "JSON"
        private const uint CHUNK_BIN = 0x004E4942;   // "BIN\0"
        private const int COMP_FLOAT = 5126;
        private const int COMP_UINT = 5125;
        private const int TARGET_ARRAY = 34962;
        private const int TARGET_ELEMENT = 34963;
        private const int MODE_TRIANGLES = 4;

        public void Export(RW4Mesh mesh, string fileName)
        {
            // Some RW4 mesh sections (e.g. blend-shape meshes) carry no vertex/index
            // buffer; there's no static geometry to write.
            if (mesh == null || mesh.vertices == null || mesh.vertices.vertices == null
                || mesh.triangles == null || mesh.triangles.triangles == null)
                throw new InvalidOperationException("mesh has no vertex/index buffer");

            var verts = mesh.vertices.vertices;
            var tris = mesh.triangles.triangles;
            int vCount = verts.Length;

            // Prefer the caller's resolver (RW4Material -> external texture resources);
            // fall back to the internal-texture heuristic for the base color.
            MaterialTextures tex = null;
            if (_textureProvider != null) { try { tex = _textureProvider(mesh); } catch { tex = null; } }
            byte[] basePng = tex != null ? tex.BaseColor : null;
            if (basePng == null) basePng = TryGetDiffusePng(mesh);   // null if no usable texture
            byte[] normalPng = tex != null ? tex.Normal : null;
            byte[] specPng = tex != null ? tex.Specular : null;

            using (var bin = new MemoryStream())
            using (var bw = new BinaryWriter(bin))
            {
                float[] min = { float.MaxValue, float.MaxValue, float.MaxValue };
                float[] max = { float.MinValue, float.MinValue, float.MinValue };
                foreach (Vertex v in verts)
                {
                    float x = v.Position.X, y = v.Position.Y, z = v.Position.Z;
                    bw.Write(x); bw.Write(y); bw.Write(z);
                    if (x < min[0]) min[0] = x; if (y < min[1]) min[1] = y; if (z < min[2]) min[2] = z;
                    if (x > max[0]) max[0] = x; if (y > max[1]) max[1] = y; if (z > max[2]) max[2] = z;
                }
                int posOffset = 0, posLen = vCount * 12;

                int normOffset = (int)bin.Position;
                foreach (Vertex v in verts) { bw.Write(v.Normal.X); bw.Write(v.Normal.Y); bw.Write(v.Normal.Z); }
                int normLen = vCount * 12;

                int uvOffset = (int)bin.Position;
                foreach (Vertex v in verts) { bw.Write(v.TextureCoordinates.X); bw.Write(v.TextureCoordinates.Y); }
                int uvLen = vCount * 8;

                int idxOffset = (int)bin.Position;
                int idxCount = 0;
                foreach (Triangle t in tris)
                {
                    if (t.i == t.j || t.j == t.k || t.i == t.k) continue;
                    bw.Write((uint)t.i); bw.Write((uint)t.j); bw.Write((uint)t.k);
                    idxCount += 3;
                }
                int idxLen = idxCount * 4;

                // Optional embedded images (PNG), each 4-byte aligned. Records (offset,len)
                // per map so BuildJson can wire base color / normal / specular textures.
                var imgSpans = new List<int[]>();   // [offset, length] in bin
                Func<byte[], int> addImage = data =>
                {
                    if (data == null) return -1;
                    Pad4(bw, bin);
                    int off = (int)bin.Position;
                    bw.Write(data);
                    imgSpans.Add(new[] { off, data.Length });
                    return imgSpans.Count - 1;        // image index
                };
                int baseImg = addImage(basePng);
                int normalImg = addImage(normalPng);
                int specImg = addImage(specPng);

                bw.Flush();
                byte[] binData = bin.ToArray();

                string json = BuildJson(vCount, idxCount, posOffset, posLen, normOffset, normLen,
                    uvOffset, uvLen, idxOffset, idxLen, imgSpans, baseImg, normalImg, specImg,
                    binData.Length, min, max);

                WriteGlb(fileName, json, binData);
            }
        }

        private static void Pad4(BinaryWriter bw, Stream s)
        {
            int pad = (int)((4 - (s.Position % 4)) % 4);
            for (int i = 0; i < pad; i++) bw.Write((byte)0);
        }

        private static string BuildJson(int vCount, int idxCount,
            int posOffset, int posLen, int normOffset, int normLen,
            int uvOffset, int uvLen, int idxOffset, int idxLen,
            List<int[]> imgSpans, int baseImg, int normalImg, int specImg,
            int bufferLength, float[] min, float[] max)
        {
            int nImages = imgSpans.Count;
            bool hasMaterial = nImages > 0;

            var bufferViews = new List<object>
            {
                new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = posOffset,  ["byteLength"] = posLen,  ["target"] = TARGET_ARRAY },
                new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = normOffset, ["byteLength"] = normLen, ["target"] = TARGET_ARRAY },
                new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = uvOffset,   ["byteLength"] = uvLen,   ["target"] = TARGET_ARRAY },
                new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = idxOffset,  ["byteLength"] = idxLen,  ["target"] = TARGET_ELEMENT }
            };
            // one bufferView + image + texture per embedded PNG; texture index == image index
            var images = new List<object>();
            var textures = new List<object>();
            for (int i = 0; i < nImages; i++)
            {
                int bvIndex = bufferViews.Count;
                bufferViews.Add(new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = imgSpans[i][0], ["byteLength"] = imgSpans[i][1] });
                images.Add(new Dictionary<string, object> { ["bufferView"] = bvIndex, ["mimeType"] = "image/png" });
                textures.Add(new Dictionary<string, object> { ["sampler"] = 0, ["source"] = i });
            }

            var primitive = new Dictionary<string, object>
            {
                ["attributes"] = new Dictionary<string, object> { ["POSITION"] = 0, ["NORMAL"] = 1, ["TEXCOORD_0"] = 2 },
                ["indices"] = 3,
                ["mode"] = MODE_TRIANGLES
            };
            if (hasMaterial) primitive["material"] = 0;

            var gltf = new Dictionary<string, object>
            {
                ["asset"] = new Dictionary<string, object> { ["version"] = "2.0", ["generator"] = "SimCityPak glTF exporter" },
                ["scene"] = 0,
                ["scenes"] = new object[] { new Dictionary<string, object> { ["nodes"] = new object[] { 0 } } },
                // Rotate -90° about X so RW4's Z-up geometry stands upright in glTF's
                // Y-up world (otherwise models lie on their side). Quaternion [x,y,z,w].
                ["nodes"] = new object[] { new Dictionary<string, object>
                    {
                        ["mesh"] = 0,
                        ["rotation"] = new object[] { -0.70710678, 0.0, 0.0, 0.70710678 }
                    } },
                ["meshes"] = new object[] { new Dictionary<string, object> { ["primitives"] = new object[] { primitive } } },
                ["buffers"] = new object[] { new Dictionary<string, object> { ["byteLength"] = bufferLength } },
                ["bufferViews"] = bufferViews.ToArray(),
                ["accessors"] = new object[]
                {
                    new Dictionary<string, object> { ["bufferView"] = 0, ["componentType"] = COMP_FLOAT, ["count"] = vCount, ["type"] = "VEC3",
                        ["min"] = new object[] { min[0], min[1], min[2] }, ["max"] = new object[] { max[0], max[1], max[2] } },
                    new Dictionary<string, object> { ["bufferView"] = 1, ["componentType"] = COMP_FLOAT, ["count"] = vCount, ["type"] = "VEC3" },
                    new Dictionary<string, object> { ["bufferView"] = 2, ["componentType"] = COMP_FLOAT, ["count"] = vCount, ["type"] = "VEC2" },
                    new Dictionary<string, object> { ["bufferView"] = 3, ["componentType"] = COMP_UINT,  ["count"] = idxCount, ["type"] = "SCALAR" }
                }
            };

            if (hasMaterial)
            {
                gltf["images"] = images.ToArray();
                gltf["samplers"] = new object[] { new Dictionary<string, object>() };
                gltf["textures"] = textures.ToArray();

                var pbr = new Dictionary<string, object>
                {
                    ["metallicFactor"] = 0.0,
                    ["roughnessFactor"] = 1.0
                };
                if (baseImg >= 0) pbr["baseColorTexture"] = new Dictionary<string, object> { ["index"] = baseImg };

                var material = new Dictionary<string, object> { ["pbrMetallicRoughness"] = pbr };
                if (normalImg >= 0)
                    material["normalTexture"] = new Dictionary<string, object> { ["index"] = normalImg };
                if (specImg >= 0)
                {
                    // KHR_materials_specular: a full-strength specular tinted by the spec texture.
                    material["extensions"] = new Dictionary<string, object>
                    {
                        ["KHR_materials_specular"] = new Dictionary<string, object>
                        {
                            ["specularColorTexture"] = new Dictionary<string, object> { ["index"] = specImg }
                        }
                    };
                }

                gltf["materials"] = new object[] { material };
                if (specImg >= 0)
                    gltf["extensionsUsed"] = new object[] { "KHR_materials_specular" };
            }

            return JsonConvert.SerializeObject(gltf);
        }

        private static void WriteGlb(string fileName, string json, byte[] binData)
        {
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            int jsonPad = (4 - (jsonBytes.Length % 4)) % 4;
            int binPad = (4 - (binData.Length % 4)) % 4;
            int totalLength = 12 + 8 + jsonBytes.Length + jsonPad + 8 + binData.Length + binPad;

            using (var fs = File.Create(fileName))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(GLB_MAGIC); w.Write(GLB_VERSION); w.Write((uint)totalLength);
                w.Write((uint)(jsonBytes.Length + jsonPad)); w.Write(CHUNK_JSON);
                w.Write(jsonBytes);
                for (int i = 0; i < jsonPad; i++) w.Write((byte)0x20);
                w.Write((uint)(binData.Length + binPad)); w.Write(CHUNK_BIN);
                w.Write(binData);
                for (int i = 0; i < binPad; i++) w.Write((byte)0x00);
            }
        }

        // ----- texture decode -----------------------------------------------

        /// <summary>
        /// Finds the model's primary block-compressed texture and returns it as PNG bytes,
        /// or null if there's no usable texture. Never throws.
        /// </summary>
        private static byte[] TryGetDiffusePng(RW4Mesh mesh)
        {
            try
            {
                if (mesh.model == null) return null;

                // Collect this model's block-compressed textures, largest first.
                // (SimCity RW4 models are single-mesh; RW4Mesh carries no material
                // link, and the MeshMaterialAssignment chain is disabled in the
                // parser, so we choose the diffuse heuristically.)
                var candidates = new List<Texture>();
                foreach (RW4Section s in mesh.model.Sections)
                {
                    if (s.TypeCode == SectionTypeCodes.Texture && s.obj is Texture)
                    {
                        Texture t = (Texture)s.obj;
                        if (t.textureType == Texture.DXT1 || t.textureType == Texture.DXT5)
                            candidates.Add(t);
                    }
                }
                if (candidates.Count == 0) return null;
                candidates.Sort((a, b) => ((long)b.width * b.height).CompareTo((long)a.width * a.height));

                // Prefer the largest texture that is NOT a normal map; decode lazily.
                byte[] firstRgba = null; int fw = 0, fh = 0;
                foreach (Texture t in candidates)
                {
                    int w = t.width, h = t.height;
                    byte[] rgba = t.textureType == Texture.DXT1
                        ? DecodeDxt1(t.texData.blob, w, h)
                        : DecodeDxt5(t.texData.blob, w, h);
                    if (firstRgba == null) { firstRgba = rgba; fw = w; fh = h; }
                    if (!LooksLikeNormalMap(rgba))
                        return RgbaToPng(rgba, w, h);
                }
                // Everything looked like a normal map: fall back to the largest.
                return RgbaToPng(firstRgba, fw, fh);
            }
            catch { return null; }
        }

        /// <summary>
        /// Cheap heuristic: tangent-space normal maps are blue-dominant with R,G
        /// clustered around mid-grey. Used to avoid picking a normal map as the
        /// base color when a model has several textures.
        /// </summary>
        public static bool LooksLikeNormalMap(byte[] rgba)
        {
            int n = rgba.Length / 4;
            if (n == 0) return false;
            long r = 0, g = 0, b = 0;
            for (int i = 0; i < n; i++) { r += rgba[i * 4]; g += rgba[i * 4 + 1]; b += rgba[i * 4 + 2]; }
            double ar = (double)r / n, ag = (double)g / n, ab = (double)b / n;
            return ab > 180 && ar > 90 && ar < 165 && ag > 90 && ag < 165
                   && ab > ar + 40 && ab > ag + 40;
        }

        public static byte[] RgbaToPng(byte[] rgba, int w, int h)
        {
            using (Bitmap bmp = RgbaToBitmap(rgba, w, h))
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        /// <summary>RGBA byte array -> 32bpp Bitmap. Caller disposes the bitmap.</summary>
        public static Bitmap RgbaToBitmap(byte[] rgba, int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, w, h);
            BitmapData bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, bmp.PixelFormat);
            byte[] bgra = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                bgra[i * 4 + 0] = rgba[i * 4 + 2]; // B
                bgra[i * 4 + 1] = rgba[i * 4 + 1]; // G
                bgra[i * 4 + 2] = rgba[i * 4 + 0]; // R
                bgra[i * 4 + 3] = rgba[i * 4 + 3]; // A
            }
            Marshal.Copy(bgra, 0, bd.Scan0, bgra.Length);
            bmp.UnlockBits(bd);
            return bmp;
        }

        /// <summary>Decodes an RW4 DXT1/DXT5 texture to a Bitmap (level 0), or null if it
        /// isn't a supported block-compressed texture. Caller disposes the bitmap.</summary>
        public static Bitmap DecodeTextureToBitmap(Texture tex)
        {
            if (tex == null) return null;
            if (tex.textureType != Texture.DXT1 && tex.textureType != Texture.DXT5) return null;
            int w = tex.width, h = tex.height;
            byte[] rgba = tex.textureType == Texture.DXT1
                ? DecodeDxt1(tex.texData.blob, w, h)
                : DecodeDxt5(tex.texData.blob, w, h);
            return RgbaToBitmap(rgba, w, h);
        }

        private static void SetColors(ushort c0, ushort c1, byte[][] col, bool dxt1)
        {
            col[0] = Rgb565(c0); col[1] = Rgb565(c1);
            if (!dxt1 || c0 > c1)
            {
                col[2] = new byte[] { (byte)((2 * col[0][0] + col[1][0]) / 3), (byte)((2 * col[0][1] + col[1][1]) / 3), (byte)((2 * col[0][2] + col[1][2]) / 3), 255 };
                col[3] = new byte[] { (byte)((col[0][0] + 2 * col[1][0]) / 3), (byte)((col[0][1] + 2 * col[1][1]) / 3), (byte)((col[0][2] + 2 * col[1][2]) / 3), 255 };
            }
            else
            {
                col[2] = new byte[] { (byte)((col[0][0] + col[1][0]) / 2), (byte)((col[0][1] + col[1][1]) / 2), (byte)((col[0][2] + col[1][2]) / 2), 255 };
                col[3] = new byte[] { 0, 0, 0, 0 }; // transparent in 1-bit-alpha DXT1
            }
        }

        private static byte[] Rgb565(ushort c)
        {
            int r = (c >> 11) & 0x1F, g = (c >> 5) & 0x3F, b = c & 0x1F;
            return new byte[] { (byte)((r << 3) | (r >> 2)), (byte)((g << 2) | (g >> 4)), (byte)((b << 3) | (b >> 2)), 255 };
        }

        private static byte[] DecodeDxt1(byte[] data, int w, int h)
        {
            byte[] outp = new byte[w * h * 4];
            int bw = (w + 3) / 4, bh = (h + 3) / 4, p = 0;
            byte[][] col = new byte[4][];
            for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                ushort c0 = (ushort)(data[p] | (data[p + 1] << 8));
                ushort c1 = (ushort)(data[p + 2] | (data[p + 3] << 8));
                uint bits = (uint)(data[p + 4] | (data[p + 5] << 8) | (data[p + 6] << 16) | (data[p + 7] << 24));
                p += 8;
                SetColors(c0, c1, col, true);
                for (int ty = 0; ty < 4; ty++)
                for (int tx = 0; tx < 4; tx++)
                {
                    int idx = (int)((bits >> (2 * (4 * ty + tx))) & 3);
                    int px = bx * 4 + tx, py = by * 4 + ty;
                    if (px >= w || py >= h) continue;
                    int o = (py * w + px) * 4;
                    outp[o] = col[idx][0]; outp[o + 1] = col[idx][1]; outp[o + 2] = col[idx][2]; outp[o + 3] = col[idx][3];
                }
            }
            return outp;
        }

        private static byte[] DecodeDxt5(byte[] data, int w, int h)
        {
            byte[] outp = new byte[w * h * 4];
            int bw = (w + 3) / 4, bh = (h + 3) / 4, p = 0;
            byte[][] col = new byte[4][];
            byte[] a = new byte[8];
            for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                a[0] = data[p]; a[1] = data[p + 1];
                if (a[0] > a[1])
                    for (int i = 1; i <= 6; i++) a[i + 1] = (byte)(((7 - i) * a[0] + i * a[1]) / 7);
                else
                {
                    for (int i = 1; i <= 4; i++) a[i + 1] = (byte)(((5 - i) * a[0] + i * a[1]) / 5);
                    a[6] = 0; a[7] = 255;
                }
                ulong abits = 0;
                for (int i = 0; i < 6; i++) abits |= ((ulong)data[p + 2 + i]) << (8 * i);
                p += 8;

                ushort c0 = (ushort)(data[p] | (data[p + 1] << 8));
                ushort c1 = (ushort)(data[p + 2] | (data[p + 3] << 8));
                uint bits = (uint)(data[p + 4] | (data[p + 5] << 8) | (data[p + 6] << 16) | (data[p + 7] << 24));
                p += 8;
                SetColors(c0, c1, col, false);

                for (int ty = 0; ty < 4; ty++)
                for (int tx = 0; tx < 4; tx++)
                {
                    int ci = (int)((bits >> (2 * (4 * ty + tx))) & 3);
                    int ai = (int)((abits >> (3 * (4 * ty + tx))) & 7);
                    int px = bx * 4 + tx, py = by * 4 + ty;
                    if (px >= w || py >= h) continue;
                    int o = (py * w + px) * 4;
                    outp[o] = col[ci][0]; outp[o + 1] = col[ci][1]; outp[o + 2] = col[ci][2]; outp[o + 3] = a[ai];
                }
            }
            return outp;
        }

        public RW4Mesh Import(RW4Mesh mesh, string fileName)
        {
            throw new NotSupportedException("glTF import is not supported; use the .obj importer.");
        }
    }
}
