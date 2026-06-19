using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace SporeMaster.RenderWare4
{
    /// <summary>
    /// Exports an RW4 mesh to a self-contained binary glTF 2.0 file (.glb):
    /// positions + normals + texture coordinates + triangle indices, in a single
    /// buffer. Opens in Blender, Windows 3D Viewer, three.js, Unity, Unreal, etc.
    ///
    /// This is the in-app equivalent of the geometry side of the SporeModder
    /// Blender add-on. Materials/textures/skeletons/animations are a TODO
    /// (see HANDOFF.md) and are not written yet.
    /// </summary>
    public class GltfConverter : IConverter
    {
        // glTF / GLB constants
        private const uint GLB_MAGIC = 0x46546C67;   // "glTF"
        private const uint GLB_VERSION = 2;
        private const uint CHUNK_JSON = 0x4E4F534A;  // "JSON"
        private const uint CHUNK_BIN = 0x004E4942;   // "BIN\0"
        private const int COMP_FLOAT = 5126;         // GL FLOAT
        private const int COMP_UINT = 5125;          // GL UNSIGNED_INT
        private const int TARGET_ARRAY = 34962;      // ARRAY_BUFFER
        private const int TARGET_ELEMENT = 34963;    // ELEMENT_ARRAY_BUFFER
        private const int MODE_TRIANGLES = 4;

        public void Export(RW4Mesh mesh, string fileName)
        {
            var verts = mesh.vertices.vertices;
            var tris = mesh.triangles.triangles;

            int vCount = verts.Length;

            // Build the binary buffer: [positions][normals][texcoords][indices]
            using (var bin = new MemoryStream())
            using (var bw = new BinaryWriter(bin))
            {
                // POSITION (VEC3 float) + track min/max (required by glTF)
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

                // NORMAL (VEC3 float)
                int normOffset = (int)bin.Position;
                foreach (Vertex v in verts)
                {
                    bw.Write(v.Normal.X); bw.Write(v.Normal.Y); bw.Write(v.Normal.Z);
                }
                int normLen = vCount * 12;

                // TEXCOORD_0 (VEC2 float)
                int uvOffset = (int)bin.Position;
                foreach (Vertex v in verts)
                {
                    bw.Write(v.TextureCoordinates.X); bw.Write(v.TextureCoordinates.Y);
                }
                int uvLen = vCount * 8;

                // INDICES (SCALAR uint), skipping degenerate triangles
                int idxOffset = (int)bin.Position;
                int idxCount = 0;
                foreach (Triangle t in tris)
                {
                    if (t.i == t.j || t.j == t.k || t.i == t.k) continue;
                    bw.Write((uint)t.i); bw.Write((uint)t.j); bw.Write((uint)t.k);
                    idxCount += 3;
                }
                int idxLen = idxCount * 4;

                bw.Flush();
                byte[] binData = bin.ToArray();

                string json = BuildJson(vCount, idxCount, posOffset, posLen, normOffset, normLen,
                    uvOffset, uvLen, idxOffset, idxLen, binData.Length, min, max);

                WriteGlb(fileName, json, binData);
            }
        }

        private static string BuildJson(int vCount, int idxCount,
            int posOffset, int posLen, int normOffset, int normLen,
            int uvOffset, int uvLen, int idxOffset, int idxLen,
            int bufferLength, float[] min, float[] max)
        {
            var gltf = new Dictionary<string, object>
            {
                ["asset"] = new Dictionary<string, object> { ["version"] = "2.0", ["generator"] = "SimCityPak glTF exporter" },
                ["scene"] = 0,
                ["scenes"] = new object[] { new Dictionary<string, object> { ["nodes"] = new object[] { 0 } } },
                ["nodes"] = new object[] { new Dictionary<string, object> { ["mesh"] = 0 } },
                ["meshes"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["primitives"] = new object[]
                        {
                            new Dictionary<string, object>
                            {
                                ["attributes"] = new Dictionary<string, object> { ["POSITION"] = 0, ["NORMAL"] = 1, ["TEXCOORD_0"] = 2 },
                                ["indices"] = 3,
                                ["mode"] = MODE_TRIANGLES
                            }
                        }
                    }
                },
                ["buffers"] = new object[] { new Dictionary<string, object> { ["byteLength"] = bufferLength } },
                ["bufferViews"] = new object[]
                {
                    new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = posOffset,  ["byteLength"] = posLen,  ["target"] = TARGET_ARRAY },
                    new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = normOffset, ["byteLength"] = normLen, ["target"] = TARGET_ARRAY },
                    new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = uvOffset,   ["byteLength"] = uvLen,   ["target"] = TARGET_ARRAY },
                    new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = idxOffset,  ["byteLength"] = idxLen,  ["target"] = TARGET_ELEMENT }
                },
                ["accessors"] = new object[]
                {
                    new Dictionary<string, object> { ["bufferView"] = 0, ["componentType"] = COMP_FLOAT, ["count"] = vCount, ["type"] = "VEC3",
                        ["min"] = new object[] { min[0], min[1], min[2] }, ["max"] = new object[] { max[0], max[1], max[2] } },
                    new Dictionary<string, object> { ["bufferView"] = 1, ["componentType"] = COMP_FLOAT, ["count"] = vCount, ["type"] = "VEC3" },
                    new Dictionary<string, object> { ["bufferView"] = 2, ["componentType"] = COMP_FLOAT, ["count"] = vCount, ["type"] = "VEC2" },
                    new Dictionary<string, object> { ["bufferView"] = 3, ["componentType"] = COMP_UINT,  ["count"] = idxCount, ["type"] = "SCALAR" }
                }
            };
            return JsonConvert.SerializeObject(gltf);
        }

        private static void WriteGlb(string fileName, string json, byte[] binData)
        {
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            int jsonPad = (4 - (jsonBytes.Length % 4)) % 4;
            int binPad = (4 - (binData.Length % 4)) % 4;

            int totalLength = 12                          // header
                + 8 + jsonBytes.Length + jsonPad          // JSON chunk
                + 8 + binData.Length + binPad;            // BIN chunk

            using (var fs = File.Create(fileName))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(GLB_MAGIC);
                w.Write(GLB_VERSION);
                w.Write((uint)totalLength);

                // JSON chunk (padded with spaces)
                w.Write((uint)(jsonBytes.Length + jsonPad));
                w.Write(CHUNK_JSON);
                w.Write(jsonBytes);
                for (int i = 0; i < jsonPad; i++) w.Write((byte)0x20);

                // BIN chunk (padded with zeros)
                w.Write((uint)(binData.Length + binPad));
                w.Write(CHUNK_BIN);
                w.Write(binData);
                for (int i = 0; i < binPad; i++) w.Write((byte)0x00);
            }
        }

        public RW4Mesh Import(RW4Mesh mesh, string fileName)
        {
            throw new NotSupportedException("glTF import is not supported; use the .obj importer.");
        }
    }
}
