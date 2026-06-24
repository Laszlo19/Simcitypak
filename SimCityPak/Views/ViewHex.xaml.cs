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
using System.Globalization;
using SporeMaster.RenderWare4;
using System.IO;
using Gibbed.Spore.Package;

namespace SimCityPak
{
    /// <summary>
    /// Interaction logic for ViewHex.xaml
    /// </summary>
    public partial class ViewHex : UserControl
    {
        public ViewHex()
        {
            InitializeComponent();
        }

        public event EventHandler DataChangedHandler;
        private void DataChanged()
        {
            if (DataChangedHandler != null) DataChangedHandler(this, new EventArgs());
        }

        // Building a hex string for the whole resource is O(n) in both time and
        // (worse) allocations: BitConverter.ToString + SplitIntoChunks produces one
        // small string per row, so a 90 MB resource (e.g. an EA VP60 video) yields
        // millions of objects and a multi-hundred-MB string -> OutOfMemoryException
        // and a cascade of error dialogs. Cap what we render; the full bytes are
        // still available via Save/Export.
        private const int MaxRenderBytes = 256 * 1024;

        /// <summary>True when the displayed text is only the first MaxRenderBytes of a
        /// larger resource (so editing/saving it back would corrupt the resource).</summary>
        private bool _truncated;

        private void Render(byte[] data)
        {
            if (data == null) { textBoxHex.Text = ""; textBoxRawData.Text = ""; _truncated = false; return; }

            _truncated = data.Length > MaxRenderBytes;
            byte[] shown = data;
            if (_truncated)
            {
                shown = new byte[MaxRenderBytes];
                Array.Copy(data, shown, MaxRenderBytes);
            }

            ByteArrayToIndexedHexStringConverter converter = new ByteArrayToIndexedHexStringConverter();
            string hex = (string)converter.Convert(shown, typeof(string), "8", CultureInfo.CurrentCulture);
            string ascii = System.Text.ASCIIEncoding.ASCII.GetString(shown);

            if (_truncated)
            {
                string note = string.Format(
                    "[ Showing the first {0:N0} of {1:N0} bytes. This resource is too large to display in" +
                    " full; use Save/Export to get the complete data. ]{2}{2}",
                    MaxRenderBytes, data.Length, Environment.NewLine);
                hex = note + hex;
                ascii = note + ascii;
            }

            textBoxHex.Text = hex;
            textBoxRawData.Text = ascii;
        }

        private void Grid_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (this.DataContext != null)
            {
                if (this.DataContext.GetType() == typeof(DatabaseIndexData))
                {
                    DatabaseIndexData index = (DatabaseIndexData)this.DataContext;
                    Render(index.Data);
                }
                else if (this.DataContext.GetType() == typeof(byte[]))
                {
                    Render((byte[])this.DataContext);
                }
                else if (this.DataContext.GetType() == typeof(RW4Section))
                {
                   // RW4Section section = (RW4Section)this.DataContext;
                   // ByteArrayToIndexedHexStringConverter converter = new ByteArrayToIndexedHexStringConverter();
                   // textBoxHex.Text = (string)converter.Convert(section.obj.Data, typeof(string), "8", CultureInfo.CurrentCulture);
                   // textBoxRawData.Text = System.Text.ASCIIEncoding.ASCII.GetString(section.obj);

                }
            }
        }

        private void btnSave_Click(object sender, RoutedEventArgs e)
        {
            if (_truncated)
            {
                MessageBox.Show(
                    "This resource is too large to display in full, so only the first part is shown. " +
                    "Saving would overwrite the resource with the truncated text. Use Export instead.",
                    "Cannot save a truncated view", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (this.DataContext != null)
            {
                if (this.DataContext.GetType() == typeof(DatabaseIndexData))
                { 
                    DatabaseIndexData index = (DatabaseIndexData)this.DataContext;
                   
                    int i = 0;
                    StringReader reader = new StringReader(textBoxHex.Text);
                    while (reader.Peek() != -1)
                    {
                        string line = reader.ReadLine();
                        string[] values = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        for (int j = 0; j < 8; j++)
                        {
                            (index.Data as byte[])[i + j] = byte.Parse(values[j + 1].ToString(), NumberStyles.AllowHexSpecifier);
                        }
                        i += 8;
                    }


                    index.Index.ModifiedData = new ModifiedGenericFile() { FileData = index.Data };
                    index.Index.IsModified = true;
                   // DataChanged();

                  //
                   // ByteArrayToIndexedHexStringConverter converter = new ByteArrayToIndexedHexStringConverter();
                  //  textBoxHex.Text = (string)converter.Convert(index.Data, typeof(string), "8", CultureInfo.CurrentCulture);
                  //  textBoxRawData.Text = System.Text.ASCIIEncoding.ASCII.GetString(index.Data);
                }
                if (this.DataContext.GetType() == typeof(byte[]))
                {
                    byte[] data = this.DataContext as byte[];

                    int i = 0;
                    StringReader reader = new StringReader(textBoxHex.Text);
                    while (reader.Peek() != -1)
                    {
                        string line = reader.ReadLine();
                        string[] values = line.Split(new char[]{ ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        for (int j = 0; j < 8; j++)
                        {
                            if (j + 1 < values.Length)
                            {
                                (this.DataContext as byte[])[i + j] = byte.Parse(values[j + 1].ToString(), NumberStyles.AllowHexSpecifier);
                            }
                        }
                        i += 8;
                    }

                    DataChanged();

                }
            }
        }

        private void btnCopyToClipboard_Click(object sender, RoutedEventArgs e)
        {
            Clipboard.SetText(textBoxHex.Text, TextDataFormat.Text);
        }
    }
}
