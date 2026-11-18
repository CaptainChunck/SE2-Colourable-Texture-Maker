using System;
using System.Runtime.InteropServices;

namespace SE2ColourableTextureMaker
{
    /// <summary>Windows' own modern "Select Folder" browser (the Explorer-style dialog), without WinForms.</summary>
    public static class FolderPicker
    {
        public static string Pick(IntPtr owner, string title, string startFolder, string okLabel = "Select Folder")
        {
            var dlg = (IFileOpenDialog)new FileOpenDialogRCW();
            try
            {
                dlg.GetOptions(out uint opts);
                dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
                dlg.SetTitle(title);
                dlg.SetOkButtonLabel(okLabel);
                if (!string.IsNullOrEmpty(startFolder) && System.IO.Directory.Exists(startFolder))
                {
                    var iid = typeof(IShellItem).GUID;
                    if (SHCreateItemFromParsingName(startFolder, IntPtr.Zero, ref iid, out IShellItem item) == 0) dlg.SetFolder(item);
                }
                int hr = dlg.Show(owner);
                if (hr != 0) return null;
                dlg.GetResult(out IShellItem result);
                result.GetDisplayName(SIGDN_FILESYSPATH, out IntPtr p);
                try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
            }
            finally { Marshal.ReleaseComObject(dlg); }
        }

        const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_PATHMUSTEXIST = 0x800;
        const uint SIGDN_FILESYSPATH = 0x80058000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItem item);

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialogRCW { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint c, IntPtr spec);
            void SetFileTypeIndex(uint i);
            void GetFileTypeIndex(out uint i);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint fos);
            void GetOptions(out uint fos);
            void SetDefaultFolder(IShellItem si);
            void SetFolder(IShellItem si);
            void GetFolder(out IShellItem si);
            void GetCurrentSelection(out IShellItem si);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem si);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint sigdn, out IntPtr name);
            void GetAttributes(uint mask, out uint attribs);
            void Compare(IShellItem other, uint hint, out int order);
        }
    }
}
