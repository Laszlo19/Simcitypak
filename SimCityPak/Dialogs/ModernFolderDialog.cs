using System;
using System.Runtime.InteropServices;

namespace SimCityPak
{
    /// <summary>
    /// A modern (Windows Vista+ <c>IFileOpenDialog</c>) folder picker, matching the style of the
    /// WPF file dialogs used to open packages. .NET Framework's WinForms FolderBrowserDialog still
    /// shows the old tree-view, so we drive the shell dialog directly via COM (no extra package).
    /// </summary>
    internal static class ModernFolderDialog
    {
        /// <summary>Shows the folder picker. Returns false if the user cancelled.</summary>
        public static bool TryPick(string title, out string path)
        {
            path = null;
            NativeFileOpenDialog dialog = null;
            try
            {
                dialog = (NativeFileOpenDialog)Activator.CreateInstance(
                    Type.GetTypeFromCLSID(new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7"))); // CLSID_FileOpenDialog
                uint opts;
                dialog.GetOptions(out opts);
                dialog.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);
                if (!string.IsNullOrEmpty(title)) dialog.SetTitle(title);

                const int S_OK = 0;
                if (dialog.Show(IntPtr.Zero) != S_OK) return false;   // cancelled

                IShellItem item;
                dialog.GetResult(out item);
                try { item.GetDisplayName(SIGDN_FILESYSPATH, out path); }
                finally { Marshal.ReleaseComObject(item); }
                return !string.IsNullOrEmpty(path);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (dialog != null) Marshal.ReleaseComObject(dialog);
            }
        }

        private const uint FOS_PICKFOLDERS = 0x00000020;
        private const uint FOS_FORCEFILESYSTEM = 0x00000040;
        private const uint SIGDN_FILESYSPATH = 0x80058000;

        // IFileOpenDialog (IID D57C7288-D4AD-4768-BE02-9D969532D960). Methods are declared in exact
        // vtable order — IModalWindow then IFileDialog — through GetResult (all we call); later
        // members are omitted because we never invoke them. Unused params are IntPtr to keep the
        // signatures stack-compatible without marshalling types we don't need.
        [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface NativeFileOpenDialog
        {
            // IModalWindow
            [PreserveSig] int Show(IntPtr parent);
            // IFileDialog
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }
    }
}
