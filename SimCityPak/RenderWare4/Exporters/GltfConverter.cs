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
    /// Skinned models also export their skeleton: a joint node hierarchy + inverse-bind
    /// matrices + per-vertex JOINTS_0/WEIGHTS_0 (a glTF skin). Still TODO: animation tracks.
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

        /// <summary>One animation channel: a joint's time-stamped local TRS keyframes.</summary>
        private class AnimTrack
        {
            public int Joint;
            public float[] Times;     // [k]
            public float[][] T;       // [k][3]
            public float[][] R;       // [k][4] xyzw
            public float[][] S;       // [k][3]
            public bool HasScale;
            public int TimesOffset, TOffset, ROffset, SOffset;   // byte offsets in the bin chunk
            public float TimeMin, TimeMax;
        }
        private class AnimClip { public string Name; public List<AnimTrack> Tracks = new List<AnimTrack>(); public float Length; }

        /// <summary>Skeleton + per-vertex skin binding + animations extracted from an RW4 model,
        /// ready to emit as a glTF skin and animations.</summary>
        private class SkinData
        {
            public int JointCount;
            public int[] Parent;          // per joint: parent joint index, or -1
            public float[][] NodeT;       // per joint: bind local translation [3]
            public float[][] NodeR;       // per joint: bind local rotation [4] xyzw
            public float[][] NodeS;       // per joint: bind local scale [3]
            public float[] InverseBind;   // 16*JointCount floats, column-major 4x4 per joint
            public ushort[] VJoints;      // 4 per vertex
            public float[] VWeights;      // 4 per vertex
            public List<AnimClip> Clips = new List<AnimClip>();
        }

        // ---- small column-major 4x4 matrix helpers (for bind-pose math) ----
        private static float[] Mat4Mul(float[] a, float[] b)
        {
            var m = new float[16];
            for (int c = 0; c < 4; c++) for (int r = 0; r < 4; r++)
            { float s = 0; for (int k = 0; k < 4; k++) s += a[k * 4 + r] * b[c * 4 + k]; m[c * 4 + r] = s; }
            return m;
        }
        private static float[] Mat4Inverse(float[] m)
        {
            var inv = new float[16];
            inv[0]=m[5]*m[10]*m[15]-m[5]*m[11]*m[14]-m[9]*m[6]*m[15]+m[9]*m[7]*m[14]+m[13]*m[6]*m[11]-m[13]*m[7]*m[10];
            inv[4]=-m[4]*m[10]*m[15]+m[4]*m[11]*m[14]+m[8]*m[6]*m[15]-m[8]*m[7]*m[14]-m[12]*m[6]*m[11]+m[12]*m[7]*m[10];
            inv[8]=m[4]*m[9]*m[15]-m[4]*m[11]*m[13]-m[8]*m[5]*m[15]+m[8]*m[7]*m[13]+m[12]*m[5]*m[11]-m[12]*m[7]*m[9];
            inv[12]=-m[4]*m[9]*m[14]+m[4]*m[10]*m[13]+m[8]*m[5]*m[14]-m[8]*m[6]*m[13]-m[12]*m[5]*m[10]+m[12]*m[6]*m[9];
            inv[1]=-m[1]*m[10]*m[15]+m[1]*m[11]*m[14]+m[9]*m[2]*m[15]-m[9]*m[3]*m[14]-m[13]*m[2]*m[11]+m[13]*m[3]*m[10];
            inv[5]=m[0]*m[10]*m[15]-m[0]*m[11]*m[14]-m[8]*m[2]*m[15]+m[8]*m[3]*m[14]+m[12]*m[2]*m[11]-m[12]*m[3]*m[10];
            inv[9]=-m[0]*m[9]*m[15]+m[0]*m[11]*m[13]+m[8]*m[1]*m[15]-m[8]*m[3]*m[13]-m[12]*m[1]*m[11]+m[12]*m[3]*m[9];
            inv[13]=m[0]*m[9]*m[14]-m[0]*m[10]*m[13]-m[8]*m[1]*m[14]+m[8]*m[2]*m[13]+m[12]*m[1]*m[10]-m[12]*m[2]*m[9];
            inv[2]=m[1]*m[6]*m[15]-m[1]*m[7]*m[14]-m[5]*m[2]*m[15]+m[5]*m[3]*m[14]+m[13]*m[2]*m[7]-m[13]*m[3]*m[6];
            inv[6]=-m[0]*m[6]*m[15]+m[0]*m[7]*m[14]+m[4]*m[2]*m[15]-m[4]*m[3]*m[14]-m[12]*m[2]*m[7]+m[12]*m[3]*m[6];
            inv[10]=m[0]*m[5]*m[15]-m[0]*m[7]*m[13]-m[4]*m[1]*m[15]+m[4]*m[3]*m[13]+m[12]*m[1]*m[7]-m[12]*m[3]*m[5];
            inv[14]=-m[0]*m[5]*m[14]+m[0]*m[6]*m[13]+m[4]*m[1]*m[14]-m[4]*m[2]*m[13]-m[12]*m[1]*m[6]+m[12]*m[2]*m[5];
            inv[3]=-m[1]*m[6]*m[11]+m[1]*m[7]*m[10]+m[5]*m[2]*m[11]-m[5]*m[3]*m[10]-m[9]*m[2]*m[7]+m[9]*m[3]*m[6];
            inv[7]=m[0]*m[6]*m[11]-m[0]*m[7]*m[10]-m[4]*m[2]*m[11]+m[4]*m[3]*m[10]+m[8]*m[2]*m[7]-m[8]*m[3]*m[6];
            inv[11]=-m[0]*m[5]*m[11]+m[0]*m[7]*m[9]+m[4]*m[1]*m[11]-m[4]*m[3]*m[9]-m[8]*m[1]*m[7]+m[8]*m[3]*m[5];
            inv[15]=m[0]*m[5]*m[10]-m[0]*m[6]*m[9]-m[4]*m[1]*m[10]+m[4]*m[2]*m[9]+m[8]*m[1]*m[6]-m[8]*m[2]*m[5];
            float det = m[0]*inv[0]+m[1]*inv[4]+m[2]*inv[8]+m[3]*inv[12];
            if (Math.Abs(det) < 1e-12f) return new float[] { 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1 };
            float id = 1f / det; for (int i = 0; i < 16; i++) inv[i] *= id;
            return inv;
        }
        /// <summary>Decompose a column-major TRS matrix into translation, rotation quaternion
        /// (xyzw) and scale.</summary>
        private static void Mat4DecomposeTRS(float[] m, out float[] t, out float[] rq, out float[] s)
        {
            t = new[] { m[12], m[13], m[14] };
            float sx = (float)Math.Sqrt(m[0]*m[0]+m[1]*m[1]+m[2]*m[2]);
            float sy = (float)Math.Sqrt(m[4]*m[4]+m[5]*m[5]+m[6]*m[6]);
            float sz = (float)Math.Sqrt(m[8]*m[8]+m[9]*m[9]+m[10]*m[10]);
            if (sx < 1e-8f) sx = 1; if (sy < 1e-8f) sy = 1; if (sz < 1e-8f) sz = 1;
            s = new[] { sx, sy, sz };
            // rotation matrix (column-major basis) with scale removed
            float r00=m[0]/sx, r10=m[1]/sx, r20=m[2]/sx;     // col0
            float r01=m[4]/sy, r11=m[5]/sy, r21=m[6]/sy;     // col1
            float r02=m[8]/sz, r12=m[9]/sz, r22=m[10]/sz;    // col2
            float tr = r00 + r11 + r22; float qx, qy, qz, qw;
            if (tr > 0) { float ss = (float)Math.Sqrt(tr + 1f) * 2f; qw = 0.25f*ss; qx=(r21-r12)/ss; qy=(r02-r20)/ss; qz=(r10-r01)/ss; }
            else if (r00 > r11 && r00 > r22) { float ss=(float)Math.Sqrt(1f+r00-r11-r22)*2f; qw=(r21-r12)/ss; qx=0.25f*ss; qy=(r01+r10)/ss; qz=(r02+r20)/ss; }
            else if (r11 > r22) { float ss=(float)Math.Sqrt(1f+r11-r00-r22)*2f; qw=(r02-r20)/ss; qx=(r01+r10)/ss; qy=0.25f*ss; qz=(r12+r21)/ss; }
            else { float ss=(float)Math.Sqrt(1f+r22-r00-r11)*2f; qw=(r10-r01)/ss; qx=(r02+r20)/ss; qy=(r12+r21)/ss; qz=0.25f*ss; }
            float ql=(float)Math.Sqrt(qx*qx+qy*qy+qz*qz+qw*qw); if (ql<1e-8f) ql=1;
            rq = new[] { qx/ql, qy/ql, qz/ql, qw/ql };
        }

        /// <summary>
        /// Builds skin data from the model's skeleton (RW4Skeleton), the mesh's blend components,
        /// and any keyframe animations. The inverse-bind is the stored skin matrix (so glTF
        /// computes jointGlobal*inverseBind correctly); joint nodes get their bind-local TRS; the
        /// scene root carries the Z-up->Y-up rotation. Vertices with no blend data bind to joint 0.
        /// Returns null if the model has no usable skeleton.
        /// </summary>
        private static SkinData ExtractSkin(RW4Mesh mesh, int vCount)
        {
            try
            {
                if (mesh == null || mesh.model == null) return null;
                RW4Skeleton skel = null;
                foreach (RW4Section s in mesh.model.Sections) { if (s.obj is RW4Skeleton) { skel = (RW4Skeleton)s.obj; break; } }
                if (skel == null || skel.jointInfo == null || skel.jointInfo.items == null) return null;
                var items = skel.jointInfo.items;
                int n = items.Length;
                if (n == 0 || skel.mat4 == null || skel.mat4.items == null || skel.mat4.items.Length < n) return null;

                var sd = new SkinData { JointCount = n, Parent = new int[n], InverseBind = new float[16 * n],
                    NodeT = new float[n][], NodeR = new float[n][], NodeS = new float[n][] };

                // inverse-bind = the stored skin matrix in column-major (the 0-padded 3x3 rows are
                // already column-major of R^T; only the [15] element needs to be 1, not 0).
                var ibm = new float[n][];
                for (int i = 0; i < n; i++)
                {
                    sd.Parent[i] = items[i].parent == null ? -1 : items[i].parent.index;
                    float[] src = skel.mat4.items[i].m;
                    var ib = new float[16];
                    Array.Copy(src, ib, 16);
                    ib[3] = 0; ib[7] = 0; ib[11] = 0; ib[15] = 1;
                    ibm[i] = ib;
                    Array.Copy(ib, 0, sd.InverseBind, i * 16, 16);
                }
                // bind-local TRS per joint = inverse(IB[parent]) cancels: bindLocal = IB[parent] * inverse(IB[i])
                for (int i = 0; i < n; i++)
                {
                    float[] bindPose = Mat4Inverse(ibm[i]);
                    float[] local = sd.Parent[i] < 0 ? bindPose : Mat4Mul(ibm[sd.Parent[i]], bindPose);
                    float[] t, rq, sc; Mat4DecomposeTRS(local, out t, out rq, out sc);
                    sd.NodeT[i] = t; sd.NodeR[i] = rq; sd.NodeS[i] = sc;
                }

                // per-vertex joints + weights
                sd.VJoints = new ushort[vCount * 4];
                sd.VWeights = new float[vCount * 4];
                var verts = mesh.vertices.vertices;
                for (int v = 0; v < vCount; v++)
                {
                    ushort[] idx = { 0, 0, 0, 0 };
                    float[] wt = { 0, 0, 0, 0 };
                    bool gotIdx = false, gotWt = false;
                    if (verts[v].VertexComponents != null)
                        foreach (var c in verts[v].VertexComponents)
                        {
                            if (c.Usage == D3DDECLUSAGE.D3DDECLUSAGE_BLENDINDICES) { ReadQuadU(c, idx, n); gotIdx = true; }
                            else if (c.Usage == D3DDECLUSAGE.D3DDECLUSAGE_BLENDWEIGHT) { ReadQuadF(c, wt); gotWt = true; }
                        }
                    if (!gotIdx) { idx[0] = 0; }
                    if (!gotWt) { wt[0] = 1; wt[1] = wt[2] = wt[3] = 0; }
                    float sum = wt[0] + wt[1] + wt[2] + wt[3];
                    if (sum <= 0.0001f) { wt[0] = 1; sum = 1; }
                    for (int k = 0; k < 4; k++) { sd.VJoints[v * 4 + k] = idx[k]; sd.VWeights[v * 4 + k] = wt[k] / sum; }
                }

                // animations: map each channel (by joint name fnv, else by order) to a joint
                var byFnv = new Dictionary<uint, int>();
                for (int i = 0; i < n; i++) if (!byFnv.ContainsKey(items[i].name_fnv)) byFnv[items[i].name_fnv] = i;
                int clipNo = 0;
                foreach (RW4Section sec in mesh.model.Sections)
                {
                    Anim an = sec.obj as Anim;
                    if (an == null || an.channels == null) continue;
                    var clip = new AnimClip { Name = "anim" + (clipNo++), Length = an.length };
                    for (int ci = 0; ci < an.channels.Length; ci++)
                    {
                        var ch = an.channels[ci];
                        if (ch.keys == null || ch.keys.Count == 0) continue;
                        int joint; if (!byFnv.TryGetValue(ch.id, out joint)) joint = ci < n ? ci : -1;
                        if (joint < 0) continue;
                        int kc = ch.keys.Count;
                        var tr = new AnimTrack { Joint = joint, Times = new float[kc], T = new float[kc][], R = new float[kc][], S = new float[kc][], HasScale = ch.components == 0x601 };
                        for (int k = 0; k < kc; k++)
                        {
                            var key = ch.keys[k];
                            tr.Times[k] = key.time;
                            tr.T[k] = new[] { key.tx, key.ty, key.tz };
                            tr.R[k] = new[] { key.qx, key.qy, key.qz, key.qw };
                            tr.S[k] = new[] { key.sx, key.sy, key.sz };
                        }
                        clip.Tracks.Add(tr);
                    }
                    if (clip.Tracks.Count > 0) sd.Clips.Add(clip);
                }
                return sd;
            }
            catch { return null; }
        }

        private static void ReadQuadU(IVertexComponentValue c, ushort[] outv, int jointCount)
        {
            var u = c as VertexUByte4Value; if (u != null) { outv[0] = u.X; outv[1] = u.Y; outv[2] = u.Z; outv[3] = u.W; }
            else { var s = c as VertexShort4Value; if (s != null) { outv[0] = (ushort)s.X; outv[1] = (ushort)s.Y; outv[2] = (ushort)s.Z; outv[3] = (ushort)s.W; } }
            for (int k = 0; k < 4; k++) if (outv[k] >= jointCount) outv[k] = 0;   // clamp stray indices
        }

        private static void ReadQuadF(IVertexComponentValue c, float[] outv)
        {
            var u = c as VertexUByte4Value; if (u != null) { outv[0] = u.X / 255f; outv[1] = u.Y / 255f; outv[2] = u.Z / 255f; outv[3] = u.W / 255f; return; }
            var f = c as VertexFloat4Value; if (f != null) { outv[0] = f.X; outv[1] = f.Y; outv[2] = f.Z; outv[3] = f.W; return; }
            var sn = c as VertexShort4NValue; if (sn != null) { outv[0] = (float)sn.X; outv[1] = (float)sn.Y; outv[2] = (float)sn.Z; outv[3] = (float)sn.W; return; }
        }

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

            // Decide whether the mesh has a usable UV. A FLOAT2 texcoord is a real per-vertex
            // UV. A FLOAT4 texcoord is a real UV on some models but a large world-projection
            // coordinate on facade buildings (which would scramble any flat texture) — so only
            // accept FLOAT4 when its values stay in a sane UV range. Otherwise export geometry
            // only (no UVs/material).
            bool hasUv = false;
            if (vCount > 0 && verts[0].HasTextureCoordinates)
            {
                bool hasFloat2 = false;
                foreach (var c in verts[0].VertexComponents)
                    if (c.Usage == D3DDECLUSAGE.D3DDECLUSAGE_TEXCOORD && c is VertexFloat2Value) { hasFloat2 = true; break; }
                if (hasFloat2) hasUv = true;
                else
                {
                    float mx = 0; float u, vtmp;
                    foreach (Vertex vv in verts) { if (vv.TryGetUV(out u, out vtmp)) { float a = Math.Max(Math.Abs(u), Math.Abs(vtmp)); if (a > mx) mx = a; } }
                    hasUv = mx <= 8f;     // sane 0..1-ish (with a little tiling); facades are far larger
                }
            }

            // Prefer the caller's resolver (RW4Material -> external texture resources);
            // fall back to the internal-texture heuristic for the base color.
            MaterialTextures tex = null;
            byte[] basePng = null, normalPng = null, specPng = null;
            if (hasUv)
            {
                if (_textureProvider != null) { try { tex = _textureProvider(mesh); } catch { tex = null; } }
                basePng = tex != null ? tex.BaseColor : null;
                if (basePng == null) basePng = TryGetDiffusePng(mesh);   // null if no usable texture
                normalPng = tex != null ? tex.Normal : null;
                specPng = tex != null ? tex.Specular : null;
            }

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
                foreach (Vertex v in verts)
                {
                    // RW4 stores V negated relative to glTF's top-left origin, so flip it.
                    float u, vv;
                    if (hasUv && v.TryGetUV(out u, out vv)) { bw.Write(u); bw.Write(-vv); }
                    else { bw.Write(0f); bw.Write(0f); }
                }
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

                // Optional skin: JOINTS_0 (ushort vec4), WEIGHTS_0 (float vec4), inverse-bind
                // matrices (float mat4 per joint).
                SkinData skin = ExtractSkin(mesh, vCount);
                int jointsOffset = -1, weightsOffset = -1, ibmOffset = -1;
                if (skin != null)
                {
                    Pad4(bw, bin); jointsOffset = (int)bin.Position;
                    for (int i = 0; i < vCount * 4; i++) bw.Write(skin.VJoints[i]);
                    Pad4(bw, bin); weightsOffset = (int)bin.Position;
                    for (int i = 0; i < vCount * 4; i++) bw.Write(skin.VWeights[i]);
                    Pad4(bw, bin); ibmOffset = (int)bin.Position;
                    for (int i = 0; i < skin.InverseBind.Length; i++) bw.Write(skin.InverseBind[i]);

                    // animation keyframe buffers (time inputs + TRS outputs per track)
                    foreach (var clip in skin.Clips)
                        foreach (var tr in clip.Tracks)
                        {
                            Pad4(bw, bin); tr.TimesOffset = (int)bin.Position;
                            float tmin = float.MaxValue, tmax = float.MinValue;
                            for (int k = 0; k < tr.Times.Length; k++) { bw.Write(tr.Times[k]); if (tr.Times[k] < tmin) tmin = tr.Times[k]; if (tr.Times[k] > tmax) tmax = tr.Times[k]; }
                            tr.TimeMin = tmin; tr.TimeMax = tmax;
                            Pad4(bw, bin); tr.TOffset = (int)bin.Position;
                            for (int k = 0; k < tr.T.Length; k++) { bw.Write(tr.T[k][0]); bw.Write(tr.T[k][1]); bw.Write(tr.T[k][2]); }
                            Pad4(bw, bin); tr.ROffset = (int)bin.Position;
                            for (int k = 0; k < tr.R.Length; k++) { bw.Write(tr.R[k][0]); bw.Write(tr.R[k][1]); bw.Write(tr.R[k][2]); bw.Write(tr.R[k][3]); }
                            if (tr.HasScale)
                            {
                                Pad4(bw, bin); tr.SOffset = (int)bin.Position;
                                for (int k = 0; k < tr.S.Length; k++) { bw.Write(tr.S[k][0]); bw.Write(tr.S[k][1]); bw.Write(tr.S[k][2]); }
                            }
                        }
                }

                bw.Flush();
                byte[] binData = bin.ToArray();

                string json = BuildJson(vCount, idxCount, posOffset, posLen, normOffset, normLen,
                    uvOffset, uvLen, idxOffset, idxLen, imgSpans, baseImg, normalImg, specImg,
                    binData.Length, min, max, skin, jointsOffset, weightsOffset, ibmOffset);

                WriteGlb(fileName, json, binData);
            }
        }

        private static void Pad4(BinaryWriter bw, Stream s)
        {
            int pad = (int)((4 - (s.Position % 4)) % 4);
            for (int i = 0; i < pad; i++) bw.Write((byte)0);
        }

        private const int COMP_USHORT = 5123;

        private static string BuildJson(int vCount, int idxCount,
            int posOffset, int posLen, int normOffset, int normLen,
            int uvOffset, int uvLen, int idxOffset, int idxLen,
            List<int[]> imgSpans, int baseImg, int normalImg, int specImg,
            int bufferLength, float[] min, float[] max,
            SkinData skin, int jointsOffset, int weightsOffset, int ibmOffset)
        {
            int nImages = imgSpans.Count;
            bool hasMaterial = nImages > 0;
            bool hasSkin = skin != null;

            var bufferViews = new List<object>
            {
                new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = posOffset,  ["byteLength"] = posLen,  ["target"] = TARGET_ARRAY },
                new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = normOffset, ["byteLength"] = normLen, ["target"] = TARGET_ARRAY },
                new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = uvOffset,   ["byteLength"] = uvLen,   ["target"] = TARGET_ARRAY },
                new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = idxOffset,  ["byteLength"] = idxLen,  ["target"] = TARGET_ELEMENT }
            };
            var accessors = new List<object>
            {
                new Dictionary<string, object> { ["bufferView"] = 0, ["componentType"] = COMP_FLOAT, ["count"] = vCount, ["type"] = "VEC3",
                    ["min"] = new object[] { min[0], min[1], min[2] }, ["max"] = new object[] { max[0], max[1], max[2] } },
                new Dictionary<string, object> { ["bufferView"] = 1, ["componentType"] = COMP_FLOAT, ["count"] = vCount, ["type"] = "VEC3" },
                new Dictionary<string, object> { ["bufferView"] = 2, ["componentType"] = COMP_FLOAT, ["count"] = vCount, ["type"] = "VEC2" },
                new Dictionary<string, object> { ["bufferView"] = 3, ["componentType"] = COMP_UINT,  ["count"] = idxCount, ["type"] = "SCALAR" }
            };

            var primitiveAttrs = new Dictionary<string, object> { ["POSITION"] = 0, ["NORMAL"] = 1, ["TEXCOORD_0"] = 2 };

            // skin accessors/bufferViews (JOINTS_0, WEIGHTS_0, inverse-bind matrices)
            int jointsAcc = -1, weightsAcc = -1, ibmAcc = -1;
            if (hasSkin)
            {
                int bvJ = bufferViews.Count;
                bufferViews.Add(new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = jointsOffset, ["byteLength"] = vCount * 8, ["target"] = TARGET_ARRAY });
                int bvW = bufferViews.Count;
                bufferViews.Add(new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = weightsOffset, ["byteLength"] = vCount * 16, ["target"] = TARGET_ARRAY });
                int bvIbm = bufferViews.Count;
                bufferViews.Add(new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = ibmOffset, ["byteLength"] = skin.JointCount * 64 });
                jointsAcc = accessors.Count; accessors.Add(new Dictionary<string, object> { ["bufferView"] = bvJ, ["componentType"] = COMP_USHORT, ["count"] = vCount, ["type"] = "VEC4" });
                weightsAcc = accessors.Count; accessors.Add(new Dictionary<string, object> { ["bufferView"] = bvW, ["componentType"] = COMP_FLOAT, ["count"] = vCount, ["type"] = "VEC4" });
                ibmAcc = accessors.Count; accessors.Add(new Dictionary<string, object> { ["bufferView"] = bvIbm, ["componentType"] = COMP_FLOAT, ["count"] = skin.JointCount, ["type"] = "MAT4" });
                primitiveAttrs["JOINTS_0"] = jointsAcc;
                primitiveAttrs["WEIGHTS_0"] = weightsAcc;
            }

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
                ["attributes"] = primitiveAttrs,
                ["indices"] = 3,
                ["mode"] = MODE_TRIANGLES
            };
            if (hasMaterial) primitive["material"] = 0;

            // ----- node graph -----
            // A root node carries the Z-up -> Y-up correction (-90° about X). For a skinned mesh
            // the joints hang off the root (so the rotation applies uniformly) and the mesh node
            // references the skin; for a static mesh the root just holds the mesh.
            object[] upRotation = new object[] { -0.70710678, 0.0, 0.0, 0.70710678 };
            var nodes = new List<object>();
            object[] sceneNodes;
            object[] gltfSkin = null;
            if (hasSkin)
            {
                int meshNodeIdx = 1;          // 0 = root, 1 = mesh
                int jointBase = 2;            // joints start at node index 2
                var rootChildren = new List<object> { meshNodeIdx };
                for (int i = 0; i < skin.JointCount; i++) if (skin.Parent[i] < 0) rootChildren.Add(jointBase + i);
                nodes.Add(new Dictionary<string, object> { ["rotation"] = upRotation, ["children"] = rootChildren.ToArray() }); // 0 root
                nodes.Add(new Dictionary<string, object> { ["mesh"] = 0, ["skin"] = 0 });                                       // 1 mesh
                for (int i = 0; i < skin.JointCount; i++)
                {
                    var jn = new Dictionary<string, object>
                    {
                        ["translation"] = new object[] { skin.NodeT[i][0], skin.NodeT[i][1], skin.NodeT[i][2] },
                        ["rotation"] = new object[] { skin.NodeR[i][0], skin.NodeR[i][1], skin.NodeR[i][2], skin.NodeR[i][3] },
                        ["scale"] = new object[] { skin.NodeS[i][0], skin.NodeS[i][1], skin.NodeS[i][2] }
                    };
                    var kids = new List<object>();
                    for (int c = 0; c < skin.JointCount; c++) if (skin.Parent[c] == i) kids.Add(jointBase + c);
                    if (kids.Count > 0) jn["children"] = kids.ToArray();
                    jn["name"] = "joint" + i;
                    nodes.Add(jn);
                }
                var jointList = new object[skin.JointCount];
                for (int i = 0; i < skin.JointCount; i++) jointList[i] = jointBase + i;
                int firstRoot = 0; for (int i = 0; i < skin.JointCount; i++) if (skin.Parent[i] < 0) { firstRoot = i; break; }
                gltfSkin = new object[] { new Dictionary<string, object> { ["inverseBindMatrices"] = ibmAcc, ["joints"] = jointList, ["skeleton"] = jointBase + firstRoot } };
                sceneNodes = new object[] { 0 };
            }
            else
            {
                nodes.Add(new Dictionary<string, object> { ["mesh"] = 0, ["rotation"] = upRotation });
                sceneNodes = new object[] { 0 };
            }

            // ----- animations: one glTF animation per clip; per track, samplers feeding the
            // joint node's translation/rotation/(scale) from the keyframe TRS over time. -----
            object[] animations = null;
            if (hasSkin && skin.Clips.Count > 0)
            {
                const int jointBase = 2;
                var animList = new List<object>();
                foreach (var clip in skin.Clips)
                {
                    var samplers = new List<object>();
                    var channels = new List<object>();
                    foreach (var tr in clip.Tracks)
                    {
                        int node = jointBase + tr.Joint;
                        int kc = tr.Times.Length;
                        // shared time input accessor for this track
                        int bvT = bufferViews.Count; bufferViews.Add(new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = tr.TimesOffset, ["byteLength"] = kc * 4 });
                        int inAcc = accessors.Count; accessors.Add(new Dictionary<string, object> { ["bufferView"] = bvT, ["componentType"] = COMP_FLOAT, ["count"] = kc, ["type"] = "SCALAR", ["min"] = new object[] { tr.TimeMin }, ["max"] = new object[] { tr.TimeMax } });

                        Action<int, int, string, string> addChannel = (off, comps, type, path) =>
                        {
                            int bv = bufferViews.Count; bufferViews.Add(new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = off, ["byteLength"] = kc * comps * 4 });
                            int outAcc = accessors.Count; accessors.Add(new Dictionary<string, object> { ["bufferView"] = bv, ["componentType"] = COMP_FLOAT, ["count"] = kc, ["type"] = type });
                            int si = samplers.Count; samplers.Add(new Dictionary<string, object> { ["input"] = inAcc, ["output"] = outAcc, ["interpolation"] = "LINEAR" });
                            channels.Add(new Dictionary<string, object> { ["sampler"] = si, ["target"] = new Dictionary<string, object> { ["node"] = node, ["path"] = path } });
                        };
                        addChannel(tr.TOffset, 3, "VEC3", "translation");
                        addChannel(tr.ROffset, 4, "VEC4", "rotation");
                        if (tr.HasScale) addChannel(tr.SOffset, 3, "VEC3", "scale");
                    }
                    animList.Add(new Dictionary<string, object> { ["name"] = clip.Name, ["samplers"] = samplers.ToArray(), ["channels"] = channels.ToArray() });
                }
                animations = animList.ToArray();
            }

            var gltf = new Dictionary<string, object>
            {
                ["asset"] = new Dictionary<string, object> { ["version"] = "2.0", ["generator"] = "SimCityPak glTF exporter" },
                ["scene"] = 0,
                ["scenes"] = new object[] { new Dictionary<string, object> { ["nodes"] = sceneNodes } },
                ["nodes"] = nodes.ToArray(),
                ["meshes"] = new object[] { new Dictionary<string, object> { ["primitives"] = new object[] { primitive } } },
                ["buffers"] = new object[] { new Dictionary<string, object> { ["byteLength"] = bufferLength } },
                ["bufferViews"] = bufferViews.ToArray(),
                ["accessors"] = accessors.ToArray()
            };
            if (hasSkin) gltf["skins"] = gltfSkin;
            if (animations != null) gltf["animations"] = animations;

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
