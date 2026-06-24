using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SimCityPak.Cli;

namespace SimCityPak
{
    /// <summary>
    /// Viewer for EA VP60 video resources (type id 0x376840D7). These resources are
    /// tens of MB and cannot be previewed in-app, so this view just shows metadata and
    /// offers to export them: raw .vp6 (plays in ffmpeg-based players such as VLC) or a
    /// re-encoded .mp4 (requires ffmpeg on PATH). Routed here by ViewSelector instead of
    /// the hex view, which used to OutOfMemory on these.
    /// </summary>
    public partial class ViewVideo : UserControl
    {
        private byte[] _data;
        private string _baseName = "video";

        public ViewVideo()
        {
            InitializeComponent();
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            var index = this.DataContext as DatabaseIndexData;
            if (index == null) return;

            _data = index.Data;
            _baseName = SanitizeFileName(index.Index.InstanceId.ToHex());

            txtInfo.Text = DescribeVideo(_data, index);
            txtStatus.Text = "";

            // MP4 export only makes sense if ffmpeg is reachable.
            btnExportMp4.IsEnabled = CliRunner.FindFfmpeg() != null;
            if (!btnExportMp4.IsEnabled)
                btnExportMp4.ToolTip = "ffmpeg was not found on PATH (or in Tools\\ffmpeg next to the app).";
        }

        private static string DescribeVideo(byte[] data, DatabaseIndexData index)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Instance:  " + index.Index.InstanceId.ToHex());
            sb.AppendLine("Size:      " + string.Format("{0:N0} bytes ({1:N1} MB)",
                data == null ? 0 : data.Length, (data == null ? 0 : data.Length) / 1024.0 / 1024.0));

            // EA "MVhd" container: "MVhd"(4) size(4) codec-fourcc(4) width(u16 LE) height(u16 LE)...
            if (data != null && data.Length >= 16 &&
                data[0] == (byte)'M' && data[1] == (byte)'V' && data[2] == (byte)'h' && data[3] == (byte)'d')
            {
                string codec = Encoding.ASCII.GetString(data, 8, 4);
                int width = data[12] | (data[13] << 8);
                int height = data[14] | (data[15] << 8);
                sb.AppendLine("Container: EA MVhd");
                sb.AppendLine("Codec:     " + codec);
                sb.AppendLine("Resolution:" + string.Format(" {0} x {1}", width, height));
            }
            return sb.ToString().TrimEnd();
        }

        private void btnSaveRaw_Click(object sender, RoutedEventArgs e)
        {
            if (_data == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = _baseName + ".vp6",
                Filter = "EA VP6 video (*.vp6)|*.vp6|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                File.WriteAllBytes(dlg.FileName, _data);
                txtStatus.Text = "Saved raw video: " + dlg.FileName;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to save: " + ex.Message, "Export failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void btnExportMp4_Click(object sender, RoutedEventArgs e)
        {
            if (_data == null) return;
            string ffmpeg = CliRunner.FindFfmpeg();
            if (ffmpeg == null)
            {
                MessageBox.Show("ffmpeg was not found on PATH (or in Tools\\ffmpeg next to the app).",
                    "ffmpeg not found", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = _baseName + ".mp4",
                Filter = "MP4 video (*.mp4)|*.mp4|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog() != true) return;

            string outPath = dlg.FileName;
            byte[] data = _data;
            SetBusy(true, "Transcoding to MP4… this can take a while for long videos.");

            Task.Run(() =>
            {
                string tmp = Path.Combine(Path.GetTempPath(),
                    "scp_video_" + Guid.NewGuid().ToString("N") + ".vp6");
                string error;
                bool ok;
                try
                {
                    File.WriteAllBytes(tmp, data);
                    ok = CliRunner.TranscodeVideoToMp4(ffmpeg, tmp, outPath, out error);
                }
                catch (Exception ex) { ok = false; error = ex.Message; }
                finally { try { File.Delete(tmp); } catch { } }

                Dispatcher.Invoke(() =>
                {
                    SetBusy(false, null);
                    if (ok)
                        txtStatus.Text = "Exported MP4: " + outPath;
                    else
                        MessageBox.Show("ffmpeg failed: " + error, "Export failed",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                });
            });
        }

        private void SetBusy(bool busy, string status)
        {
            btnSaveRaw.IsEnabled = !busy;
            btnExportMp4.IsEnabled = !busy && CliRunner.FindFfmpeg() != null;
            Mouse.OverrideCursor = busy ? Cursors.Wait : null;
            if (status != null) txtStatus.Text = status;
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "video";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }
}
