using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Microsoft.Xna.Framework.Graphics;
using SporeMaster.RenderWare4;
using System.Windows.Interop;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using Microsoft.Win32;
using System.IO;
using Microsoft.Xna.Framework;

namespace SimCityPak
{
    /// <summary>
    /// Interaction logic for ViewMesh.xaml
    /// </summary>
    public partial class ViewMesh : UserControl
    {
        public ViewMesh()
        {
            InitializeComponent();




        }

        private BoundingBoxVisual3D _boundingBox;
        private int _meshColorToken;   // bumped per selection so a stale background colour-resolve is ignored

        private void UserControl_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (this.DataContext != null && this.DataContext.GetType() == typeof(RW4ModelSectionView))
            {
                matDiffuseMain.Brush = new SolidColorBrush(Colors.LightGray);
                RW4ModelSectionView sectionView = this.DataContext as RW4ModelSectionView;

               // listBoxTextures.ItemsSource = sectionView.Model.Sections.Where<RW4Section>(s => s.TypeCode == SectionTypeCodes.Texture);

                RW4Section section = sectionView.Section;
                SporeMaster.RenderWare4.RW4Mesh mesh = section.obj as SporeMaster.RenderWare4.RW4Mesh;

                meshMain.TriangleIndices.Clear();
                meshMain.Positions.Clear();
                meshMain.Normals.Clear();
                meshMain.TextureCoordinates.Clear();


                if (_boundingBox != null)
                {
                    viewPort.Children.Remove(_boundingBox);
                    _boundingBox = null;
                }


                RW4Section bboxSection = sectionView.Model.Sections.FirstOrDefault<RW4Section>(s => s.TypeCode == SectionTypeCodes.BBox);

                try
                {
                    if (mesh != null)
                    {
                        // Build positions for EVERY vertex (never throw on a missing component, or the
                        // whole mesh aborts before triangles are added -> an empty, invisible mesh).
                        // Normals / UVs are collected in parallel and only applied if present on all
                        // vertices (WPF requires their count to equal the position count, or nothing
                        // renders).
                        var normalsTmp = new System.Collections.Generic.List<Vector3D>();
                        var uvsTmp = new System.Collections.Generic.List<System.Windows.Point>();
                        bool allNormals = true, allUvs = true;
                        foreach (var v in mesh.vertices.vertices)
                        {
                            var posC = v.VertexComponents.FirstOrDefault(vc => vc.Usage == D3DDECLUSAGE.D3DDECLUSAGE_POSITION) as VertexFloat3Value;
                            meshMain.Positions.Add(posC != null ? new Point3D(posC.X, posC.Y, posC.Z) : new Point3D());

                            var normalC = v.VertexComponents.FirstOrDefault(vc => vc.Usage == D3DDECLUSAGE.D3DDECLUSAGE_NORMAL);
                            var nb = normalC as VertexUByte4Value; var nf = normalC as VertexFloat3Value;
                            if (nb != null) normalsTmp.Add(new Vector3D((nb.X - 127.5f) / 127.5f, (nb.Y - 127.5f) / 127.5f, (nb.Z - 127.5f) / 127.5f));
                            else if (nf != null) normalsTmp.Add(new Vector3D(nf.X, nf.Y, nf.Z));
                            else { normalsTmp.Add(new Vector3D(0, 0, 1)); allNormals = false; }

                            var tcC = v.VertexComponents.FirstOrDefault(vc => vc.Usage == D3DDECLUSAGE.D3DDECLUSAGE_TEXCOORD);
                            var t2 = tcC as VertexFloat2Value; var t4 = tcC as VertexFloat4Value;
                            if (t2 != null) uvsTmp.Add(new System.Windows.Point(t2.X, t2.Y));
                            else if (t4 != null) uvsTmp.Add(new System.Windows.Point(t4.X, t4.Y));
                            else { uvsTmp.Add(new System.Windows.Point()); allUvs = false; }
                        }
                        if (allNormals) foreach (var n in normalsTmp) meshMain.Normals.Add(n);
                        if (allUvs) foreach (var p in uvsTmp) meshMain.TextureCoordinates.Add(p);
                        foreach (var t in mesh.triangles.triangles)
                        {
                            meshMain.TriangleIndices.Add((int)t.i);
                            meshMain.TriangleIndices.Add((int)t.j);
                            meshMain.TriangleIndices.Add((int)t.k);
                        }

                        // Colour/texture the preview. WPF has no custom shaders: facade buildings index
                        // a slot-4 tint palette by vertex COLOR (palette = texture, palette coordinate
                        // = UV); other models use their resolved diffuse. Robustness rules: (1) the
                        // mesh ALWAYS renders — start from solid grey and only swap in a texture when
                        // the UVs match the position count AND are sane (facade FLOAT4 world coords are
                        // huge and would scramble a flat texture); (2) resolving textures can scan the
                        // game folder, so do it on a background thread and apply via the dispatcher,
                        // leaving the grey mesh visible meanwhile.
                        matDiffuseMain.Brush = new SolidColorBrush(Colors.LightGray);
                        try
                        {
                            var texDirs = SimCityPak.PackageReader.DatabaseManager.Instance.PackageFiles
                                .Where(p => p != null && p.packageFileInfo != null && p.packageFileInfo.Directory != null)
                                .Select(p => p.packageFileInfo.Directory.FullName).Distinct().ToList();
                            var indices = SimCityPak.PackageReader.DatabaseManager.Instance.Indices;
                            int vCount = meshMain.Positions.Count;
                            var meshRef = mesh;
                            int token = ++_meshColorToken;

                            bool existingUvSane = meshMain.TextureCoordinates.Count == vCount;
                            if (existingUvSane)
                                foreach (var p in meshMain.TextureCoordinates)
                                    if (Math.Abs(p.X) > 8 || Math.Abs(p.Y) > 8) { existingUvSane = false; break; }

                            System.Threading.Tasks.Task.Run(() =>
                            {
                                try
                                {
                                    int pw, ph;
                                    byte[] palRgba = Cli.CliRunner.GetFacadePaletteRgba(meshRef, indices, texDirs, out pw, out ph);
                                    byte[] png = (palRgba == null && existingUvSane) ? Cli.CliRunner.GetModelBaseColorPng(meshRef, indices, texDirs) : null;

                                    System.Collections.Generic.List<System.Windows.Point> palUvs = null;
                                    if (palRgba != null && pw > 0 && ph > 0)
                                    {
                                        palUvs = new System.Collections.Generic.List<System.Windows.Point>(vCount);
                                        var verts = meshRef.vertices.vertices;
                                        for (int vi = 0; vi < vCount && vi < verts.Length; vi++)
                                        {
                                            float cr = 0, cg = 0;
                                            var col = verts[vi].VertexComponents.FirstOrDefault(c => c.Usage == D3DDECLUSAGE.D3DDECLUSAGE_COLOR) as VertexD3DColorValue;
                                            if (col != null) { cr = col.R / 255f; cg = col.G / 255f; }
                                            palUvs.Add(new System.Windows.Point(cr, cg));
                                        }
                                    }

                                    Dispatcher.Invoke(() =>
                                    {
                                        if (token != _meshColorToken) return;   // a different mesh is selected now
                                        if (palRgba != null && palUvs != null && palUvs.Count == meshMain.Positions.Count)
                                        {
                                            var palBmp = new WriteableBitmap(pw, ph, 96, 96, PixelFormats.Bgra32, null);
                                            byte[] bgra = new byte[pw * ph * 4];
                                            for (int i = 0; i < pw * ph; i++) { bgra[i * 4] = palRgba[i * 4 + 2]; bgra[i * 4 + 1] = palRgba[i * 4 + 1]; bgra[i * 4 + 2] = palRgba[i * 4]; bgra[i * 4 + 3] = 255; }
                                            palBmp.WritePixels(new Int32Rect(0, 0, pw, ph), bgra, pw * 4, 0);
                                            palBmp.Freeze();
                                            meshMain.TextureCoordinates = new System.Windows.Media.PointCollection(palUvs);
                                            matDiffuseMain.Brush = new ImageBrush(palBmp) { Stretch = Stretch.Fill };
                                        }
                                        else if (png != null && meshMain.TextureCoordinates.Count == meshMain.Positions.Count)
                                        {
                                            var bmp = new BitmapImage();
                                            using (var ms = new MemoryStream(png))
                                            { bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = ms; bmp.EndInit(); }
                                            bmp.Freeze();
                                            matDiffuseMain.Brush = new ImageBrush(bmp) { Stretch = Stretch.Fill };
                                        }
                                    });
                                }
                                catch (Exception ex) { Logger.Exception("ViewMesh colour worker", ex); }
                            });
                        }
                        catch (Exception ex) { Logger.Exception("ViewMesh facade colour", ex); }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Exception("ViewMesh.DataContextChanged (3D mesh preview)", ex);
                }
                viewPort.ZoomExtents();
            }
        }

        private void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
        {

        }

        private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
        {

        }

        private void OnViewportMouseMove(object sender, MouseEventArgs e)
        {

        }

        private void Zoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            //double s = -Math.Pow(2.0, -Zoom.Value);
            // camMain.Position = new Point3D(camMain.LookDirection.X * s, camMain.LookDirection.Y * s, camMain.LookDirection.Z * s);
        }

        private void Zoom_ValueChanged_1(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // RotateTransform3D tranf = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), Rotate.Value));
            // Model.Transform = tranf;
        }

        private void listBoxTextures_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            //get the texture
           /* if (listBoxTextures.SelectedItem != null)
            {
                RW4Section textureSection = (RW4Section)listBoxTextures.SelectedItem;
                try
                {
                    SporeMaster.RenderWare4.Texture tex = textureSection.obj as SporeMaster.RenderWare4.Texture;
                    if (tex != null)
                    {
                        ImageBrush brush = new ImageBrush(tex.ToImage());
                        matDiffuseMain.Brush = brush;
                    }
                }
                catch
                {

                }
            }*/
        }

        private void btnExport_Click(object sender, RoutedEventArgs e)
        {
            
        }


        private void TangentSolver(RW4Mesh mesh)
        {
            int vertexCount = mesh.vertices.vertices.Length;
            SporeMaster.RenderWare4.VertexBuffer vertices = mesh.vertices.vertices;
            Buffer<Triangle> triangles = mesh.triangles.triangles;

            Vector3[] tan1 = new Vector3[vertices.Length];
            Vector3[] tan2 = new Vector3[vertices.Length];

            int triangleCount = mesh.triangles.triangles.Length;
            for (int i = 0; i < triangleCount; i += 3)
            {
                Triangle i1 = triangles[i];

                Vertex v1 = vertices[(int)i1.i];
                Vertex v2 = vertices[(int)i1.j];
                Vertex v3 = vertices[(int)i1.k];

                var x1 = v2.Position.X - v1.Position.X;
                var x2 = v3.Position.X - v1.Position.X;
                var y1 = v2.Position.Y - v1.Position.Y;
                var y2 = v3.Position.Y - v1.Position.Y;
                var z1 = v2.Position.Z - v1.Position.Z;
                var z2 = v3.Position.Z - v1.Position.Z;

                var s1 = v2.TextureCoordinates.X - v1.TextureCoordinates.X;
                var s2 = v3.TextureCoordinates.X - v1.TextureCoordinates.X;
                var t1 = v2.TextureCoordinates.Y - v1.TextureCoordinates.Y;
                var t2 = v3.TextureCoordinates.Y - v1.TextureCoordinates.Y;

                float r = 1.0f / (s1 * t2 - s2 * t1);

                var sdir = new Vector3((t2 * x1 - t1 * x2) * r, (t2 * y1 - t1 * y2) * r, (t2 * z1 - t1 * z2) * r);
                var tdir = new Vector3((s1 * x2 - s2 * x1) * r, (s1 * y2 - s2 * y1) * r, (s1 * z2 - s2 * z1) * r);

                tan1[i1.i] += sdir;
                tan1[i1.j] += sdir;
                tan1[i1.k] += sdir;

                tan2[i1.i] += tdir;
                tan2[i1.j] += tdir;
                tan2[i1.k] += tdir;
            }


            for (int i = 0; i < mesh.vertices.vertices.Length; i++)
            {

                Vector3 normal = new Vector3(mesh.vertices.vertices[i].Normal.X,
                    mesh.vertices.vertices[i].Normal.Y,
                    mesh.vertices.vertices[i].Normal.Z);

                // Gram-Schmidt orthogonalize  
                Vector3 tangent = tan1[i] - normal * Vector3.Dot(normal, tan1[i]);
                tangent.Normalize();

                // Calculate handedness (here maybe you need to switch >= with <= depend on the geometry winding order)  
                float tangentdir = (Vector3.Dot(Vector3.Cross(normal, tan1[i]), tan2[i]) >= 0.0f) ? 1.0f : -1.0f;
                Vector3 binormal = Vector3.Cross(normal, tangent) * tangentdir;

                Vertex v = mesh.vertices.vertices[i];

                VertexUByte4Value tangentValue = (VertexUByte4Value)v.VertexComponents.First(c => c.Usage == D3DDECLUSAGE.D3DDECLUSAGE_TANGENT && c.DeclarationType == D3DDECLTYPE.D3DDECLTYPE_UBYTE4);
                tangentValue.X = (byte)(tangent.X * 127.5F + 127.5F);
                tangentValue.Y = (byte)(tangent.Y * 127.5F + 127.5F);
                tangentValue.Z = (byte)(tangent.Z * 127.5F + 127.5F);
                tangentValue.W = (byte)255;

                mesh.vertices.vertices[i] = v;
            }

        }
    
       private void mnuExportOBJ_Click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog dlg = new SaveFileDialog();
            dlg.DefaultExt = ".obj";
            dlg.Filter = "WaveFront .OBJ|*.obj";
            dlg.FileName = MainWindow.SelectedExportName;   // localized name of the selected resource
            if (dlg.ShowDialog().GetValueOrDefault(false))
            using (MainWindow.Busy("Exporting OBJ…"))
            {
                if (this.DataContext != null && this.DataContext.GetType() == typeof(RW4ModelSectionView))
                {
                    matDiffuseMain.Brush = new SolidColorBrush(Colors.LightGray);
                    RW4ModelSectionView sectionView = this.DataContext as RW4ModelSectionView;

                   // listBoxTextures.ItemsSource = sectionView.Model.Sections.Where<RW4Section>(s => s.TypeCode == SectionTypeCodes.Texture);

                    RW4Section section = sectionView.Section;
                    RW4Mesh mesh = section.obj as RW4Mesh;
                    mesh.Export(dlg.FileName);
                }
            }
        }

        private void mnuImportCollada_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.DefaultExt = ".dae";
            dlg.Filter = "COLLADA|*.dae";
            if (dlg.ShowDialog().GetValueOrDefault(false))
            using (MainWindow.Busy("Importing COLLADA…"))
            {
                RW4ModelSectionView sectionView = this.DataContext as RW4ModelSectionView;
                VertexFormat vertexFormatSection = (VertexFormat)sectionView.Model.Sections.First(s => s.TypeCode == SectionTypeCodes.VertexFormat).obj;

                RW4Section section = sectionView.Section;
                SporeMaster.RenderWare4.RW4Mesh meshSection = section.obj as SporeMaster.RenderWare4.RW4Mesh;

                IConverter converter = new AdvancedColladaConverter();

                RW4Mesh newMesh = converter.Import(meshSection, dlg.FileName);

                meshSection.vertices.vertices.section.obj = newMesh.vertices.vertices;
                meshSection.triangles.triangles.section.obj = newMesh.triangles.triangles;

                this.DataContext = sectionView;

                section.Changed();
            }
        }

        private void mnuImportOBJ_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.DefaultExt = ".obj";
            dlg.Filter = "Wavefront .OBJ|*.obj";
            if (dlg.ShowDialog().GetValueOrDefault(false))
            using (MainWindow.Busy("Importing OBJ…"))
            {
                RW4ModelSectionView sectionView = this.DataContext as RW4ModelSectionView;
                VertexFormat vertexFormatSection = (VertexFormat)sectionView.Model.Sections.First(s => s.TypeCode == SectionTypeCodes.VertexFormat).obj;

                RW4Section section = sectionView.Section;
                SporeMaster.RenderWare4.RW4Mesh meshSection = section.obj as SporeMaster.RenderWare4.RW4Mesh;

                IConverter converter = new WaveFrontOBJConverter();

                RW4Mesh newMesh = converter.Import(meshSection, dlg.FileName);

                meshSection.vertices.vertices.section.obj = newMesh.vertices.vertices;
                meshSection.triangles.triangles.section.obj = newMesh.triangles.triangles;

                this.DataContext = sectionView;

                section.Changed();
            }
        }

        private void mnuRecalculateTangent_Click(object sender, RoutedEventArgs e)
        {
            RW4ModelSectionView sectionView = this.DataContext as RW4ModelSectionView;
            RW4Section section = sectionView.Section;

            SporeMaster.RenderWare4.RW4Mesh meshSection = section.obj as SporeMaster.RenderWare4.RW4Mesh;

            TangentSolver(meshSection);

            section.Changed();
        }
    }
}
